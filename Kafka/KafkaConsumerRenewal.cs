using Confluent.Kafka;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Coflnet.Kafka;

/// <summary>
/// Readiness of one native consumer during a renewal start, handoff or recovery. Call <see cref="PartitionsAssigned"/>
/// from the consumer's partitions-assigned handler (an empty assignment counts) and <see cref="ReportError"/> from its
/// error handler, or let <see cref="Attach{TKey,TValue}"/> wire both on a builder that has neither of its own.
/// Both methods may be called from librdkafka callback threads.
/// </summary>
public sealed class KafkaConsumerReadiness
{
    internal KafkaConsumerReadiness() { }

    internal TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void PartitionsAssigned() => Completion.TrySetResult();

    /// <summary>Fatal, authentication, authorization and transport errors fail readiness; other errors are ignored.</summary>
    public void ReportError(Error error)
    {
        if (FailsReadiness(error))
            Completion.TrySetException(new KafkaException(new Error(error.Code)));
    }

    public ConsumerBuilder<TKey, TValue> Attach<TKey, TValue>(ConsumerBuilder<TKey, TValue> builder) =>
        builder.SetPartitionsAssignedHandler((_, _) => PartitionsAssigned()).SetErrorHandler((_, error) => ReportError(error));

    private static bool FailsReadiness(Error error) =>
        error.IsFatal || error.Code is ErrorCode.Local_Authentication or ErrorCode.SaslAuthenticationFailed
            or ErrorCode.GroupAuthorizationFailed or ErrorCode.TopicAuthorizationFailed
            or ErrorCode.Local_Transport or ErrorCode.Local_AllBrokersDown or ErrorCode.Local_Ssl;
}

/// <summary>
/// Client-certificate renewal (<c>KAFKA:TLS:RELOAD_ON_CHANGE=true</c>, see README) for consume loops that build their
/// own native consumer; <see cref="KafkaConsumer"/> uses the same handoff. The loop starts its handle with
/// <see cref="StartAsync"/>, links <see cref="HandoffRequested"/> into blocking consume calls and, once it reaches a safe
/// boundary (every fetched record processed and its offset committed), continues with the handle
/// <see cref="HandoffAsync"/> returns. Auto-commit and auto-offset-store are disabled: the loop commits the next offset
/// of every processed partition itself. Not thread-safe: call <see cref="StartAsync"/> and <see cref="HandoffAsync"/>
/// from the consume loop only, one at a time, and stop using the instance (including <see cref="HandoffRequested"/>)
/// once it is disposed.
/// </summary>
public sealed class KafkaConsumerRenewal<TKey, TValue> : IAsyncDisposable
{
    private readonly KafkaTlsRotation rotation;
    private readonly bool ownsRotation;
    private readonly ConsumerConfig settings;
    private readonly string[] topics;
    private readonly Func<ConsumerConfig, KafkaConsumerReadiness, IConsumer<TKey, TValue>> build;
    private readonly ILogger logger;

    internal KafkaConsumerRenewal(KafkaTlsRotation rotation, ConsumerConfig settings, string[] topics,
        Func<ConsumerConfig, KafkaConsumerReadiness, IConsumer<TKey, TValue>> build, ILogger logger, bool ownsRotation = false)
    {
        this.rotation = rotation;
        this.settings = settings;
        this.topics = topics;
        this.build = build;
        this.logger = logger;
        this.ownsRotation = ownsRotation;
    }

    /// <summary>
    /// <c>null</c> when the flag is off, or (one-time warning) when <paramref name="config"/> connects elsewhere than
    /// <c>KAFKA:*</c>: consume on the plain path then. Throws, starting no client, without SASL_SSL or when the current
    /// files fail validation or the metadata preflight. <paramref name="build"/> must return an unsubscribed consumer
    /// whose handlers report to the given readiness; it is called again for every handoff and recovery.
    /// </summary>
    public static Task<KafkaConsumerRenewal<TKey, TValue>?> CreateAsync(IConfiguration configuration, ConsumerConfig config,
        string[] topics, Func<ConsumerConfig, KafkaConsumerReadiness, IConsumer<TKey, TValue>> build, ILogger logger) =>
        CreateAsync(configuration, config, topics, build, logger, null);

