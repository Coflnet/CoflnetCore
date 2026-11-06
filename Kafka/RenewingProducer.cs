using System.Diagnostics;
using Confluent.Kafka;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Coflnet.Kafka;

// Forwards to the current native producer. A validated, preflighted TLS generation gets a freshly built
// producer; the previous one drains its in-flight calls, flushes (bounded by its delivery timeout) and is
// disposed off the hot path.
internal sealed class RenewingProducer<TKey, TValue> : IProducer<TKey, TValue>
{
    private readonly TimeSpan retireFlush;
    private readonly object gate = new();
    private readonly List<string> brokers = new();
    private readonly CancellationTokenSource stopped = new();
    private readonly ProducerConfig config;
    private readonly Func<ProducerConfig, IProducer<TKey, TValue>> build;
    private readonly Func<ClientConfig, Task> preflight;
    private readonly ILogger logger;
    private (string Username, string Password)? credentials;
    private KafkaTlsRotation rotation = null!;
    private Generation current = null!;
    private Task renewing = Task.CompletedTask;
    private Task retiring = Task.CompletedTask;
    private int disposed;

    private RenewingProducer(ProducerConfig config, Func<ProducerConfig, IProducer<TKey, TValue>> build,
        ILogger logger, Func<ClientConfig, Task> preflight)
    {
        this.config = config;
        retireFlush = RetireFlushBound(config);
        this.build = build;
        this.logger = logger;
        this.preflight = preflight;
    }

    internal static RenewingProducer<TKey, TValue> Create(IConfiguration configuration, ClientConfig common, ProducerConfig config,
        Func<ProducerConfig, IProducer<TKey, TValue>> build, ILogger logger, Func<ClientConfig, Task>? preflight = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null, bool watch = true)
    {
        var producer = new RenewingProducer<TKey, TValue>(config, build, logger, preflight ?? KafkaTlsRotation.MetadataPreflight([]));
        // Like a plain producer, startup does not wait for a reachable broker; only renewals are preflighted.
        producer.rotation = KafkaTlsRotation.CreateAsync(configuration, common, [], logger, producer.PreflightAsync,
            delay: delay, watch: watch, initialPreflight: false).GetAwaiter().GetResult();
        try
        {
            producer.current = new Generation(build(producer.rotation.Configure(config, producer.rotation.Current)));
        }
        catch
        {
            producer.rotation.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }
        producer.renewing = Task.Run(producer.RenewAsync);
        return producer;
    }

    // Dispose purges what is still queued, so a retired handle may flush until librdkafka itself would have
    // failed the last queued message: message timeout (default 300 s) plus linger and a small margin.
    internal static TimeSpan RetireFlushBound(ProducerConfig config) =>
        TimeSpan.FromMilliseconds((config.MessageTimeoutMs ?? 300000) + (config.LingerMs ?? 5) + 5000);

    internal KafkaTlsRotation Rotation => rotation;
    internal Task Retiring { get { lock (gate) return retiring; } }

    public Handle Handle => Volatile.Read(ref current).Producer.Handle;
    public string Name => Volatile.Read(ref current).Producer.Name;

    public async Task<DeliveryResult<TKey, TValue>> ProduceAsync(string topic, Message<TKey, TValue> message,
        CancellationToken cancellationToken = default)
    {
        var generation = Enter();
        try { return await generation.Producer.ProduceAsync(topic, message, cancellationToken).ConfigureAwait(false); }
        finally { generation.Exit(); }
    }

    public async Task<DeliveryResult<TKey, TValue>> ProduceAsync(TopicPartition topicPartition, Message<TKey, TValue> message,
        CancellationToken cancellationToken = default)
    {
        var generation = Enter();
        try { return await generation.Producer.ProduceAsync(topicPartition, message, cancellationToken).ConfigureAwait(false); }
        finally { generation.Exit(); }
    }

