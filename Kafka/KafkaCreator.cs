using System.Collections.Concurrent;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace Coflnet.Kafka;
public class KafkaCreator
{
    internal static readonly ConcurrentDictionary<string, bool> renewalWarnings = new();
    private readonly ILogger<KafkaCreator> _logger;
    private readonly IConfiguration config;
    private readonly IConfiguration root;

    public KafkaCreator(ILogger<KafkaCreator> logger, IConfiguration config)
    {
        _logger = logger;
        root = config;
        this.config = config.GetSection("KAFKA");
    }

    public async Task CreateTopicIfNotExist(string topic, int partitions = 3)
    {
        short replicationFactor = config.GetValue<short>("REPLICATION_FACTOR");
        using var adminClient = new AdminClientBuilder(GetClientConfig(config)).Build();
        try
        {
            var meta = adminClient.GetMetadata(topic, TimeSpan.FromSeconds(10));
            if (meta.Topics.Count != 0 && meta.Topics[0].Error.Code == ErrorCode.NoError)
            {
                if (meta.Topics[0].Partitions.Count < partitions)
                {
                    _logger.LogInformation($"Topic {topic} has {meta.Topics[0].Partitions.Count} partitions, increasing to {partitions}");
                    _logger.LogInformation($"Topic metadata: {JsonConvert.SerializeObject(meta.Topics[0])}");
                    await adminClient.CreatePartitionsAsync(new[]
                    {
                            new PartitionsSpecification
                            {
                                Topic = topic,
                                IncreaseTo = partitions
                            }
                        });
                }
                return; // topic exists
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, $"Kafka topic {topic} check");
        }
        try
        {
            await adminClient.CreateTopicsAsync(new TopicSpecification[]
            {
                    new TopicSpecification
                    {
                        Name = topic,
                        NumPartitions = partitions,
                        ReplicationFactor = replicationFactor
                    }
            });
            _logger.LogInformation($"Created topic {topic} with {partitions} partitions and {replicationFactor} replication factor");
        }
        catch (Exception e)
        {
            _logger.LogError(e, $"Kafka topic {topic} create");
        }
    }

    public static AdminClientConfig GetClientConfig(IConfiguration config)
    {
        if (config["BROKERS"] == null)
            config = config.GetSection("KAFKA");
        var baseConfig = new AdminClientConfig
        {
            BootstrapServers = config["BROKERS"],
            SslCaLocation = config["TLS:CA_LOCATION"],
            SslCertificateLocation = config["TLS:CERTIFICATE_LOCATION"],
            SslKeyLocation = config["TLS:KEY_LOCATION"],
            SaslPassword = config["PASSWORD"]
        };
        if (!string.IsNullOrEmpty(config["USERNAME"]))
            baseConfig.SaslUsername = config["USERNAME"];
        if (!string.IsNullOrEmpty(baseConfig.SaslUsername))
        {
            if (!string.IsNullOrEmpty(baseConfig.SslKeyLocation))
                baseConfig.SecurityProtocol = SecurityProtocol.SaslSsl;
            else
                baseConfig.SecurityProtocol = SecurityProtocol.SaslPlaintext;
            baseConfig.SaslMechanism = SaslMechanism.ScramSha256;
        }
        else
        {
            if (!string.IsNullOrEmpty(baseConfig.SslKeyLocation))
                baseConfig.SecurityProtocol = SecurityProtocol.Ssl;
            else
                baseConfig.SecurityProtocol = SecurityProtocol.Plaintext;
        }
        return baseConfig;
    }

    private static ProducerConfig GetProducerconfig(IConfiguration config)
    {
        var baseConfig = GetClientConfig(config);
        return new ProducerConfig(baseConfig)
        {
            MessageSendMaxRetries = 10,
            Acks = Acks.All,
            LingerMs = 100,
            BatchNumMessages = 1000,
            CompressionType = CompressionType.Lz4
        };
    }