    internal static async Task<KafkaConsumerRenewal<TKey, TValue>?> CreateAsync(IConfiguration configuration, ConsumerConfig config,
        string[] topics, Func<ConsumerConfig, KafkaConsumerReadiness, IConsumer<TKey, TValue>> build, ILogger logger,
        Func<ClientConfig, Task>? preflight)
    {
        if (!configuration.GetValue<bool>("KAFKA:TLS:RELOAD_ON_CHANGE")) return null;
        if (KafkaConsumer.RenewableSettings(configuration, config, logger) is not { } renewable) return null;
        var rotation = await KafkaTlsRotation.CreateAsync(configuration, renewable.Baseline, topics, logger, preflight).ConfigureAwait(false);
        return new(rotation, renewable.Settings, topics, build, logger, ownsRotation: true);
    }

    /// <summary>
    /// Cancelled while a preflighted candidate waits for a handoff. Read it again after every handoff; reading it after
    /// <see cref="DisposeAsync"/> throws <see cref="ObjectDisposedException"/>.
    /// </summary>
    public CancellationToken HandoffRequested => rotation.RotationToken;

    /// <summary>
    /// Builds, subscribes and awaits the assignment (60 s bound) of the first handle. Records fetched meanwhile go to
    /// <paramref name="buffered"/> and must be processed first. On failure (also of <paramref name="buffered"/>) the
    /// handle is closed and the error propagates.
    /// </summary>
    public async Task<IConsumer<TKey, TValue>> StartAsync(Action<ConsumeResult<TKey, TValue>> buffered, CancellationToken stoppingToken)
    {
        var ready = new KafkaConsumerReadiness();
        var consumer = build(rotation.Configure(settings), ready);
        var records = new List<ConsumeResult<TKey, TValue>>();
        try
        {
            consumer.Subscribe(topics);
            await AwaitAssignment(consumer, ready.Completion, records, stoppingToken);
        }
        catch
        {
            CloseAndDispose(consumer);
            throw;
        }
        return DeliverOrClose(consumer, records, buffered);
    }

    /// <summary>
    /// Takes ownership of <paramref name="current"/> and returns the handle to continue with: <paramref name="current"/>
    /// itself when no handoff is due or the candidate failed before Close, otherwise a subscribed, assigned replacement
    /// (or a rebuilt previous generation when the candidate failed after Close). Only records fetched by the returned
    /// handle reach <paramref name="buffered"/>. Throws when that recovery fails, when <paramref name="buffered"/> throws or
    /// on stop; no handle is left open then.
    /// </summary>
    public async Task<IConsumer<TKey, TValue>> HandoffAsync(IConsumer<TKey, TValue> current,
        Action<ConsumeResult<TKey, TValue>> buffered, CancellationToken stoppingToken)
    {
        if (!rotation.RotationToken.IsCancellationRequested || !rotation.ShouldRotate()) return current;
        var records = new List<ConsumeResult<TKey, TValue>>();
        var next = await NextHandleAsync(current, records, stoppingToken);
        return DeliverOrClose(next, records, buffered);
    }

    // The handle to continue with; records holds only what that handle fetched while assigning.
    private async Task<IConsumer<TKey, TValue>> NextHandleAsync(IConsumer<TKey, TValue> current,
        List<ConsumeResult<TKey, TValue>> records, CancellationToken stoppingToken)
    {
        var candidate = rotation.Candidate!;
        var previous = rotation.Current;
        var handles = new Handles(current);
        try
        {
            return await SwapAsync(handles, candidate, records, stoppingToken);
        }
        catch (Exception e) when (!stoppingToken.IsCancellationRequested)
        {
            records.Clear(); // fetched by the failed candidate: replayed
            var report = Rejected(candidate, e);
            CloseFailedCandidate(handles.TakeReplacement(), report);
            if (handles.TakeOld() is not { } retained) return await RecoverAsync(previous, report, records, stoppingToken);
            if (report) logger.LogError("Renewable Kafka candidate rejected before handoff; current handle retained.");
            return retained;
        }
        finally
        {
            handles.CloseRemaining();
        }
    }

    private async Task<IConsumer<TKey, TValue>> SwapAsync(Handles handles, KafkaTlsRotation.Snapshot candidate,
        List<ConsumeResult<TKey, TValue>> records, CancellationToken stoppingToken)
    {
        var ready = new KafkaConsumerReadiness();
        var replacement = handles.Replacement = build(rotation.Configure(settings, candidate), ready);
        if (!rotation.IsPending(candidate) || ready.Completion.Task.IsFaulted)
            throw new InvalidOperationException("Kafka candidate changed or failed before handoff.");
        handles.CloseOld(); // Close is irreversible; failures below rebuild the previous snapshot.
        if (!rotation.IsValid(candidate)) throw new InvalidOperationException("Kafka candidate expired during Close.");
        replacement.Subscribe(topics);
        await AwaitAssignment(replacement, ready.Completion, records, stoppingToken);
        if (!rotation.IsValid(candidate)) throw new InvalidOperationException("Kafka candidate expired during assignment.");
        rotation.Accept(candidate);
        handles.TakeReplacement();
        logger.LogInformation("Renewable Kafka consumer handoff completed after assignment.");
        return replacement;
    }

