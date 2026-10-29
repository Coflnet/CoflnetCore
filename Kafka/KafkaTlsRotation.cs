using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;
using Coflnet.Core.Security;
using Confluent.Kafka;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Coflnet.Kafka;

// One watcher per running opt-in consumer. Private PEM snapshots never leave this process.
internal sealed class KafkaTlsRotation : IAsyncDisposable
{
    private readonly Channel<(long Version, Snapshot? Material)> changes = Channel.CreateBounded<(long, Snapshot?)>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly CancellationTokenSource stopped = new();
    private readonly List<FileSystemWatcher> watchers = new();
    private readonly object gate = new();
    private readonly IConfiguration section;
    private readonly Func<ClientConfig, Task> preflight;
    private readonly ClientConfig baseline;
    private readonly ILogger logger;
    private readonly TimeProvider clock;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly HashSet<string> failureTypes = new();
    private string? failureHash;
    private long version;
    private TimeSpan retryDelay = TimeSpan.FromSeconds(30);
    private CancellationTokenSource? retry;
    private Task retryTask = Task.CompletedTask;
    private CancellationTokenSource rotate = new();
    private Snapshot current = null!;
    private Snapshot? pending;
    private Task worker = Task.CompletedTask;

    private KafkaTlsRotation(IConfiguration configuration, ClientConfig baseline,
        ILogger logger, Func<ClientConfig, Task> preflight, TimeProvider clock, Func<TimeSpan, CancellationToken, Task> delay)
    {
        section = configuration.GetSection("KAFKA:TLS");
        this.baseline = new ClientConfig(baseline.ToDictionary(item => item.Key, item => item.Value));
        this.logger = logger;
        this.preflight = preflight;
        this.clock = clock;
        this.delay = delay;
    }