    public void Produce(string topic, Message<TKey, TValue> message, Action<DeliveryReport<TKey, TValue>>? deliveryHandler = null)
    {
        var generation = Enter();
        try { generation.Producer.Produce(topic, message, deliveryHandler); }
        finally { generation.Exit(); }
    }

    public void Produce(TopicPartition topicPartition, Message<TKey, TValue> message, Action<DeliveryReport<TKey, TValue>>? deliveryHandler = null)
    {
        var generation = Enter();
        try { generation.Producer.Produce(topicPartition, message, deliveryHandler); }
        finally { generation.Exit(); }
    }

    public int Poll(TimeSpan timeout) => Call(producer => producer.Poll(timeout));

    // Also waits, within the same timeout (negative = infinite), for producers retired by a renewal to finish.
    public int Flush(TimeSpan timeout)
    {
        var elapsed = Stopwatch.StartNew();
        var remaining = Call(producer => producer.Flush(timeout));
        WaitForRetired(timeout, elapsed.Elapsed);
        return remaining;
    }

    private void WaitForRetired(TimeSpan timeout, TimeSpan spent)
    {
        if (timeout < TimeSpan.Zero)
        {
            Retiring.Wait();
            return;
        }
        Retiring.Wait(TimeSpan.FromTicks(Math.Max(0, (timeout - spent).Ticks)));
    }

    public void Flush(CancellationToken cancellationToken = default)
    {
        Use(producer => producer.Flush(cancellationToken));
        Retiring.Wait(cancellationToken);
    }

    public void InitTransactions(TimeSpan timeout) => Use(producer => producer.InitTransactions(timeout));
    public void BeginTransaction() => Use(producer => producer.BeginTransaction());
    public void CommitTransaction(TimeSpan timeout) => Use(producer => producer.CommitTransaction(timeout));
    public void CommitTransaction() => Use(producer => producer.CommitTransaction());
    public void AbortTransaction(TimeSpan timeout) => Use(producer => producer.AbortTransaction(timeout));
    public void AbortTransaction() => Use(producer => producer.AbortTransaction());

    public void SendOffsetsToTransaction(IEnumerable<TopicPartitionOffset> offsets, IConsumerGroupMetadata groupMetadata, TimeSpan timeout)
        => Use(producer => producer.SendOffsetsToTransaction(offsets, groupMetadata, timeout));

    public int AddBrokers(string brokers)
    {
        lock (gate)
        {
            if (!this.brokers.Contains(brokers)) this.brokers.Add(brokers);
            return current.Producer.AddBrokers(brokers);
        }
    }

    public void SetSaslCredentials(string username, string password)
    {
        lock (gate)
        {
            credentials = (username, password);
            current.Producer.SetSaslCredentials(username, password);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 1) return;
        stopped.Cancel();
        try { renewing.GetAwaiter().GetResult(); }
        finally
        {
            rotation.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Volatile.Read(ref current).Producer.Dispose();
            stopped.Dispose();
        }
    }

    private Task PreflightAsync(ClientConfig candidate)
    {
        lock (gate)
            if (credentials is { } sasl) (candidate.SaslUsername, candidate.SaslPassword) = sasl;
        return preflight(candidate);
    }

    private async Task RenewAsync()
    {
        while (!stopped.IsCancellationRequested)
        {
            await WaitForCandidateAsync();
            if (stopped.IsCancellationRequested || !rotation.ShouldRotate()) continue;
            if (rotation.Candidate is { } candidate) Swap(candidate);
        }
    }

    private async Task WaitForCandidateAsync()
    {
        using var wake = CancellationTokenSource.CreateLinkedTokenSource(stopped.Token, rotation.RotationToken);
        try { await Task.Delay(Timeout.Infinite, wake.Token); }
        catch (OperationCanceledException) { }
    }

