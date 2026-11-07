using Coflnet.Kafka;
using Confluent.Kafka;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace Coflnet.Core.Tests;

// KafkaConsumerRenewal: the public handoff for consume loops that build their own consumer.
public partial class KafkaTlsRotationTests
{
    private static ConsumeResult<Ignore, string> Record(long offset) =>
        new() { Topic = "settings", Partition = 0, Offset = offset, Message = new() { Value = "synthetic" } };

    private Task<KafkaConsumerRenewal<Ignore, string>?> PublicRenewal(bool reload,
        Func<ConsumerConfig, KafkaConsumerReadiness, IConsumer<Ignore, string>> build, Action<ConsumerConfig>? change = null)
    {
        var settings = CreatorConfiguration(reload);
        var callerSettings = RenewableBatchSettings(settings, explicitCommit: false);
        change?.Invoke(callerSettings);
        return KafkaConsumerRenewal<Ignore, string>.CreateAsync(settings, callerSettings, ["settings"], build,
            NullLogger.Instance, _ => Task.CompletedTask);
    }

    // Assigns as soon as it subscribes; records it returns before that are buffered by the renewal.
    private static Mock<IConsumer<Ignore, string>> AssigningConsumer(KafkaConsumerReadiness readiness, ConsumeResult<Ignore, string>? fetchedFirst = null)
    {
        var consumer = new Mock<IConsumer<Ignore, string>>();
        consumer.Setup(c => c.Subscribe(It.IsAny<IEnumerable<string>>())).Callback(() =>
        {
            if (fetchedFirst is null) readiness.PartitionsAssigned();
        });
        consumer.Setup(c => c.Consume(It.IsAny<CancellationToken>())).Returns(() =>
        {
            readiness.PartitionsAssigned();
            return fetchedFirst!;
        });
        return consumer;
    }

    [Test]
    public async Task PublicRenewalIsNullWithoutOptInOrForADifferentConnection()
    {
        KafkaConsumer.renewalWarnings.Clear();
        var builds = 0;
        IConsumer<Ignore, string> Build(ConsumerConfig _, KafkaConsumerReadiness __) { builds++; throw new AssertionException("No build expected."); }
        Assert.That(await PublicRenewal(reload: false, Build), Is.Null);
        Assert.That(await PublicRenewal(reload: true, Build, settings => settings.BootstrapServers = "127.0.0.1:3"), Is.Null);
        Assert.That(await PublicRenewal(reload: true, Build, settings => settings.SaslUsername = "other-user"), Is.Null,
            "Preflight would authenticate as KAFKA:USERNAME, not as the caller.");
        Assert.That(builds, Is.Zero);
        Assert.That(KafkaConsumer.renewalWarnings.Count, Is.EqualTo(1));
    }

    [Test]
    public async Task PublicHandoffWithoutCandidateReturnsTheSameHandleWithoutBuilding()
    {
        var builds = 0;
        Mock<IConsumer<Ignore, string>>? first = null;
        await using var renewal = await PublicRenewal(reload: true, (_, readiness) =>
        {
            builds++;
            first = AssigningConsumer(readiness);
            return first.Object;
        });
        var started = await renewal!.StartAsync(_ => Assert.Fail("Nothing was fetched."), CancellationToken.None);
        var continued = await renewal.HandoffAsync(started, _ => Assert.Fail("Nothing was fetched."), CancellationToken.None);
        Assert.That(continued, Is.SameAs(started));
        Assert.That(builds, Is.EqualTo(1));
        first!.Verify(c => c.Close(), Times.Never);
    }