    private void Watch()
    {
        foreach (var directory in new[] { "CA_LOCATION", "CERTIFICATE_LOCATION", "KEY_LOCATION" }
            .Select(key => Path.GetDirectoryName(Path.GetFullPath(section[key]
                ?? throw new InvalidOperationException("Renewable Kafka TLS requires all three file locations.")))!).Distinct())
        {
            var watcher = new FileSystemWatcher(directory)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
                IncludeSubdirectories = false
            };
            watcher.Changed += (_, _) => SignalChange();
            watcher.Created += (_, _) => SignalChange();
            watcher.Deleted += (_, _) => SignalChange();
            watcher.Renamed += (_, _) => SignalChange();
            watcher.Error += (_, _) => SignalChange();
            watchers.Add(watcher);
            watcher.EnableRaisingEvents = true;
        }
    }

    internal void SignalChange()
    {
        lock (gate)
        {
            if (stopped.IsCancellationRequested) return;
            retry?.Cancel();
            retryDelay = TimeSpan.FromSeconds(30);
            changes.Writer.TryWrite((++version, null));
        }
    }

    internal static async Task<KafkaTlsRotation> CreateAsync(IConfiguration configuration, ClientConfig baseline,
        string[] topics, ILogger logger, Func<ClientConfig, Task>? preflight = null, TimeProvider? clock = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null, bool watch = true)
    {
        preflight ??= candidate => Task.Run(() =>
        {
            var config = new AdminClientConfig(candidate.ToDictionary(item => item.Key, item => item.Value));
            using var admin = new AdminClientBuilder(config).SetLogHandler((_, _) => { }).Build();
            // An all-topics request cannot auto-create a misspelled/deleted topic.
            var metadata = admin.GetMetadata(TimeSpan.FromSeconds(10));
            if (topics.Any(topic => !metadata.Topics.Any(t => t.Topic == topic && !t.Error.IsError)))
                throw new InvalidOperationException("Kafka metadata preflight failed.");
        });
        var owner = new KafkaTlsRotation(configuration, baseline, logger, preflight, clock ?? TimeProvider.System, delay ?? Task.Delay);
        try
        {
            if (baseline.SecurityProtocol != SecurityProtocol.SaslSsl || string.IsNullOrWhiteSpace(baseline.SaslUsername))
                throw new InvalidOperationException("Renewable Kafka consumers require an unchanged SASL_SSL identity.");
            if (watch) owner.Watch();
            owner.current = owner.ReadSnapshot(0);
            await owner.PreflightAsync(owner.current);
            owner.worker = owner.ObserveAsync();
            return owner;
        }
        catch
        {
            await owner.DisposeAsync();
            throw new InvalidOperationException("Initial renewable Kafka TLS validation/preflight failed; no consumer was started.");
        }
    }

    internal CancellationToken RotationToken { get { lock (gate) return rotate.Token; } }

    internal ConsumerConfig Configure(ConsumerConfig config)
    {
        lock (gate) return Configure(config, current);
    }

    internal bool ShouldRotate()
    {
        lock (gate)
        {
            if (pending is null) return false;
            if (pending.Expires > clock.GetUtcNow().AddMinutes(1)) return true;
            pending = null;
            rotate.Dispose();
            rotate = new();
            logger.LogError("Renewable Kafka TLS candidate expired while draining; retaining the current consumer. Awaiting a new material generation.");
            return false;
        }
    }

    internal Snapshot Current { get { lock (gate) return current; } }
    internal Snapshot? Candidate { get { lock (gate) return pending; } }
    internal bool IsValid(Snapshot snapshot) => snapshot.Expires > clock.GetUtcNow().AddMinutes(1);
    internal bool IsPending(Snapshot snapshot)
    {
        lock (gate) return ReferenceEquals(snapshot, pending) && IsValid(snapshot);
    }

    internal ConsumerConfig Configure(ConsumerConfig config, Snapshot snapshot)
    {
        var result = new ConsumerConfig(config.ToDictionary(item => item.Key, item => item.Value));
        snapshot.Apply(result);
        return result;
    }

    internal void Accept(Snapshot snapshot)
    {
        lock (gate)
        {
            if (!IsValid(snapshot)) throw new AuthenticationException("Kafka candidate expired during handoff.");
            current = snapshot;
            if (pending?.Hash == snapshot.Hash) Reject(pending);
        }
    }

    internal void Reject(Snapshot snapshot)
    {
        lock (gate)
        {
            if (!ReferenceEquals(pending, snapshot)) return;
            pending = null;
            rotate.Dispose();
            rotate = new();
        }
    }

    private async Task PreflightAsync(Snapshot snapshot)
    {
        var candidate = new ClientConfig(baseline.ToDictionary(item => item.Key, item => item.Value));
        snapshot.Apply(candidate);
        await preflight(candidate).WaitAsync(TimeSpan.FromSeconds(60), stopped.Token);
    }

    internal bool ReportOnce(Snapshot snapshot, string failureType)
    {
        lock (gate)
        {
            if (snapshot.Version != version) return false;
            if (failureHash != snapshot.Hash) { failureHash = snapshot.Hash; failureTypes.Clear(); }
            return failureTypes.Add(failureType);
        }
    }

    internal void Retry(Snapshot snapshot)
    {
        lock (gate)
        {
            if (snapshot.Version != version || snapshot.Hash == current.Hash || !IsValid(snapshot) || stopped.IsCancellationRequested) return;
            retry?.Cancel();
            retry?.Dispose();
            retry = CancellationTokenSource.CreateLinkedTokenSource(stopped.Token);
            retryTask = RetryAsync(snapshot, retryDelay, retry.Token);
            retryDelay = TimeSpan.FromSeconds(Math.Min(300, retryDelay.TotalSeconds * 2));
        }
    }

    private async Task RetryAsync(Snapshot snapshot, TimeSpan wait, CancellationToken token)
    {
        try
        {
            await delay(wait, token);
            lock (gate)
                if (!token.IsCancellationRequested && snapshot.Version == version)
                    changes.Writer.TryWrite((snapshot.Version, snapshot));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private async Task ObserveAsync()
    {
        try
        {
            await foreach (var change in changes.Reader.ReadAllAsync(stopped.Token))
            {
                Snapshot? snapshot = change.Material;
                try
                {
                    lock (gate) if (change.Version != version) continue;
                    snapshot ??= ReadSnapshot(change.Version);
                    lock (gate)
                    {
                        if (snapshot.Version != version || snapshot.Hash == current.Hash) continue;
                        if (snapshot.Hash == pending?.Hash) { pending = snapshot; continue; }
                    }
                    if (!IsValid(snapshot)) throw new AuthenticationException("Kafka retry material expired.");
                    await PreflightAsync(snapshot);
                    lock (gate)
                    {
                        if (snapshot.Version != version || !IsValid(snapshot)) continue;
                        pending = snapshot;
                        rotate.Cancel();
                    }
                }
                catch (Exception e) when (!stopped.IsCancellationRequested)
                {
                    if (snapshot is not null) Retry(snapshot);
                    if (snapshot is null || ReportOnce(snapshot, e.GetType().Name))
                        logger.LogError("Renewable Kafka TLS candidate rejected ({FailureType}); retaining the current consumer.", e.GetType().Name);
                }
            }
        }
        catch (OperationCanceledException) when (stopped.IsCancellationRequested) { }
    }

    private Snapshot ReadSnapshot(long generationVersion)
    {
        var ca = File.ReadAllText(section["CA_LOCATION"]!);
        var certificate = File.ReadAllText(section["CERTIFICATE_LOCATION"]!);
        var key = File.ReadAllText(section["KEY_LOCATION"]!);
        var roots = new X509Certificate2Collection();
        var chain = new X509Certificate2Collection();
        try
        {
            roots.ImportFromPem(ca);
            chain.ImportFromPem(certificate);
            using var leaf = X509Certificate2.CreateFromPem(certificate, key);
            if (leaf.Extensions.OfType<X509BasicConstraintsExtension>().Any(e => e.CertificateAuthority) ||
                !leaf.Extensions.OfType<X509EnhancedKeyUsageExtension>().Any(e =>
                    e.EnhancedKeyUsages.Cast<Oid>().Any(oid => oid.Value == "1.3.6.1.5.5.7.3.2")) ||
                leaf.NotAfter.ToUniversalTime() <= clock.GetUtcNow().UtcDateTime.AddMinutes(1) ||
                !PemTlsOptions.ValidateChain(leaf, null, roots, clientCertificate: true, intermediates: chain))
                throw new AuthenticationException("Invalid renewable Kafka client chain or lifetime.");
            return new(ca, certificate, key,
                Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(ca + certificate + key))),
                leaf.NotAfter.ToUniversalTime(), generationVersion);
        }
        finally
        {
            foreach (var item in roots) item.Dispose();
            foreach (var item in chain) item.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var watcher in watchers) watcher.Dispose();
        stopped.Cancel();
        try { await worker; await retryTask; }
        catch (OperationCanceledException) { }
        finally
        {
            retry?.Dispose();
            rotate.Dispose();
            stopped.Dispose();
        }
    }

    internal sealed record Snapshot(string Ca, string Certificate, string Key, string Hash, DateTimeOffset Expires, long Version)
    {
        public override string ToString() => "Kafka TLS snapshot";

        internal void Apply(ClientConfig config)
        {
            config.SslCaLocation = null;
            config.SslCertificateLocation = null;
            config.SslKeyLocation = null;
            config.SslCaPem = Ca;
            config.SslCertificatePem = Certificate;
            config.SslKeyPem = Key;
            config.EnableSslCertificateVerification = true;
            config.SslEndpointIdentificationAlgorithm = SslEndpointIdentificationAlgorithm.Https;
        }
    }
}
