using System.Collections.Concurrent;
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
    internal static readonly ConcurrentDictionary<string, bool> renewalWarnings = new();
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


    internal async Task ConsumeRenewing<T>(string topic, Func<T, Task> action, CancellationToken stoppingToken,
        string groupId, AutoOffsetReset start, IDeserializer<T>? deserializer, Func<ClientConfig, Task>? preflight = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<ConsumerConfig, TaskCompletionSource, IConsumer<Ignore, T>>? createConsumer = null)
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
        await using var rotation = await KafkaTlsRotation.CreateAsync(config, common, [topic], _logger, preflight);
        var backoff = new RebuildBackoff(delay ?? Task.Delay);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ConsumeBatchCore(consumerConfig, [topic], async batch =>
                {
                    foreach (var message in batch) await action(message);
                    backoff.Reset();
                }, stoppingToken, 1, deserializer, rotation, createConsumer);
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError("Renewable Kafka consumer stopped ({FailureType}); rebuilding the same group.", e.GetType().Name);
                processFail.Inc();
                await backoff.WaitAsync(stoppingToken);
            }
        }
    }

    // Bounds rebuilds after fast failures (auth, all brokers down, assignment timeout): 2 s doubling to 60 s,
    // back to 2 s once a record was processed.
    internal sealed class RebuildBackoff(Func<TimeSpan, CancellationToken, Task> delay)
    {
        private static readonly TimeSpan First = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan Max = TimeSpan.FromSeconds(60);
        private TimeSpan next = First;

        internal void Reset() => next = First;

        internal async Task WaitAsync(CancellationToken stoppingToken)
        {
            var wait = next;
            next = TimeSpan.FromTicks(Math.Min(Max.Ticks, next.Ticks * 2));
            try { await delay(wait, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
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
        if (this.config.GetValue<bool>("KAFKA:TLS:RELOAD_ON_CHANGE"))
        {
            await ConsumeBatchRenewing(config, topics, action, cancleToken, maxChunkSize, deserializer);
            return;
        }
        await ConsumeBatchCore(config, topics, action, cancleToken, maxChunkSize, deserializer);
    }

    // Opt-in renewal for every ConsumeBatch overload: the in-flight batch and its commit finish before the handoff.
    // Unlike Consume, failures propagate to the caller exactly as on the default path.
    internal async Task ConsumeBatchRenewing<T>(ConsumerConfig consumerConfig, string[] topics,
        Func<IEnumerable<T>, Task> action, CancellationToken stoppingToken, int maxChunkSize,
        IDeserializer<T>? deserializer = default, Func<ClientConfig, Task>? preflight = null,
        Func<ConsumerConfig, TaskCompletionSource, IConsumer<Ignore, T>>? createConsumer = null)
    {
        if (RenewableSettings(config, consumerConfig, _logger) is not { } renewable)
        {
            await ConsumeBatchCore(consumerConfig, topics, action, stoppingToken, maxChunkSize, deserializer, createConsumer: createConsumer);
            return;
        }
        await using var rotation = await KafkaTlsRotation.CreateAsync(config, renewable.Baseline, topics, _logger, preflight);
        await ConsumeBatchCore(renewable.Settings, topics, action, stoppingToken, maxChunkSize, deserializer, rotation, createConsumer);
    }

    // null (one-time warning): the ConsumerConfig connects elsewhere than KAFKA:* and is consumed without renewal.
    internal static (ClientConfig Baseline, ConsumerConfig Settings)? RenewableSettings(IConfiguration configuration,
        ConsumerConfig consumerConfig, ILogger logger)
    {
        var baseline = KafkaCreator.GetClientConfig(configuration);
        if (!SameConnection(consumerConfig, baseline))
        {
            if (renewalWarnings.TryAdd("connection", true))
                logger.LogWarning("Kafka batch consumer TLS renewal disabled (ConsumerConfig differs from KAFKA:* connection settings); it keeps its certificate until restart.");
            return null;
        }
        // Independent copy (Confluent configs wrap the caller's dictionary). Auto-commit would let Close commit
        // records buffered by a failed handoff that were never processed, so only processed offsets are committed.
        return (baseline, new ConsumerConfig(consumerConfig.ToDictionary(item => item.Key, item => item.Value))
        {
            EnableAutoCommit = false,
            EnableAutoOffsetStore = false
        });
    }

    // The SASL identity counts too: preflight and renewal use KAFKA:* credentials, never the caller's.
    private static bool SameConnection(ClientConfig candidate, ClientConfig baseline) =>
        candidate.BootstrapServers == baseline.BootstrapServers && candidate.SecurityProtocol == baseline.SecurityProtocol
        && candidate.SaslUsername == baseline.SaslUsername;

    internal async Task ConsumeBatchCore<T>(ConsumerConfig config, string[] topics,
        Func<IEnumerable<T>, Task> action, CancellationToken cancleToken, int maxChunkSize,
        IDeserializer<T>? deserializer = default, KafkaTlsRotation? rotation = null,
        Func<ConsumerConfig, TaskCompletionSource, IConsumer<Ignore, T>>? createConsumer = null)
    {
        var batch = new Queue<ConsumeResult<Ignore, T>>();
        var conf = new ConsumerConfig(config.ToDictionary(item => item.Key, item => item.Value)) { AutoCommitIntervalMs = 0 };
        await Task.Yield();
        Func<ConsumerConfig, KafkaConsumerReadiness, IConsumer<Ignore, T>> build = createConsumer is null
            ? (options, readiness) => BuildConsumer(options, deserializer, rotation is null ? null : readiness)
            : (options, readiness) => createConsumer(options, readiness.Completion);
        var renewal = rotation is null ? null : new KafkaConsumerRenewal<Ignore, T>(rotation, conf, topics, build, _logger);
        IConsumer<Ignore, T>? c = null;
        var interrupted = Interruptible(cancleToken, renewal);
        var currentChunkSize = 1;
        try
        {
            if (renewal is null)
            {
                c = build(conf, new KafkaConsumerReadiness());
                c.Subscribe(topics);
            }
            else c = await renewal.StartAsync(batch.Enqueue, cancleToken);
            while (!cancleToken.IsCancellationRequested)
            {
                if (batch.Count == 0 && renewal?.HandoffRequested.IsCancellationRequested == true)
                {
                    var current = c;
                    c = null; // the renewal owns the handle until it returns the one to continue with
                    c = await renewal.HandoffAsync(current, batch.Enqueue, cancleToken);
                    interrupted.Dispose();
                    interrupted = Interruptible(cancleToken, renewal);
                    continue;
                }
                if (interrupted.IsCancellationRequested)
                {
                    interrupted.Dispose();
                    interrupted = Interruptible(cancleToken, renewal);
                }
                try
                {
                    if (batch.Count == 0)
                    {
                        ConsumeResult<Ignore, T> cr;
                        try { cr = c.Consume(interrupted.Token); }
                        catch (OperationCanceledException) when (renewal is not null && !cancleToken.IsCancellationRequested)
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
                            c.Commit(renewal is null ? batch.Select(b => b.TopicPartitionOffset) :
                                batch.GroupBy(b => b.TopicPartition).Select(g =>
                                    new TopicPartitionOffset(g.Key, g.Max(b => b.Offset.Value) + 1)));
                        }
                        catch (KafkaException e)
                        {
                            if (renewal is null) _logger.LogError(e, "Kafka batch commit failed.");
                            else _logger.LogError("Renewable Kafka commit failed ({ErrorCode}); unconfirmed offsets may replay.", e.Error.Code);
                        }
                    batch.Clear();
                    if (currentChunkSize < maxChunkSize) currentChunkSize++;
                }
                catch (ConsumeException e)
                {
                    if (renewal is null) _logger.LogError(e, "Kafka consume failed.");
                    else _logger.LogError("Renewable Kafka consume failed ({ErrorCode}).", e.Error.Code);
                }
            }
        }
        catch (OperationCanceledException) when (renewal is null || interrupted.IsCancellationRequested)
        { if (renewal is null) c?.Close(); }
        finally
        {
            try { if (renewal is not null) c?.Close(); }
            finally { c?.Dispose(); interrupted.Dispose(); }
        }
    }

    private static CancellationTokenSource Interruptible<TKey, TValue>(CancellationToken stoppingToken,
        KafkaConsumerRenewal<TKey, TValue>? renewal) =>
        CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, renewal?.HandoffRequested ?? CancellationToken.None);

    private static IConsumer<Ignore, T> BuildConsumer<T>(ConsumerConfig options, IDeserializer<T>? deserializer,
        KafkaConsumerReadiness? readiness)
    {
        var builder = new ConsumerBuilder<Ignore, T>(options).SetValueDeserializer(deserializer ?? new JsonDeserializer<T>());
        readiness?.Attach(builder);
        return builder.Build();
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