    private void Swap(KafkaTlsRotation.Snapshot candidate)
    {
        IProducer<TKey, TValue>? replacement = null;
        try
        {
            replacement = build(rotation.Configure(config, candidate));
            if (!rotation.IsPending(candidate)) throw new InvalidOperationException("Kafka candidate changed before handoff.");
            Install(replacement, candidate);
            replacement = null;
            logger.LogInformation("Renewable Kafka producer handoff completed; the previous producer drains in the background.");
        }
        catch (Exception e)
        {
            replacement?.Dispose();
            if (stopped.IsCancellationRequested) return;
            rotation.Reject(candidate);
            rotation.Retry(candidate);
            if (rotation.ReportOnce(candidate, e.GetType().Name))
                logger.LogError("Renewable Kafka producer candidate rejected ({FailureType}); current producer retained.", e.GetType().Name);
        }
    }

    private void Install(IProducer<TKey, TValue> replacement, KafkaTlsRotation.Snapshot candidate)
    {
        lock (gate)
        {
            foreach (var list in brokers) replacement.AddBrokers(list);
            if (credentials is { } sasl) replacement.SetSaslCredentials(sasl.Username, sasl.Password);
            rotation.Accept(candidate);
            var previous = Interlocked.Exchange(ref current, new Generation(replacement));
            var retired = RetireAsync(previous);
            retiring = retiring.IsCompleted ? retired : Task.WhenAll(retiring, retired);
        }
    }

    private async Task RetireAsync(Generation previous)
    {
        await previous.Retire().ConfigureAwait(false);
        try
        {
            var undelivered = await Task.Run(() => previous.Producer.Flush(retireFlush)).ConfigureAwait(false);
            if (undelivered > 0)
                logger.LogWarning("Retired Kafka producer still had {Count} undelivered messages after its bounded flush.", undelivered);
        }
        catch (Exception e)
        {
            logger.LogWarning("Retired Kafka producer flush failed ({FailureType}).", e.GetType().Name);
        }
        finally
        {
            DisposeRetired(previous.Producer);
        }
    }

    private void DisposeRetired(IProducer<TKey, TValue> producer)
    {
        try { producer.Dispose(); }
        catch (Exception e) { logger.LogWarning("Retired Kafka producer dispose failed ({FailureType}).", e.GetType().Name); }
    }

    // A caller that observed `current` after registering cannot race retirement: Retire sees its registration.
    private Generation Enter()
    {
        while (true)
        {
            var generation = Volatile.Read(ref current);
            generation.Enter();
            if (ReferenceEquals(generation, Volatile.Read(ref current))) return generation;
            generation.Exit();
        }
    }

    private void Use(Action<IProducer<TKey, TValue>> action)
    {
        var generation = Enter();
        try { action(generation.Producer); }
        finally { generation.Exit(); }
    }

    private T Call<T>(Func<IProducer<TKey, TValue>, T> call)
    {
        var generation = Enter();
        try { return call(generation.Producer); }
        finally { generation.Exit(); }
    }

    private sealed class Generation(IProducer<TKey, TValue> producer)
    {
        private readonly TaskCompletionSource drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int users;
        private int retired;

        internal IProducer<TKey, TValue> Producer { get; } = producer;

        internal void Enter() => Interlocked.Increment(ref users);

        internal void Exit()
        {
            if (Interlocked.Decrement(ref users) == 0 && Volatile.Read(ref retired) == 1) drained.TrySetResult();
        }

        internal Task Retire()
        {
            Interlocked.Exchange(ref retired, 1);
            if (Volatile.Read(ref users) == 0) drained.TrySetResult();
            return drained.Task;
        }
    }
}

// The renewal recipe keeps its own builder type so the caller's configure callback can be verified to have
// kept it (and the PEM config it carries) instead of returning an unrelated builder.
internal sealed class RenewableProducerBuilder<TKey, TValue>(ProducerConfig config) : ProducerBuilder<TKey, TValue>(config)
{
    internal string? Setting(string key) => Config.FirstOrDefault(item => item.Key == key).Value;
}
