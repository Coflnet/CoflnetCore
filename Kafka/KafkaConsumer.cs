using Confluent.Kafka;
using Prometheus;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text;
using Newtonsoft.Json;

namespace Coflnet.Kafka;
public class KafkaConsumer
{
    static Counter processFail = Metrics.CreateCounter("consume_process_failed", "How often processing of consumed messages failed");
    private readonly ILogger<KafkaConsumer> _logger;
    private readonly IConfiguration config;

    public KafkaConsumer(ILogger<KafkaConsumer> logger, IConfiguration config)
    {
        _logger = logger;
        this.config = config;
    }


    /// <summary>
    /// Generic consumer
    /// </summary>
    /// <param name="config"></param>
    /// <param name="topic"></param>
    /// <param name="action"></param>
    /// <param name="cancleToken"></param>
    /// <param name="groupId"></param>
    /// <param name="start">What event to start at</param>
    /// <param name="deserializer">The deserializer used for new messages</param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public async Task Consume<T>(string topic, Func<T, Task> action,
                                        CancellationToken cancleToken,
                                        string groupId = "default",
                                        AutoOffsetReset start = AutoOffsetReset.Earliest,
                                        IDeserializer<T>? deserializer = null)
    {
        if (config.GetValue<bool>("KAFKA:TLS:RELOAD_ON_CHANGE"))
        {
            await ConsumeRenewing(topic, action, cancleToken, groupId, start, deserializer);
            return;
        }
        while (!cancleToken.IsCancellationRequested)
            try
            {
                await ConsumeBatch(topic, async batch =>
                {
                    foreach (var message in batch)
                        await action(message);
                }, cancleToken, groupId, 1, start, deserializer);
            }
            catch (Exception e)
            {
                _logger.LogError(e, $"Kafka consumer process for {topic}");
                processFail.Inc();
            }
    }


    private async Task ConsumeRenewing<T>(string topic, Func<T, Task> action, CancellationToken stoppingToken,
        string groupId, AutoOffsetReset start, IDeserializer<T>? deserializer)
    {
        var common = KafkaCreator.GetClientConfig(config);
        var consumerConfig = new ConsumerConfig(common.ToDictionary(item => item.Key, item => item.Value))
        {
            GroupId = groupId,
            AutoOffsetReset = start,
            EnableAutoCommit = false,
            EnableAutoOffsetStore = false,
            PartitionAssignmentStrategy = PartitionAssignmentStrategy.CooperativeSticky
        };
        await using var rotation = await KafkaTlsRotation.CreateAsync(config, common, [topic], _logger);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ConsumeBatchCore(consumerConfig, [topic], async batch =>
                {
                    foreach (var message in batch) await action(message);
                }, stoppingToken, 1, deserializer, rotation);

            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError("Renewable Kafka consumer stopped ({FailureType}); rebuilding the same group.", e.GetType().Name);
                processFail.Inc();
            }
        }
    }

    /// <summary>
    /// Consume a batch of messages for a single topic
    /// </summary>
    /// <param name="config"></param>
    /// <param name="topic"></param>
    /// <param name="action"></param>
    /// <param name="cancleToken"></param>
    /// <param name="groupId"></param>
    /// <param name="maxChunkSize"></param>
    /// <param name="start"></param>
    /// <param name="deserializer"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public Task ConsumeBatch<T>(string topic, Func<IEnumerable<T>, Task> action,
                                        CancellationToken cancleToken,
                                        string groupId = "default",
                                        int maxChunkSize = 500,
                                        AutoOffsetReset start = AutoOffsetReset.Earliest,
                                        IDeserializer<T>? deserializer = null)
    {
        return ConsumeBatch<T>(new string[] { topic }, action, cancleToken, groupId, maxChunkSize, start, deserializer);
    }

    /// <summary>
    /// Consume a batch of messages for multiple topics 
    /// </summary>
    /// <param name="config"></param>
    /// <param name="topics"></param>
    /// <param name="action"></param>
    /// <param name="cancleToken"></param>
    /// <param name="groupId"></param>
    /// <param name="maxChunkSize"></param>
    /// <param name="start"></param>
    /// <param name="deserializer"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public async Task ConsumeBatch<T>(string[] topics, Func<IEnumerable<T>, Task> action,
                                        CancellationToken cancleToken = default,
                                        string groupId = "default",
                                        int maxChunkSize = 500,
                                        AutoOffsetReset start = AutoOffsetReset.Earliest,
                                        IDeserializer<T>? deserializer = null)
    {
        await ConsumeBatch(new ConsumerConfig(KafkaCreator.GetClientConfig(config))
        {
            GroupId = groupId,

            // Note: The AutoOffsetReset property determines the start offset in the event
            // there are not yet any committed offsets for the consumer group for the
            // topic/partitions of interest. By default, offsets are committed
            // automatically, so in this example, consumption will only start from the
            // earliest message in the topic 'my-topic' the first time you run the program.
            AutoOffsetReset = start,
            EnableAutoCommit = false,
            PartitionAssignmentStrategy = PartitionAssignmentStrategy.CooperativeSticky
        }, topics, action, cancleToken, maxChunkSize, deserializer);
    }
    public async Task ConsumeBatch<T>(
                                                ConsumerConfig config,
                                                string topic,
                                                Func<IEnumerable<T>, Task> action,
                                                CancellationToken cancleToken,
                                                int maxChunkSize = 500,
                                                IDeserializer<T>? deserializer = null)
    {
        await ConsumeBatch(config, new string[] { topic }, action, cancleToken, maxChunkSize, deserializer);
    }

    public async Task ConsumeBatch<T>(
                                            ConsumerConfig config,
                                            string[] topics,
                                            Func<IEnumerable<T>, Task> action,
                                            CancellationToken cancleToken,
                                            int maxChunkSize = 500,
                                            IDeserializer<T>? deserializer = default)
    {
        await ConsumeBatchCore(config, topics, action, cancleToken, maxChunkSize, deserializer);
    }

    internal async Task ConsumeBatchCore<T>(ConsumerConfig config, string[] topics,
        Func<IEnumerable<T>, Task> action, CancellationToken cancleToken, int maxChunkSize,
        IDeserializer<T>? deserializer = default, KafkaTlsRotation? rotation = null,
        Func<ConsumerConfig, TaskCompletionSource, IConsumer<Ignore, T>>? createConsumer = null)
    {
        var batch = new Queue<ConsumeResult<Ignore, T>>();
        var conf = new ConsumerConfig(config.ToDictionary(item => item.Key, item => item.Value)) { AutoCommitIntervalMs = 0 };
        await Task.Yield();
        createConsumer ??= (options, ready) =>
        {
            var builder = new ConsumerBuilder<Ignore, T>(options)
                .SetValueDeserializer(deserializer ?? new JsonDeserializer<T>());
            if (rotation is not null)
                builder.SetPartitionsAssignedHandler((_, _) => ready.TrySetResult())
                    .SetErrorHandler((_, error) =>
                    {
                        if (error.IsFatal || error.Code is ErrorCode.Local_Authentication or ErrorCode.SaslAuthenticationFailed
                            or ErrorCode.GroupAuthorizationFailed or ErrorCode.TopicAuthorizationFailed
                            or ErrorCode.Local_Transport or ErrorCode.Local_AllBrokersDown or ErrorCode.Local_Ssl)
                            ready.TrySetException(new KafkaException(new Error(error.Code)));
                    });
            return builder.Build();
        };
        IConsumer<Ignore, T>? c = null;
        var interrupted = CancellationTokenSource.CreateLinkedTokenSource(cancleToken,
            rotation?.RotationToken ?? CancellationToken.None);
        var currentChunkSize = 1;
        try
        {
            var initialReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            c = createConsumer(rotation is null ? conf : rotation.Configure(conf), initialReady);
            c.Subscribe(topics);
            if (rotation is not null) await AwaitAssignment(c, initialReady, batch, cancleToken);
            while (!cancleToken.IsCancellationRequested)
            {
                if (batch.Count == 0 && rotation?.RotationToken.IsCancellationRequested == true && rotation.ShouldRotate())
                {
                    var candidate = rotation.Candidate!;
                    var previous = rotation.Current;
                    IConsumer<Ignore, T>? replacement = null;
                    var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    try
                    {
                        replacement = createConsumer(rotation.Configure(conf, candidate), ready);
                        if (!rotation.IsPending(candidate) || ready.Task.IsFaulted)
                            throw new InvalidOperationException("Kafka candidate changed or failed before handoff.");
                        var old = c;
                        c = null; // Close is irreversible; failures below rebuild the previous snapshot.
                        try { old.Close(); } finally { old.Dispose(); }
                        if (!rotation.IsValid(candidate)) throw new InvalidOperationException("Kafka candidate expired during Close.");
                        replacement.Subscribe(topics);
                        await AwaitAssignment(replacement, ready, batch, cancleToken);
                        if (!rotation.IsValid(candidate)) throw new InvalidOperationException("Kafka candidate expired during assignment.");
                        rotation.Accept(candidate);
                        c = replacement;
                        replacement = null;
                        _logger.LogInformation("Renewable Kafka consumer handoff completed after assignment.");
                    }
                    catch (Exception e) when (!cancleToken.IsCancellationRequested)
                    {
                        rotation.Reject(candidate);
                        rotation.Retry(candidate);
                        var reportFailure = rotation.ReportOnce(candidate, e.GetType().Name);
                        if (replacement is not null)
                        {
                            var failed = replacement;
                            replacement = null;
                            try { failed.Close(); }
                            catch (KafkaException closeError) { if (reportFailure) _logger.LogWarning("Failed Kafka candidate close reported {ErrorCode}; disposing before recovery.", closeError.Error.Code); }
                            finally { failed.Dispose(); }
                        }
                        if (c is not null)
                        {
                            if (reportFailure) _logger.LogError("Renewable Kafka candidate rejected before handoff; current handle retained.");
                        }
                        else
                        {
                            batch.Clear(); // No auto-store/commit: an unprocessed candidate record is replayed.
                            if (!rotation.IsValid(previous)) throw new InvalidOperationException("Previous Kafka certificate is no longer valid for recovery.");
                            var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                            c = createConsumer(rotation.Configure(conf, previous), recovered);
                            c.Subscribe(topics);
                            await AwaitAssignment(c, recovered, batch, cancleToken);
                            if (!rotation.IsValid(previous)) throw new InvalidOperationException("Previous Kafka certificate expired during recovery.");
                            if (reportFailure) _logger.LogError("Renewable Kafka handoff failed after Close; previous validated generation rebuilt. The original handle was not retained.");
                        }
                    }
                    finally
                    {
                        if (replacement is not null)
                            try { replacement.Close(); } finally { replacement.Dispose(); }
                    }
                    interrupted.Dispose();
                    interrupted = CancellationTokenSource.CreateLinkedTokenSource(cancleToken, rotation.RotationToken);
                    continue;
                }
                if (interrupted.IsCancellationRequested)
                {
                    interrupted.Dispose();
                    interrupted = CancellationTokenSource.CreateLinkedTokenSource(cancleToken,
                        rotation?.RotationToken ?? CancellationToken.None);
                }
                try
                {
                    if (batch.Count == 0)
                    {
                        ConsumeResult<Ignore, T> cr;
                        try { cr = c.Consume(interrupted.Token); }
                        catch (OperationCanceledException) when (rotation is not null && !cancleToken.IsCancellationRequested)
                        { continue; }
                        batch.Enqueue(cr); // A record returned during cancellation must still be processed.
                    }
                    while (batch.Count < currentChunkSize)
                    {
                        var cr = c.Consume(TimeSpan.Zero);
                        if (cr is null) break;
                        batch.Enqueue(cr);
                    }
                    await action(batch.Select(a => a.Message.Value)).ConfigureAwait(false);
                    if (!config.EnableAutoCommit ?? true)
                        try
                        {
                            c.Commit(rotation is null ? batch.Select(b => b.TopicPartitionOffset) :
                                batch.GroupBy(b => b.TopicPartition).Select(g =>
                                    new TopicPartitionOffset(g.Key, g.Max(b => b.Offset.Value) + 1)));
                        }
                        catch (KafkaException e)
                        {
                            if (rotation is null) _logger.LogError(e, "Kafka batch commit failed.");
                            else _logger.LogError("Renewable Kafka commit failed ({ErrorCode}); unconfirmed offsets may replay.", e.Error.Code);
                        }
                    batch.Clear();
                    if (currentChunkSize < maxChunkSize) currentChunkSize++;
                }
                catch (ConsumeException e)
                {
                    if (rotation is null) _logger.LogError(e, "Kafka consume failed.");
                    else _logger.LogError("Renewable Kafka consume failed ({ErrorCode}).", e.Error.Code);
                }
            }
        }
        catch (OperationCanceledException) when (rotation is null || interrupted.IsCancellationRequested)
        { if (rotation is null) c?.Close(); }
        finally
        {
            try { if (rotation is not null) c?.Close(); }
            finally { c?.Dispose(); interrupted.Dispose(); }
        }
    }

    private static async Task AwaitAssignment<T>(IConsumer<Ignore, T> consumer, TaskCompletionSource ready,
        Queue<ConsumeResult<Ignore, T>> batch, CancellationToken stoppingToken)
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
                if (record is not null) batch.Enqueue(record);
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

}

public class JsonDeserializer<T> : IDeserializer<T>
{
    public T Deserialize(ReadOnlySpan<byte> data, bool isNull, SerializationContext context)
    {
        if (isNull)
            return default!;
        var str = Encoding.UTF8.GetString(data);
        return JsonConvert.DeserializeObject<T>(str)!;
    }
}