    private bool Rejected(KafkaTlsRotation.Snapshot candidate, Exception failure)
    {
        rotation.Reject(candidate);
        rotation.Retry(candidate);
        return rotation.ReportOnce(candidate, failure.GetType().Name);
    }

    private void CloseFailedCandidate(IConsumer<TKey, TValue>? failed, bool report)
    {
        if (failed is null) return;
        try { failed.Close(); }
        catch (KafkaException closeError)
        {
            if (report) logger.LogWarning("Failed Kafka candidate close reported {ErrorCode}; disposing before recovery.", closeError.Error.Code);
        }
        finally { failed.Dispose(); }
    }

    // No auto-store/commit: records the closed candidate fetched are replayed by the rebuilt handle.
    private async Task<IConsumer<TKey, TValue>> RecoverAsync(KafkaTlsRotation.Snapshot previous, bool report,
        List<ConsumeResult<TKey, TValue>> records, CancellationToken stoppingToken)
    {
        if (!rotation.IsValid(previous)) throw new InvalidOperationException("Previous Kafka certificate is no longer valid for recovery.");
        var ready = new KafkaConsumerReadiness();
        var recovered = build(rotation.Configure(settings, previous), ready);
        try
        {
            recovered.Subscribe(topics);
            await AwaitAssignment(recovered, ready.Completion, records, stoppingToken);
            if (!rotation.IsValid(previous)) throw new InvalidOperationException("Previous Kafka certificate expired during recovery.");
        }
        catch
        {
            CloseAndDispose(recovered);
            throw;
        }
        if (report) logger.LogError("Renewable Kafka handoff failed after Close; previous validated generation rebuilt. The original handle was not retained.");
        return recovered;
    }

    // A throwing callback must not leak the handle the caller would never receive.
    private static IConsumer<TKey, TValue> DeliverOrClose(IConsumer<TKey, TValue> consumer, List<ConsumeResult<TKey, TValue>> records,
        Action<ConsumeResult<TKey, TValue>> buffered)
    {
        try
        {
            foreach (var record in records) buffered(record);
            return consumer;
        }
        catch
        {
            CloseAndDispose(consumer);
            throw;
        }
    }

    private static void CloseAndDispose(IConsumer<TKey, TValue>? consumer)
    {
        if (consumer is null) return;
        try { consumer.Close(); }
        finally { consumer.Dispose(); }
    }

    internal static async Task AwaitAssignment(IConsumer<TKey, TValue> consumer, TaskCompletionSource ready,
        List<ConsumeResult<TKey, TValue>> records, CancellationToken stoppingToken)
    {
        using var wake = new CancellationTokenSource();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var interrupted = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, wake.Token, deadline.Token);
        _ = ready.Task.ContinueWith(_ =>
        {
            try { wake.Cancel(); } catch (ObjectDisposedException) { }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        while (!ready.Task.IsCompleted)
        {
            try
            {
                var record = consumer.Consume(interrupted.Token);
                if (record is not null) records.Add(record);
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                if (deadline.IsCancellationRequested && !ready.Task.IsCompleted)
                    throw new TimeoutException("Kafka assignment did not complete within 60 seconds.");
            }
        }
        stoppingToken.ThrowIfCancellationRequested();
        await ready.Task;
    }

    public async ValueTask DisposeAsync()
    {
        if (ownsRotation) await rotation.DisposeAsync().ConfigureAwait(false);
    }

    // The handles one handoff owns; whatever is still held when it ends (stop) is closed.
    private sealed class Handles(IConsumer<TKey, TValue> old)
    {
        private IConsumer<TKey, TValue>? old = old;
        internal IConsumer<TKey, TValue>? Replacement;

        internal IConsumer<TKey, TValue>? TakeOld() => Interlocked.Exchange(ref old, null);
        internal IConsumer<TKey, TValue>? TakeReplacement() => Interlocked.Exchange(ref Replacement, null);
        internal void CloseOld() => CloseAndDispose(TakeOld());

        internal void CloseRemaining()
        {
            try { CloseAndDispose(TakeReplacement()); }
            finally { CloseAndDispose(TakeOld()); }
        }
    }
}