    /// <summary>
    /// Get a ConsumerConfig with proper mTLS/SASL security settings from configuration.
    /// Used for creating consumer clients that need to connect with the same security as admin/producer clients.
    /// </summary>
    /// <param name="config">IConfiguration section or root config</param>
    /// <param name="groupId">Consumer group ID (optional, can be set after)</param>
    /// <returns>ConsumerConfig with all TLS/SASL settings properly configured</returns>
    public static ConsumerConfig GetConsumerConfig(IConfiguration config, string? groupId = null)
    {
        if (config["BROKERS"] == null)
            config = config.GetSection("KAFKA");
        
        var baseConfig = GetClientConfig(config);
        var consumerConfig = new ConsumerConfig(baseConfig)
        {
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = true,
            StatisticsIntervalMs = 60000
        };
        
        if (!string.IsNullOrEmpty(groupId))
        {
            consumerConfig.GroupId = groupId;
        }
        
        return consumerConfig;
    }

    /// <summary>
    /// Builds a producer. With <c>KAFKA:TLS:RELOAD_ON_CHANGE=true</c> and SASL_SSL the result follows client
    /// certificate renewals (see README); <paramref name="configure"/> is then re-applied to every new native producer.
    /// </summary>
    public IProducer<TKey, TRes> BuildProducer<TKey, TRes>(bool serializeToMsgPack = false, Func<ProducerBuilder<TKey, TRes>, ProducerBuilder<TKey, TRes>> configure = null)
    {
        var producerConfig = GetProducerconfig(config);
        if (config.GetValue<bool>("TLS:RELOAD_ON_CHANGE"))
            return BuildRenewingProducer(producerConfig, serializeToMsgPack, configure);
        var builder = new ProducerBuilder<TKey, TRes>(producerConfig);
        if (serializeToMsgPack)
            builder = new ProducerBuilder<TKey, TRes>(producerConfig).SetValueSerializer(SerializerFactory.GetSerializer<TRes>());
        if (configure != null)
            builder = configure.Invoke(builder);
        return builder.Build();
    }

    private IProducer<TKey, TRes> BuildRenewingProducer<TKey, TRes>(ProducerConfig producerConfig, bool serializeToMsgPack,
        Func<ProducerBuilder<TKey, TRes>, ProducerBuilder<TKey, TRes>>? configure)
    {
        ProducerBuilder<TKey, TRes> Recipe(ProducerConfig settings)
        {
            ProducerBuilder<TKey, TRes> builder = new RenewableProducerBuilder<TKey, TRes>(settings);
            if (serializeToMsgPack)
                builder = builder.SetValueSerializer(SerializerFactory.GetSerializer<TRes>());
            return configure == null ? builder : configure.Invoke(builder);
        }
        var probe = Recipe(producerConfig);
        var reason = NonRenewableReason(producerConfig, probe);
        if (reason == null)
            return RenewingProducer<TKey, TRes>.Create(root, GetClientConfig(config), producerConfig, settings => Recipe(settings).Build(), _logger);
        if (renewalWarnings.TryAdd(reason, true))
            _logger.LogWarning("Kafka producer TLS renewal disabled ({Reason}); this producer keeps its certificate until restart.", reason);
        return probe.Build();
    }

    internal static string? NonRenewableReason<TKey, TRes>(ProducerConfig settings, ProducerBuilder<TKey, TRes> builder)
    {
        if (settings.SecurityProtocol != SecurityProtocol.SaslSsl) return "not SASL_SSL";
        if (builder is not RenewableProducerBuilder<TKey, TRes> recipe) return "configure replaced the builder";
        // Swapping the native handle mid-transaction would break transactional semantics.
        if (!string.IsNullOrEmpty(recipe.Setting("transactional.id"))) return "transactional.id is set";
        // Without the background poll thread a retired handle never serves its delivery reports.
        var backgroundPoll = recipe.Setting("dotnet.producer.enable.background.poll");
        return string.Equals(backgroundPoll, "false", StringComparison.OrdinalIgnoreCase) ? "manual poll" : null;
    }
}
