using System.Collections.Concurrent;
using Coflnet.Kafka;
using Confluent.Kafka;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace Coflnet.Core.Tests;

// Producer and ConsumeBatch renewal; shares the certificate fixture of KafkaTlsRotationTests.
public partial class KafkaTlsRotationTests
{
    private RenewingProducer<string, string> RenewingProducer(FakeProducers producers, ILogger log,
        Func<ClientConfig, Task>? preflight = null, Func<TimeSpan, CancellationToken, Task>? delay = null,
        ProducerConfig? settings = null)
    {
        settings ??= new ProducerConfig(baseline.ToDictionary(item => item.Key, item => item.Value)) { Acks = Acks.All };
        return RenewingProducer<string, string>.Create(configuration, baseline, settings, producers.Build, log,
            preflight ?? (_ => Task.CompletedTask), delay);
    }

    [Test]
    public async Task ProducerRenewalUsesTheNewHandleAndFlushesThenDisposesTheOldOneOnce()
    {
        var log = new FailureLog();
        var producers = new FakeProducers();
        using var producer = RenewingProducer(producers, log);
        producer.Produce("settings", new Message<string, string> { Value = "before" });
        var first = producers[0];
        Publish();
        await log.Activated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await producer.Retiring.WaitAsync(TimeSpan.FromSeconds(5));
        producer.Produce("settings", new Message<string, string> { Value = "after" });
        var second = producers[1];
        Assert.Multiple(() =>
        {
            Assert.That(producers.Count, Is.EqualTo(2));
            Assert.That(first.Produced, Is.EqualTo(1));
            Assert.That(second.Produced, Is.EqualTo(1));
            Assert.That(first.Events, Is.EqualTo(new[] { "flush", "dispose" }));
            Assert.That(second.Config.SslCertificatePem, Is.EqualTo(File.ReadAllText(Path.Combine(directory, "client.crt"))));
            Assert.That(second.Config.SslCertificatePem, Is.Not.EqualTo(first.Config.SslCertificatePem));
            Assert.That(second.Config.SslCertificateLocation, Is.Null);
            Assert.That(second.Config.Acks, Is.EqualTo(Acks.All));
            Assert.That(producer.Name, Is.EqualTo("fake-2"));
        });
        producer.Dispose();
        Assert.That(second.Disposals, Is.EqualTo(1));
        Assert.That(first.Disposals, Is.EqualTo(1));
    }

    [Test]
    public async Task RetiredProducerFlushIsBoundedByItsDeliveryTimeoutAndReportsLeftovers()
    {
        Assert.That(RenewingProducer<string, string>.RetireFlushBound(new ProducerConfig()), Is.EqualTo(TimeSpan.FromMilliseconds(305005)));
        var log = new FailureLog();
        var producers = new FakeProducers { FirstFlushLeftover = 3 };
        var settings = new ProducerConfig(baseline.ToDictionary(item => item.Key, item => item.Value)) { MessageTimeoutMs = 20000, LingerMs = 100 };
        using var producer = RenewingProducer(producers, log, settings: settings);
        Publish();
        await log.Activated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await producer.Retiring.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(producers[0].LastFlushTimeout, Is.EqualTo(TimeSpan.FromMilliseconds(25100)), "Not a fixed 30 s that drops slow deliveries.");
        Assert.That(log.Message, Does.Contain("3 undelivered"));
    }

    [Test]
    public async Task FlushWithInfiniteTimeoutWaitsForRetiringProducers()
    {
        var log = new FailureLog();
        var delivery = new TaskCompletionSource<DeliveryResult<string, string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var producers = new FakeProducers { FirstDelivery = delivery };
        using var producer = RenewingProducer(producers, log);
        var inflight = producer.ProduceAsync("settings", new Message<string, string> { Value = "inflight" });
        Publish();
        await log.Activated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var flushing = Task.Run(() => producer.Flush(Timeout.InfiniteTimeSpan));
        await Task.Delay(300);
        Assert.That(flushing.IsCompleted, Is.False, "A negative timeout must not skip the retiring producer.");
        delivery.SetResult(new DeliveryResult<string, string>());
        await flushing.WaitAsync(TimeSpan.FromSeconds(5));
        await inflight;
        Assert.That(producers[0].Disposals, Is.EqualTo(1));
    }