    [Test]
    public async Task PublicHandoffClosesTheOldHandleAndDeliversOnlyRecordsOfTheReplacement()
    {
        var configs = new List<ConsumerConfig>();
        var handles = new List<Mock<IConsumer<Ignore, string>>>();
        await using var renewal = await PublicRenewal(reload: true, (config, readiness) =>
        {
            configs.Add(config);
            handles.Add(AssigningConsumer(readiness, handles.Count == 0 ? null : Record(42)));
            return handles[^1].Object;
        });
        var started = await renewal!.StartAsync(_ => Assert.Fail("Nothing was fetched."), CancellationToken.None);
        Publish();
        await Task.Delay(Timeout.Infinite, renewal.HandoffRequested).ContinueWith(_ => { }).WaitAsync(TimeSpan.FromSeconds(5));
        var buffered = new List<long>();
        var continued = await renewal.HandoffAsync(started, record => buffered.Add(record.Offset.Value), CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(continued, Is.SameAs(handles[1].Object));
            Assert.That(buffered, Is.EqualTo(new long[] { 42 }), "The record fetched while assigning must be processed.");
            Assert.That(configs[1].SslCertificatePem, Is.EqualTo(File.ReadAllText(Path.Combine(directory, "client.crt"))));
            Assert.That(configs[1].SslCertificatePem, Is.Not.EqualTo(configs[0].SslCertificatePem));
            Assert.That(configs.Select(c => (c.EnableAutoCommit, c.EnableAutoOffsetStore)), Has.All.EqualTo(((bool?)false, (bool?)false)));
            Assert.That(renewal.HandoffRequested.IsCancellationRequested, Is.False);
        });
        handles[0].Verify(c => c.Close(), Times.Once);
        handles[0].Verify(c => c.Dispose(), Times.Once);
        handles[1].Verify(c => c.Close(), Times.Never);
    }

    [Test]
    public async Task PublicHandoffRetainsTheCurrentHandleWhenTheCandidateFailsBeforeClose()
    {
        var handles = new List<Mock<IConsumer<Ignore, string>>>();
        await using var renewal = await PublicRenewal(reload: true, (_, readiness) =>
        {
            if (handles.Count == 1) readiness.ReportError(new Error(ErrorCode.Local_Ssl));
            handles.Add(AssigningConsumer(readiness));
            return handles[^1].Object;
        });
        var started = await renewal!.StartAsync(_ => { }, CancellationToken.None);
        Publish();
        await Task.Delay(Timeout.Infinite, renewal.HandoffRequested).ContinueWith(_ => { }).WaitAsync(TimeSpan.FromSeconds(5));
        var continued = await renewal.HandoffAsync(started, _ => Assert.Fail("The failed candidate's records are not delivered."), CancellationToken.None);
        Assert.That(continued, Is.SameAs(started));
        handles[0].Verify(c => c.Close(), Times.Never);
        handles[1].Verify(c => c.Close(), Times.Once);
        handles[1].Verify(c => c.Dispose(), Times.Once);
    }

    [Test]
    public void ReadinessFailsOnlyOnFatalAuthenticationAuthorizationOrTransportErrors()
    {
        var readiness = new KafkaConsumerReadiness();
        readiness.ReportError(new Error(ErrorCode.Local_TimedOut));
        Assert.That(readiness.Completion.Task.IsCompleted, Is.False);
        readiness.ReportError(new Error(ErrorCode.TopicAuthorizationFailed));
        Assert.That(readiness.Completion.Task.IsFaulted, Is.True);
    }

    [Test]
    public async Task PublicStartClosesTheHandleWhenTheBufferedCallbackThrows()
    {
        Mock<IConsumer<Ignore, string>>? first = null;
        await using var renewal = await PublicRenewal(reload: true, (_, readiness) =>
        {
            first = AssigningConsumer(readiness, Record(42));
            return first.Object;
        });
        Assert.ThrowsAsync<InvalidOperationException>(() =>
            renewal!.StartAsync(_ => throw new InvalidOperationException("synthetic callback failure"), CancellationToken.None));
        first!.Verify(c => c.Close(), Times.Once);
        first.Verify(c => c.Dispose(), Times.Once);
    }

    [Test]
    public async Task PublicHandoffClosesTheAcceptedReplacementWhenTheBufferedCallbackThrows()
    {
        var handles = new List<Mock<IConsumer<Ignore, string>>>();
        await using var renewal = await PublicRenewal(reload: true, (_, readiness) =>
        {
            handles.Add(AssigningConsumer(readiness, handles.Count == 0 ? null : Record(42)));
            return handles[^1].Object;
        });
        var started = await renewal!.StartAsync(_ => { }, CancellationToken.None);
        Publish();
        await Task.Delay(Timeout.Infinite, renewal.HandoffRequested).ContinueWith(_ => { }).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.ThrowsAsync<InvalidOperationException>(() => renewal.HandoffAsync(started,
            _ => throw new InvalidOperationException("synthetic callback failure"), CancellationToken.None));
        Assert.That(handles, Has.Count.EqualTo(2), "No recovery handle: the candidate was accepted.");
        foreach (var handle in handles)
        {
            handle.Verify(c => c.Close(), Times.Once);
            handle.Verify(c => c.Dispose(), Times.Once);
        }
    }

    [Test]
    public async Task RenewingConsumeBacksOffFastFailuresAndResetsAfterAProcessedRecord()
    {
        var waits = new List<TimeSpan>();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var builds = 0;
        var handled = 0;
        var owner = new KafkaConsumer(new FailureLog(), CreatorConfiguration(reload: true));
        await owner.ConsumeRenewing<string>("settings", _ => { handled++; return Task.CompletedTask; }, stop.Token, "flipbase",
            AutoOffsetReset.Earliest, null, preflight: _ => Task.CompletedTask,
            delay: (wait, _) => { waits.Add(wait); return Task.CompletedTask; },
            createConsumer: (_, ready) =>
            {
                if (++builds <= 3) throw new InvalidOperationException("synthetic fast failure");
                if (builds == 5) { stop.Cancel(); return IdleConsumer(ready).Object; }
                var consumer = new Mock<IConsumer<Ignore, string>>();
                consumer.Setup(c => c.Subscribe(It.IsAny<IEnumerable<string>>())).Callback(() => ready.TrySetResult());
                consumer.SetupSequence(c => c.Consume(It.IsAny<CancellationToken>()))
                    .Returns(Record(42)).Throws(new InvalidOperationException("synthetic consume failure"));
                return consumer.Object;
            }).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(handled, Is.EqualTo(1));
        Assert.That(waits.Select(w => w.TotalSeconds), Is.EqualTo(new double[] { 2, 4, 8, 2 }));
    }

    [Test]
    public async Task RebuildBackoffIsCappedAtOneMinute()
    {
        var waits = new List<TimeSpan>();
        var backoff = new KafkaConsumer.RebuildBackoff((wait, _) => { waits.Add(wait); return Task.CompletedTask; });
        for (var i = 0; i < 8; i++) await backoff.WaitAsync(CancellationToken.None);
        Assert.That(waits.Select(w => w.TotalSeconds), Is.EqualTo(new double[] { 2, 4, 8, 16, 32, 60, 60, 60 }));
    }
}
