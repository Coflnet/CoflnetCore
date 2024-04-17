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
    /// <param name="handler"></param>
    /// <param name="cancleToken"></param>
    /// <param name="groupId"></param>
    /// <param name="start">What event to start at</param>
    /// <param name="deserializer">The deserializer used for new messages</param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public async Task Consume<T>(string topic, Func<T, CancellationToken, Task> handler,
                                        CancellationToken cancleToken,
                                        string groupId = "default",
                                        AutoOffsetReset start = AutoOffsetReset.Earliest,
                                        IDeserializer<T>? deserializer = null)
    {
        while (!cancleToken.IsCancellationRequested)
            try
            {
                await ConsumeBatch(topic, async batch =>
                {
                    foreach (var message in batch)
                        await handler(message, cancleToken);
                }, cancleToken, groupId, 1, start, deserializer);
            }
            catch (Exception e)
            {
                _logger.LogError(e, $"Kafka consumer process for {topic}");
                processFail.Inc();
            }
    }


    /// <summary>
    /// Consume a batch of messages for a single topic
    /// </summary>
    /// <param name="topic"></param>
    /// <param name="handler">To be invoked</param>
    /// <param name="cancleToken"></param>
    /// <param name="groupId"></param>
    /// <param name="maxChunkSize"></param>
    /// <param name="start"></param>
    /// <param name="deserializer"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public Task ConsumeBatch<T>(string topic, Func<IEnumerable<T>, CancellationToken, Task> handler,
                                        CancellationToken cancleToken,
                                        string groupId = "default",
                                        int maxChunkSize = 500,
                                        AutoOffsetReset start = AutoOffsetReset.Earliest,
                                        IDeserializer<T>? deserializer = null)
    {
        return ConsumeBatch<T>(new string[] { topic }, handler, cancleToken, groupId, maxChunkSize, start, deserializer);
    }
    /// <summary>
    /// Consume a batch of messages for a single topic
    /// </summary>
    /// <param name="topic"></param>
    /// <param name="handler"></param>
    /// <param name="cancleToken"></param>
    /// <param name="groupId"></param>
    /// <param name="maxChunkSize"></param>
    /// <param name="start"></param>
    /// <param name="deserializer"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public Task ConsumeBatch<T>(string topic, Func<IEnumerable<T>, Task> handler,
                                        CancellationToken cancleToken,
                                        string groupId = "default",
                                        int maxChunkSize = 500,
                                        AutoOffsetReset start = AutoOffsetReset.Earliest,
                                        IDeserializer<T>? deserializer = null)
    {
        return ConsumeBatch<T>(topic, (d, c) => handler(d), cancleToken, groupId, maxChunkSize, start, deserializer);
    }

    /// <summary>
    /// Consume a batch of messages for multiple topics 
    /// </summary>
    /// <param name="config"></param>
    /// <param name="topics"></param>
    /// <param name="handler"></param>
    /// <param name="cancleToken"></param>
    /// <param name="groupId"></param>
    /// <param name="maxChunkSize"></param>
    /// <param name="start"></param>
    /// <param name="deserializer"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public async Task ConsumeBatch<T>(string[] topics, Func<IEnumerable<T>, CancellationToken, Task> handler,
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
        }, topics, handler, cancleToken, maxChunkSize, deserializer);
    }
    public async Task ConsumeBatch<T>(
                                                ConsumerConfig config,
                                                string topic,
                                                Func<IEnumerable<T>, CancellationToken, Task> handler,
                                                CancellationToken cancleToken,
                                                int maxChunkSize = 500,
                                                IDeserializer<T>? deserializer = null)
    {
        await ConsumeBatch(config, new string[] { topic }, handler, cancleToken, maxChunkSize, deserializer);
    }

    public async Task ConsumeBatch<T>(
                                            ConsumerConfig config,
                                            string[] topics,
                                            Func<IEnumerable<T>, CancellationToken, Task> handler,
                                            CancellationToken cancleToken,
                                            int maxChunkSize = 500,
                                            IDeserializer<T>? deserializer = default)
    {
        var batch = new Queue<ConsumeResult<Ignore, T>>();
        var conf = new ConsumerConfig(config)
        {
            AutoCommitIntervalMs = 0
        };
        // in case this method is awaited on in a backgroundWorker
        await Task.Yield();

        var currentChunkSize = 1;
        var builder = new ConsumerBuilder<Ignore, T>(conf);
        if (deserializer == default)
            deserializer = new JsonDeserializer<T>();
        builder = builder.SetValueDeserializer(deserializer);

        using (var c = builder.Build())
        {
            c.Subscribe(topics);
            try
            {
                while (!cancleToken.IsCancellationRequested)
                {
                    try
                    {
                        BuildBatch(config, topics, maxChunkSize, batch, currentChunkSize, c, cancleToken);
                        await handler(batch.Select(a => a.Message.Value), cancleToken).ConfigureAwait(false);
                        TellKafkaBatchProcessed(config, topics, batch, c);
                        batch.Clear();
                        currentChunkSize = IncreaseBatchSizeTillMax(maxChunkSize, currentChunkSize);
                    }
                    catch (ConsumeException e)
                    {
                        _logger.LogError(e, $"On consume {string.Join(',', topics)}");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Ensure the consumer leaves the group cleanly and final offsets are committed.
                c.Close();
            }
        }

        static void BuildBatch(ConsumerConfig config, string[] topics, int maxChunkSize, Queue<ConsumeResult<Ignore, T>> batch, int currentChunkSize, IConsumer<Ignore, T> c, CancellationToken cancleToken)
        {
            var extraLog = currentChunkSize < 2 && maxChunkSize > 2;
            if (extraLog)
                Console.WriteLine($"Polling for {currentChunkSize} messages from {string.Join(',', topics)}, config: {config.BootstrapServers}");
            var cr = c.Consume(cancleToken);
            batch.Enqueue(cr);
            if (extraLog)
                Console.WriteLine($"Consumed message '{cr.Message.Value}' at: '{cr.TopicPartitionOffset}'.");
            while (batch.Count < currentChunkSize)
            {
                cr = c.Consume(TimeSpan.Zero);
                if (cr == null)
                {
                    break;
                }
                batch.Enqueue(cr);
            }
        }

        void TellKafkaBatchProcessed(ConsumerConfig config, string[] topics, Queue<ConsumeResult<Ignore, T>> batch, IConsumer<Ignore, T> c)
        {
            if (!config.EnableAutoCommit ?? true)
                try
                {
                    c.Commit(batch.Select(b => b.TopicPartitionOffset));
                }
                catch (KafkaException e)
                {
                    _logger.LogError(e, $"On commit {string.Join(',', topics)} {e.Error.IsFatal}");
                }
        }

        static int IncreaseBatchSizeTillMax(int maxChunkSize, int currentChunkSize)
        {
            if (currentChunkSize < maxChunkSize)
                currentChunkSize++;
            return currentChunkSize;
        }
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