    [Test]
    public async Task ProduceRacingRenewalsNeverUsesADisposedHandleAndInflightDeliveryDelaysRetirement()
    {
        var log = new FailureLog();
        var delivery = new TaskCompletionSource<DeliveryResult<string, string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var producers = new FakeProducers { FirstDelivery = delivery };
        using var producer = RenewingProducer(producers, log);
        var inflight = producer.ProduceAsync("settings", new Message<string, string> { Value = "inflight" });
        var calls = 0L;
        var running = true;
        var racers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            while (Volatile.Read(ref running))
            {
                producer.Produce("settings", new Message<string, string> { Value = "race" });
                Interlocked.Increment(ref calls);
            }
        })).ToArray();
        for (var generation = 2; generation <= 4; generation++)
        {
            Publish();
            await Until(() => producer.Name == "fake-" + generation);
        }
        Volatile.Write(ref running, false);
        await Task.WhenAll(racers).WaitAsync(TimeSpan.FromSeconds(5));
        await Until(() => producers[1].Disposals == 1 && producers[2].Disposals == 1);
        var first = producers[0];
        Assert.That(first.Flushes + first.Disposals, Is.Zero, "An in-flight ProduceAsync keeps its handle alive.");
        delivery.SetResult(new DeliveryResult<string, string>());
        await inflight.WaitAsync(TimeSpan.FromSeconds(5));
        await producer.Retiring.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Multiple(() =>
        {
            Assert.That(producers.All.Sum(p => p.Produced), Is.EqualTo(Interlocked.Read(ref calls) + 1), "Each call lands on exactly one handle.");
            Assert.That(producers.All.Sum(p => p.UsedWhileDisposed), Is.Zero);
            Assert.That(producers.All.Take(3).Select(p => p.Disposals), Is.EqualTo(new[] { 1, 1, 1 }));
            Assert.That(producers[3].Disposals, Is.Zero);
        });
    }

    [Test]
    public async Task SaslCredentialsAndBrokersAreReappliedToRenewedHandlesAndUsedForPreflight()
    {
        var log = new FailureLog();
        var producers = new FakeProducers();
        ClientConfig? preflighted = null;
        using var producer = RenewingProducer(producers, log, candidate => { preflighted = candidate; return Task.CompletedTask; });
        producer.AddBrokers("127.0.0.1:2");
        producer.AddBrokers("127.0.0.1:2");
        producer.SetSaslCredentials("rotated-user", "rotated-password");
        Publish();
        await log.Activated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var expected = new[] { "brokers:127.0.0.1:2", "sasl:rotated-user" };
        Assert.Multiple(() =>
        {
            Assert.That(producers[0].Events.Take(3), Is.EqualTo(new[] { expected[0], expected[0], expected[1] }));
            Assert.That(producers[1].Events, Is.EqualTo(expected), "Reapplied once each, before the handle is exposed.");
            Assert.That(preflighted?.SaslUsername, Is.EqualTo("rotated-user"));
            Assert.That(preflighted?.SaslPassword, Is.EqualTo("rotated-password"));
            Assert.That(log.Message, Does.Not.Contain("rotated-password"));
        });
    }

    [TestCase("expired")]
    [TestCase("preflight")]
    [TestCase("build")]
    public async Task RejectedProducerRenewalKeepsTheCurrentProducer(string failure)
    {
        var log = new FailureLog();
        var retries = new RetryDelays();
        var producers = new FakeProducers { FailBuild = index => failure == "build" && index == 1 };
        using var producer = RenewingProducer(producers, log,
            _ => failure == "preflight" ? Task.FromException(new TimeoutException("not logged")) : Task.CompletedTask, retries.Delay);
        Publish(expired: failure == "expired");
        await log.Failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        producer.Produce("settings", new Message<string, string> { Value = "kept" });
        Assert.Multiple(() =>
        {
            Assert.That(producer.Name, Is.EqualTo("fake-1"));
            Assert.That(producers[0].Produced, Is.EqualTo(1));
            Assert.That(producers[0].Disposals + producers[0].Flushes, Is.Zero);
            Assert.That(log.Message, Does.Not.Contain("not logged").And.Not.Contain("PRIVATE KEY"));
        });
        if (failure != "build") return;
        Assert.That(log.Message, Does.Contain("current producer retained"));
        (await retries.Next()).Release.SetResult();
        await log.Activated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(producer.Name, Is.EqualTo("fake-3"), "The validated snapshot is retried without a file change.");
    }

    [Test]
    public void BuildProducerReturnsThePlainProducerWithoutOptIn()
    {
        var creator = new KafkaCreator(Mock.Of<ILogger<KafkaCreator>>(), CreatorConfiguration(reload: false));
        using var producer = creator.BuildProducer<string, string>(configure: builder => builder.SetLogHandler((_, _) => { }));
        Assert.That(producer, Is.Not.InstanceOf<RenewingProducer<string, string>>());
    }

    [Test]
    public void BuildProducerReturnsARenewingProducerAndReappliesConfigureWithOptIn()
    {
        var creator = new KafkaCreator(Mock.Of<ILogger<KafkaCreator>>(), CreatorConfiguration(reload: true));
        var configured = 0;
        using var producer = creator.BuildProducer<string, string>(configure: builder =>
        {
            configured++;
            return builder.SetLogHandler((_, _) => { });
        });
        Assert.That(producer, Is.InstanceOf<RenewingProducer<string, string>>());
        Assert.That(configured, Is.EqualTo(2), "Once to check renewability, once for the native producer.");
    }

    [Test]
    public void ProducerWhoseConfigureReplacesTheBuilderIsNotRotatedAndWarnsOnce()
    {
        KafkaCreator.renewalWarnings.Clear();
        var logger = new Mock<ILogger<KafkaCreator>>();
        var creator = new KafkaCreator(logger.Object, CreatorConfiguration(reload: true));
        ProducerBuilder<string, string> Transactional(ProducerBuilder<string, string> _) =>
            new ProducerBuilder<string, string>(new ProducerConfig { BootstrapServers = "127.0.0.1:1", TransactionalId = "synthetic-tx" })
                .SetLogHandler((_, _) => { });
        using (var producer = creator.BuildProducer<string, string>(configure: Transactional))
            Assert.That(producer, Is.Not.InstanceOf<RenewingProducer<string, string>>());
        using (creator.BuildProducer<string, string>(configure: Transactional)) { }
        logger.Verify(l => l.Log(LogLevel.Warning, It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(), It.IsAny<Exception?>(),
            (Func<It.IsAnyType, Exception?, string>)It.IsAny<object>()), Times.Once);
    }

    [Test]
    public void ProducersThatCannotBeSwappedSafelyAreNotRenewable()
    {
        string? Reason(Action<ProducerConfig> change, bool replaceBuilder = false)
        {
            var settings = new ProducerConfig(baseline.ToDictionary(item => item.Key, item => item.Value));
            change(settings);
            ProducerBuilder<string, string> builder = replaceBuilder
                ? new ProducerBuilder<string, string>(settings) : new RenewableProducerBuilder<string, string>(settings);
            return KafkaCreator.NonRenewableReason(settings, builder);
        }
        Assert.Multiple(() =>
        {
            Assert.That(Reason(_ => { }), Is.Null);
            Assert.That(Reason(s => s.SecurityProtocol = SecurityProtocol.Ssl), Is.EqualTo("not SASL_SSL"));
            Assert.That(Reason(_ => { }, replaceBuilder: true), Is.EqualTo("configure replaced the builder"));
            Assert.That(Reason(s => s.TransactionalId = "synthetic-tx"), Is.EqualTo("transactional.id is set"));
            Assert.That(Reason(s => s.EnableBackgroundPoll = false), Is.EqualTo("manual poll"), "A retired handle could never drain.");
        });
    }

    private ConsumerConfig RenewableBatchSettings(IConfiguration settings, bool explicitCommit = true)
    {
        var result = new ConsumerConfig(KafkaCreator.GetClientConfig(settings).ToDictionary(item => item.Key, item => item.Value))
        {
            GroupId = "flipbase", PartitionAssignmentStrategy = PartitionAssignmentStrategy.CooperativeSticky
        };
        if (explicitCommit) { result.EnableAutoCommit = false; result.EnableAutoOffsetStore = false; }
        return result;
    }

    [Test]
    public async Task BatchConsumerRotatesOnlyAfterTheInflightBatchAndItsCommit()
    {
        var events = new ConcurrentQueue<string>();
        var offset = 41L;
        ConsumeResult<Ignore, string> Next() => new()
        {
            Topic = "settings", Partition = 0, Offset = Interlocked.Increment(ref offset), Message = new() { Value = "synthetic" }
        };
        var consumer = new Mock<IConsumer<Ignore, string>>();
        consumer.Setup(c => c.Consume(It.IsAny<CancellationToken>())).Returns(Next);
        consumer.Setup(c => c.Consume(It.IsAny<TimeSpan>())).Returns(Next);
        consumer.Setup(c => c.Commit(It.IsAny<IEnumerable<TopicPartitionOffset>>())).Callback<IEnumerable<TopicPartitionOffset>>(
            offsets => events.Enqueue("commit " + offsets.Single().Offset.Value));
        consumer.Setup(c => c.Close()).Callback(() => events.Enqueue("close"));
        var processing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var log = new FailureLog();
        var builds = 0;
        var creatorConfiguration = CreatorConfiguration(reload: true);
        var owner = new KafkaConsumer(log, creatorConfiguration);
        var consuming = Task.Run(() => owner.ConsumeBatchRenewing<string>(RenewableBatchSettings(creatorConfiguration), ["settings"], async batch =>
        {
            var size = batch.Count();
            events.Enqueue("batch " + size);
            if (size < 3) return;
            processing.TrySetResult();
            await release.Task;
        }, stop.Token, 3, preflight: _ => Task.CompletedTask, createConsumer: (_, ready) =>
        {
            if (++builds == 1) { ready.TrySetResult(); return consumer.Object; }
            events.Enqueue("build candidate");
            return IdleConsumer(ready).Object;
        }));
        await processing.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Publish();
        await log.Activated.Task.WaitAsync(TimeSpan.FromMilliseconds(500)).ContinueWith(_ => { });
        Assert.That(events, Is.EqualTo(new[] { "batch 1", "commit 43", "batch 2", "commit 45", "batch 3" }));
        release.TrySetResult();
        await log.Activated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(events, Is.EqualTo(new[] { "batch 1", "commit 43", "batch 2", "commit 45", "batch 3", "commit 48", "build candidate", "close" }));
        stop.Cancel();
        await consuming.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task BatchHandoffFailureNeverAutoCommitsBufferedRecordsAndReplaysThem()
    {
        var creatorConfiguration = CreatorConfiguration(reload: true);
        var log = new FailureLog();
        var owner = new KafkaConsumer(log, creatorConfiguration);
        var configs = new ConcurrentQueue<ConsumerConfig>();
        var firstReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var old = IdleConsumer(firstReady);
        var replacement = new Mock<IConsumer<Ignore, string>>();
        var handled = 0;
        var builds = 0;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var callerSettings = RenewableBatchSettings(creatorConfiguration, explicitCommit: false); // auto-commit left at librdkafka's default (on)
        var consuming = Task.Run(() => owner.ConsumeBatchRenewing<string>(callerSettings, ["settings"],
            _ => { handled++; return Task.CompletedTask; }, stop.Token, 3, preflight: _ => Task.CompletedTask,
            createConsumer: (config, ready) =>
            {
                configs.Enqueue(config);
                if (++builds == 1) { ready.TrySetResult(); return old.Object; }
                if (builds > 2) return IdleConsumer(ready).Object;
                replacement.Setup(c => c.Consume(It.IsAny<CancellationToken>())).Returns(() =>
                {
                    ready.TrySetException(new KafkaException(new Error(ErrorCode.GroupAuthorizationFailed)));
                    return new() { Topic = "settings", Partition = 0, Offset = 42, Message = new() { Value = "buffered" } };
                });
                return replacement.Object;
            }));
        await firstReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Publish();
        await log.Failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Multiple(() =>
        {
            Assert.That(log.Message, Does.Contain("after Close"), "The previous generation was rebuilt so the record replays.");
            Assert.That(configs.Select(c => (c.EnableAutoCommit, c.EnableAutoOffsetStore)), Has.All.EqualTo(((bool?)false, (bool?)false)));
            Assert.That(callerSettings.EnableAutoCommit, Is.Null, "The caller's config is copied, not mutated.");
            Assert.That(handled, Is.Zero);
        });
        replacement.Verify(c => c.Commit(It.IsAny<IEnumerable<TopicPartitionOffset>>()), Times.Never);
        replacement.Verify(c => c.Close(), Times.Once);
        stop.Cancel();
        await consuming.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task BatchConsumerWithDifferentConnectionSettingsFallsBackToThePlainPathAndWarnsOnce()
    {
        KafkaConsumer.renewalWarnings.Clear();
        var creatorConfiguration = CreatorConfiguration(reload: true);
        var log = new FailureLog();
        var owner = new KafkaConsumer(log, creatorConfiguration);
        var callerSettings = RenewableBatchSettings(creatorConfiguration);
        callerSettings.BootstrapServers = "127.0.0.1:3";
        var consumer = new Mock<IConsumer<Ignore, string>>();
        consumer.Setup(c => c.Consume(It.IsAny<CancellationToken>())).Returns(new ConsumeResult<Ignore, string>
        {
            Topic = "settings", Partition = 0, Offset = 42, Message = new() { Value = "synthetic" }
        });
        ConsumerConfig? used = null;
        var preflights = 0;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await owner.ConsumeBatchRenewing<string>(callerSettings, ["settings"], _ => { stop.Cancel(); return Task.CompletedTask; },
            stop.Token, 1, preflight: _ => { preflights++; return Task.CompletedTask; },
            createConsumer: (config, ready) => { used = config; ready.TrySetResult(); return consumer.Object; });
        Assert.Multiple(() =>
        {
            Assert.That(used?.BootstrapServers, Is.EqualTo("127.0.0.1:3"));
            Assert.That(used?.SslCertificatePem, Is.Null, "No renewal snapshot on the plain path.");
            Assert.That(preflights, Is.Zero);
            Assert.That(log.Message, Does.Contain("renewal disabled"));
        });
        consumer.Verify(c => c.Close(), Times.Never);
        Assert.That(KafkaConsumer.renewalWarnings.Count, Is.EqualTo(1));
    }

    [Test]
    public void ConsumeBatchWithOptInFailsClosedWithoutSaslSsl()
    {
        var owner = new KafkaConsumer(new FailureLog(), configuration);
        var failure = Assert.ThrowsAsync<InvalidOperationException>(() =>
            owner.ConsumeBatch<string>(["settings"], _ => Task.CompletedTask, CancellationToken.None));
        Assert.That(failure!.Message, Does.Contain("(InvalidOperationException); no Kafka client was started"));
    }

    [Test]
    public async Task DisposalRacingARejectedReadDoesNotFaultDisposeAsync()
    {
        var clock = new GatedClock();
        var rotation = await KafkaTlsRotation.CreateAsync(configuration, baseline, ["settings"], NullLogger.Instance,
            _ => Task.CompletedTask, clock, watch: false);
        Publish();
        clock.Armed = true;
        rotation.SignalChange();
        Assert.That(clock.Entered.Wait(TimeSpan.FromSeconds(5)), Is.True);
        var disposing = rotation.DisposeAsync().AsTask();
        clock.Release.Set(); // the read now judges the leaf expired, after disposal began
        Assert.DoesNotThrowAsync(() => disposing.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private sealed class GatedClock : TimeProvider
    {
        internal volatile bool Armed;
        internal readonly ManualResetEventSlim Entered = new();
        internal readonly ManualResetEventSlim Release = new();

        public override DateTimeOffset GetUtcNow()
        {
            if (!Armed) return DateTimeOffset.UtcNow;
            Armed = false;
            Entered.Set();
            Release.Wait(TimeSpan.FromSeconds(10));
            return DateTimeOffset.UtcNow.AddDays(30);
        }
    }

    private IConfiguration CreatorConfiguration(bool reload) => new ConfigurationBuilder().AddConfiguration(configuration)
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["KAFKA:TLS:RELOAD_ON_CHANGE"] = reload ? "true" : "false",
            ["KAFKA:BROKERS"] = "127.0.0.1:1", // closed loopback port; never a real broker
            ["KAFKA:USERNAME"] = "synthetic-user", ["KAFKA:PASSWORD"] = "synthetic-password"
        }).Build();

    private static async Task Until(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(10, deadline.Token);
    }

    private sealed class FakeProducers
    {
        private readonly List<FakeProducer> built = new();
        internal TaskCompletionSource<DeliveryResult<string, string>>? FirstDelivery;
        internal int FirstFlushLeftover;
        internal Func<int, bool> FailBuild = _ => false;
        internal FakeProducer this[int index] { get { lock (built) return built[index]; } }
        internal int Count { get { lock (built) return built.Count; } }
        internal FakeProducer[] All { get { lock (built) return built.ToArray(); } }

        internal IProducer<string, string> Build(ProducerConfig config)
        {
            lock (built)
            {
                var index = built.Count;
                if (FailBuild(index)) { built.Add(new FakeProducer(config, "failed", null, 0)); throw new InvalidOperationException("synthetic build failure"); }
                var producer = index == 0
                    ? new FakeProducer(config, "fake-1", FirstDelivery, FirstFlushLeftover)
                    : new FakeProducer(config, "fake-" + (index + 1), null, 0);
                built.Add(producer);
                return producer;
            }
        }
    }

    private sealed class FakeProducer(ProducerConfig config, string name,
        TaskCompletionSource<DeliveryResult<string, string>>? delivery, int flushLeftover) : IProducer<string, string>
    {
        internal TimeSpan LastFlushTimeout;
        internal readonly ProducerConfig Config = config;
        internal readonly ConcurrentQueue<string> Events = new();
        internal int Produced;
        internal int Flushes;
        internal int Disposals;
        internal int UsedWhileDisposed;
        private int inside;

        public Handle Handle => throw new NotSupportedException();
        public string Name => name;

        private void Track(Action body)
        {
            Interlocked.Increment(ref inside);
            if (Volatile.Read(ref Disposals) > 0) Interlocked.Increment(ref UsedWhileDisposed);
            body();
            Thread.SpinWait(20);
            if (Volatile.Read(ref Disposals) > 0) Interlocked.Increment(ref UsedWhileDisposed);
            Interlocked.Decrement(ref inside);
        }

        public Task<DeliveryResult<string, string>> ProduceAsync(string topic, Message<string, string> message, CancellationToken cancellationToken = default)
        {
            Track(() => Interlocked.Increment(ref Produced));
            return delivery?.Task ?? Task.FromResult(new DeliveryResult<string, string>());
        }

        public Task<DeliveryResult<string, string>> ProduceAsync(TopicPartition topicPartition, Message<string, string> message, CancellationToken cancellationToken = default)
            => ProduceAsync(topicPartition.Topic, message, cancellationToken);

        public void Produce(string topic, Message<string, string> message, Action<DeliveryReport<string, string>>? deliveryHandler = null)
            => Track(() => Interlocked.Increment(ref Produced));

        public void Produce(TopicPartition topicPartition, Message<string, string> message, Action<DeliveryReport<string, string>>? deliveryHandler = null)
            => Produce(topicPartition.Topic, message, deliveryHandler);

        public int Poll(TimeSpan timeout) => 0;

        public int Flush(TimeSpan timeout)
        {
            Track(() => { Interlocked.Increment(ref Flushes); Events.Enqueue("flush"); });
            LastFlushTimeout = timeout;
            return flushLeftover;
        }

        public void Flush(CancellationToken cancellationToken = default) => Flush(TimeSpan.Zero);
        public void InitTransactions(TimeSpan timeout) => throw new NotSupportedException();
        public void BeginTransaction() => throw new NotSupportedException();
        public void CommitTransaction(TimeSpan timeout) => throw new NotSupportedException();
        public void CommitTransaction() => throw new NotSupportedException();
        public void AbortTransaction(TimeSpan timeout) => throw new NotSupportedException();
        public void AbortTransaction() => throw new NotSupportedException();
        public void SendOffsetsToTransaction(IEnumerable<TopicPartitionOffset> offsets, IConsumerGroupMetadata groupMetadata, TimeSpan timeout)
            => throw new NotSupportedException();

        public int AddBrokers(string brokers)
        {
            Events.Enqueue("brokers:" + brokers);
            return 1;
        }

        public void SetSaslCredentials(string username, string password) => Events.Enqueue("sasl:" + username);

        public void Dispose()
        {
            if (Volatile.Read(ref inside) > 0) Interlocked.Increment(ref UsedWhileDisposed);
            Interlocked.Increment(ref Disposals);
            Events.Enqueue("dispose");
        }
    }
}
