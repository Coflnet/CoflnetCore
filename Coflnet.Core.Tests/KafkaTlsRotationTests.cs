using System.Collections.Concurrent;
using System.Threading.Channels;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Confluent.Kafka.Admin;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Coflnet.Kafka;
using Confluent.Kafka;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace Coflnet.Core.Tests;

[NonParallelizable]
[Platform("Linux")]
public class KafkaTlsRotationTests
{
    private string directory = null!;
    private X509Certificate2 root = null!;
    private X509Certificate2 intermediate = null!;
    private IConfiguration configuration = null!;
    private int generation;
    private readonly ClientConfig baseline = new()
    {
        BootstrapServers = "localhost:19092", SecurityProtocol = SecurityProtocol.SaslSsl,
        SaslMechanism = SaslMechanism.ScramSha256, SaslUsername = "synthetic-user", SaslPassword = "synthetic-password"
    };

    [SetUp]
    public void SetUp()
    {
        directory = Path.Combine(Path.GetTempPath(), "kafka-rotation-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=rotation-root", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        root = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(2));
        using var intermediateKey = RSA.Create(2048);
        var intermediateRequest = new CertificateRequest("CN=rotation-intermediate", intermediateKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        intermediateRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        intermediateRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        using var signed = intermediateRequest.Create(root, DateTimeOffset.UtcNow.AddSeconds(-45), DateTimeOffset.UtcNow.AddDays(1), RandomNumberGenerator.GetBytes(16));
        intermediate = signed.CopyWithPrivateKey(intermediateKey);
        foreach (var name in new[] { "ca.crt", "client.crt", "client.key" })
            File.CreateSymbolicLink(Path.Combine(directory, name), "..data/" + name);
        Publish();
        configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["KAFKA:TLS:RELOAD_ON_CHANGE"] = "true",
            ["KAFKA:TLS:CA_LOCATION"] = Path.Combine(directory, "ca.crt"),
            ["KAFKA:TLS:CERTIFICATE_LOCATION"] = Path.Combine(directory, "client.crt"),
            ["KAFKA:TLS:KEY_LOCATION"] = Path.Combine(directory, "client.key")
        }).Build();
    }

    [TearDown]
    public void TearDown()
    {
        intermediate.Dispose();
        root.Dispose();
        Directory.Delete(directory, true);
    }

    private void Publish(bool expired = false, TimeSpan? validity = null)
    {
        var target = "generation-" + ++generation;
        var path = Path.Combine(directory, target);
        Directory.CreateDirectory(path);
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=settings", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.2") }, true));
        using var certificate = request.Create(intermediate, DateTimeOffset.UtcNow.AddSeconds(-30),
            expired ? DateTimeOffset.UtcNow.AddSeconds(-1) : DateTimeOffset.UtcNow.Add(validity ?? TimeSpan.FromHours(2)), RandomNumberGenerator.GetBytes(16));
        File.WriteAllText(Path.Combine(path, "ca.crt"), root.ExportCertificatePem());
        File.WriteAllText(Path.Combine(path, "client.crt"), certificate.ExportCertificatePem() + "\n" + intermediate.ExportCertificatePem());
        File.WriteAllText(Path.Combine(path, "client.key"), key.ExportPkcs8PrivateKeyPem());
        Directory.CreateSymbolicLink(Path.Combine(directory, "..data_tmp"), target);
        Assert.That(Rename(Path.Combine(directory, "..data_tmp"), Path.Combine(directory, "..data")), Is.Zero);
    }

    [DllImport("libc", EntryPoint = "rename", SetLastError = true)]
    private static extern int Rename(string source, string destination);

    private static ConsumerConfig ConsumerSettings() => new()
    {
        GroupId = "flipbase", EnableAutoCommit = false, EnableAutoOffsetStore = false,
        PartitionAssignmentStrategy = PartitionAssignmentStrategy.CooperativeSticky
    };

    [Test]
    public async Task RotationDrainsTheInflightBatchAndCommitsNextOffsetBeforeClosing()
    {
        await using var rotation = await KafkaTlsRotation.CreateAsync(configuration, baseline, ["settings"],
            NullLogger.Instance, _ => Task.CompletedTask);
        var before = rotation.Configure(ConsumerSettings());
        var events = new ConcurrentQueue<string>();
        var processing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var consumer = new Mock<IConsumer<Ignore, string>>();
        consumer.Setup(c => c.Consume(It.IsAny<CancellationToken>())).Returns(new ConsumeResult<Ignore, string>
        {
            Topic = "settings", Partition = 0, Offset = 42, Message = new() { Value = "synthetic" }
        });
        consumer.Setup(c => c.Commit(It.IsAny<IEnumerable<TopicPartitionOffset>>())).Callback<IEnumerable<TopicPartitionOffset>>(offsets =>
        {
            Assert.That(offsets.Single().Offset.Value, Is.EqualTo(43));
            events.Enqueue("commit");
        });
        consumer.Setup(c => c.Close()).Callback(() => events.Enqueue("close"));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var log = new FailureLog();
        var builds = 0;
        var owner = new KafkaConsumer(log, configuration);
        var consuming = owner.ConsumeBatchCore<string>(before, ["settings"], async _ =>
        {
            events.Enqueue("action"); processing.TrySetResult(); await release.Task;
        }, stop.Token, 1, rotation: rotation, createConsumer: (_, ready) =>
        {
            if (++builds == 1) { ready.TrySetResult(); return consumer.Object; }
            events.Enqueue("build candidate");
            return IdleConsumer(ready).Object;
        });
        await processing.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Publish();
        await Task.Delay(Timeout.Infinite, rotation.RotationToken).ContinueWith(_ => { }).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(events, Is.EqualTo(new[] { "action" }));
        release.TrySetResult();
        await log.Activated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(events, Is.EqualTo(new[] { "action", "commit", "build candidate", "close" }));
        var after = rotation.Configure(ConsumerSettings());
        Assert.Multiple(() =>
        {
            Assert.That(after.SslCertificatePem, Is.Not.EqualTo(before.SslCertificatePem));
            Assert.That(after.GroupId, Is.EqualTo("flipbase"));
            Assert.That(after.EnableAutoCommit, Is.False);
            Assert.That(after.EnableAutoOffsetStore, Is.False);
            Assert.That(after.SslCaLocation, Is.Null);
            Assert.That(after.SslCertificateLocation, Is.Null);
            Assert.That(after.SslKeyLocation, Is.Null);
        });
        stop.Cancel();
        await consuming.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task IdleRotationClosesAndDoesNotDropARecordReturnedDuringCancellation(bool returnRacingRecord)
    {
        await using var rotation = await KafkaTlsRotation.CreateAsync(configuration, baseline, ["settings"],
            NullLogger.Instance, _ => Task.CompletedTask);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var consumer = new Mock<IConsumer<Ignore, string>>();
        consumer.Setup(c => c.Consume(It.IsAny<CancellationToken>())).Returns((CancellationToken token) =>
        {
            entered.TrySetResult(); token.WaitHandle.WaitOne();
            if (!returnRacingRecord) token.ThrowIfCancellationRequested();
            return new() { Topic = "settings", Partition = 0, Offset = 42, Message = new() { Value = "synthetic" } };
        });
        var handled = 0;
        var builds = 0;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var log = new FailureLog();
        var owner = new KafkaConsumer(log, configuration);
        var task = Task.Run(() => owner.ConsumeBatchCore<string>(rotation.Configure(ConsumerSettings()), ["settings"],
            _ => { handled++; return Task.CompletedTask; }, stop.Token, 1,
            rotation: rotation, createConsumer: (_, ready) =>
            {
                if (++builds == 1) { ready.TrySetResult(); return consumer.Object; }
                return IdleConsumer(ready).Object;
            }));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Publish();
        await log.Activated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(handled, Is.EqualTo(returnRacingRecord ? 1 : 0));
        consumer.Verify(c => c.Close(), Times.Once);
        consumer.Verify(c => c.Commit(It.IsAny<IEnumerable<TopicPartitionOffset>>()), returnRacingRecord ? Times.Once() : Times.Never());
        stop.Cancel();
        await task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static Mock<IConsumer<Ignore, string>> IdleConsumer(TaskCompletionSource ready)
    {
        var consumer = new Mock<IConsumer<Ignore, string>>();
        consumer.Setup(c => c.Subscribe(It.IsAny<IEnumerable<string>>())).Callback(() => ready.TrySetResult());
        consumer.Setup(c => c.Consume(It.IsAny<CancellationToken>())).Returns((CancellationToken token) =>
        {
            token.WaitHandle.WaitOne(); token.ThrowIfCancellationRequested();
            throw new AssertionException("Unexpected idle consume continuation.");
        });
        return consumer;
    }

    [Test]
    public async Task ExpiredGenerationRetainsOldCredentialsUntilAValidNewGeneration()
    {
        var log = new FailureLog();
        var calls = 0;
        await using var rotation = await KafkaTlsRotation.CreateAsync(configuration, baseline, ["settings"],
            log, _ => { Interlocked.Increment(ref calls); return Task.CompletedTask; });
        var before = rotation.Configure(ConsumerSettings()).SslCertificatePem;
        Publish(expired: true);
        await log.Failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Multiple(() =>
        {
            Assert.That(rotation.RotationToken.IsCancellationRequested, Is.False);
            Assert.That(rotation.Configure(ConsumerSettings()).SslCertificatePem, Is.EqualTo(before));
            Assert.That(calls, Is.EqualTo(1));
            Assert.That(log.Message, Does.Not.Contain("PRIVATE KEY").And.Not.Contain("synthetic-password"));
        });
        Publish();
        await Task.Delay(Timeout.Infinite, rotation.RotationToken).ContinueWith(_ => { }).WaitAsync(TimeSpan.FromSeconds(5));
        rotation.Accept(rotation.Candidate!);
        Assert.That(rotation.Configure(ConsumerSettings()).SslCertificatePem, Is.Not.EqualTo(before));
    }

    [Test]
    public async Task MetadataFailureKeepsOldContextAndPreservesSaslOnNextValidGeneration()
    {
        var log = new FailureLog();
        var calls = 0;
        await using var rotation = await KafkaTlsRotation.CreateAsync(configuration, baseline, ["settings"], log, candidate =>
        {
            Assert.That(candidate.SaslUsername, Is.EqualTo("synthetic-user"));
            Assert.That(candidate.SaslPassword, Is.EqualTo("synthetic-password"));
            return ++calls == 2 ? Task.FromException(new TimeoutException("must not be logged")) : Task.CompletedTask;
        });
        var old = rotation.Configure(ConsumerSettings()).SslCertificatePem;
        Publish();
        await log.Failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(rotation.RotationToken.IsCancellationRequested, Is.False);
        Assert.That(rotation.Configure(ConsumerSettings()).SslCertificatePem, Is.EqualTo(old));
        Assert.That(log.Message, Does.Not.Contain("must not be logged"));
        Publish();
        await Task.Delay(Timeout.Infinite, rotation.RotationToken).ContinueWith(_ => { }).WaitAsync(TimeSpan.FromSeconds(5));
        rotation.Accept(rotation.Candidate!);
    }

    [Test]
    public async Task ValidatedMaterialRetriesMetadataWithBoundedBackoffWithoutFileChanges()
    {
        var retries = new RetryDelays();
        var log = new FailureLog();
        var calls = 0;
        await using var rotation = await KafkaTlsRotation.CreateAsync(configuration, baseline, ["settings"], log,
            _ => ++calls is > 1 and < 8 ? Task.FromException(new TimeoutException("not logged")) : Task.CompletedTask,
            delay: retries.Delay, watch: false);
        var old = rotation.Current;
        Publish();
        rotation.SignalChange();
        foreach (var seconds in new[] { 30, 60, 120, 240, 300, 300 })
        {
            var retry = await retries.Next();
            Assert.That(retry.Wait.TotalSeconds, Is.EqualTo(seconds));
            Assert.That(rotation.Current, Is.SameAs(old));
            retry.Release.SetResult();
        }
        await Task.Delay(Timeout.Infinite, rotation.RotationToken).ContinueWith(_ => { }).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(calls, Is.EqualTo(8));
        Assert.That(log.Errors, Is.EqualTo(1), "Repeated network failure of one generation/type must log once.");
        rotation.Accept(rotation.Candidate!);
        Assert.That(rotation.Current, Is.Not.SameAs(old));
    }

    [Test]
    public async Task NewInvalidGenerationCancelsValidatedRetryAndCannotBeOverriddenByStaleWork()
    {
        var retries = new RetryDelays();
        var log = new FailureLog();
        var calls = 0;
        await using var rotation = await KafkaTlsRotation.CreateAsync(configuration, baseline, ["settings"], log,
            _ => ++calls == 2 ? Task.FromException(new TimeoutException()) : Task.CompletedTask,
            delay: retries.Delay, watch: false);
        var old = rotation.Current;
        Publish();
        rotation.SignalChange();
        var stale = await retries.Next();
        Publish(expired: true);
        rotation.SignalChange();
        Assert.That(stale.Token.IsCancellationRequested, Is.True);
        stale.Release.SetResult();
        await log.SecondFailure.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(rotation.Current, Is.SameAs(old));
        Assert.That(rotation.Candidate, Is.Null);
        Assert.That(calls, Is.EqualTo(2), "Invalid material must not be retried or preflighted.");
        Assert.That(retries.HasPending, Is.False);
        Publish();
        rotation.SignalChange();
        await Task.Delay(Timeout.Infinite, rotation.RotationToken).ContinueWith(_ => { }).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(rotation.Candidate!.Certificate, Is.EqualTo(File.ReadAllText(Path.Combine(directory, "client.crt"))));
    }

    [Test]
    public async Task SupersededInFlightRetryCannotPublishItsOldSnapshot()
    {
        var retries = new RetryDelays();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        await using var rotation = await KafkaTlsRotation.CreateAsync(configuration, baseline, ["settings"], NullLogger.Instance,
            _ =>
            {
                var attempt = ++calls;
                if (attempt == 2) return Task.FromException(new TimeoutException());
                if (attempt == 3) { entered.SetResult(); return release.Task; }
                return Task.CompletedTask;
            }, delay: retries.Delay, watch: false);
        Publish();
        rotation.SignalChange();
        var retry = await retries.Next();
        retry.Release.SetResult();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Publish();
        rotation.SignalChange();
        var newest = File.ReadAllText(Path.Combine(directory, "client.crt"));
        release.SetResult();
        await Task.Delay(Timeout.Infinite, rotation.RotationToken).ContinueWith(_ => { }).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(rotation.Candidate!.Certificate, Is.EqualTo(newest));
        Assert.That(calls, Is.EqualTo(4));
    }

    [Test]
    public async Task DisposalCancelsValidatedRetryWithoutAnotherNetworkAttempt()
    {
        var retries = new RetryDelays();
        var calls = 0;
        var rotation = await KafkaTlsRotation.CreateAsync(configuration, baseline, ["settings"], NullLogger.Instance,
            _ => ++calls == 2 ? Task.FromException(new TimeoutException()) : Task.CompletedTask,
            delay: retries.Delay, watch: false);
        Publish();
        rotation.SignalChange();
        var retry = await retries.Next();
        await rotation.DisposeAsync();
        Assert.That(retry.Token.IsCancellationRequested, Is.True);
        retry.Release.SetResult();
        Assert.That(calls, Is.EqualTo(2));
    }

    private sealed class RetryDelays
    {
        private readonly Channel<(TimeSpan Wait, CancellationToken Token, TaskCompletionSource Release)> requests = Channel.CreateUnbounded<(TimeSpan, CancellationToken, TaskCompletionSource)>();
        internal bool HasPending => requests.Reader.TryPeek(out _);
        internal Task Delay(TimeSpan wait, CancellationToken token)
        {
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            requests.Writer.TryWrite((wait, token, release));
            return release.Task.WaitAsync(token);
        }
        internal async Task<(TimeSpan Wait, CancellationToken Token, TaskCompletionSource Release)> Next() =>
            await requests.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public void InitialBadGenerationFailsClosed()
    {
        Publish(expired: true);
        Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var ignored = await KafkaTlsRotation.CreateAsync(configuration, baseline, ["settings"],
                NullLogger.Instance, _ => throw new AssertionException("Bad material must not reach native preflight."));
        });
    }

    [Test]
    public async Task ARejectedLaterGenerationDoesNotReplaceTheAlreadyPreflightedCandidate()
    {
        var log = new FailureLog();
        string? accepted = null;
        await using var rotation = await KafkaTlsRotation.CreateAsync(configuration, baseline, ["settings"], log,
            candidate => { accepted = candidate.SslCertificatePem; return Task.CompletedTask; });
        Publish();
        await Task.Delay(Timeout.Infinite, rotation.RotationToken).ContinueWith(_ => { }).WaitAsync(TimeSpan.FromSeconds(5));
        var good = accepted;
        Publish(expired: true);
        await log.Failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        rotation.Accept(rotation.Candidate!);
        Assert.That(rotation.Configure(ConsumerSettings()).SslCertificatePem, Is.EqualTo(good));
    }

    [Test]
    public async Task ActionFailureClosesWithoutCommittingTheFetchedRecord()
    {
        await using var rotation = await KafkaTlsRotation.CreateAsync(configuration, baseline, ["settings"],
            NullLogger.Instance, _ => Task.CompletedTask);
        var consumer = new Mock<IConsumer<Ignore, string>>();
        consumer.Setup(c => c.Consume(It.IsAny<CancellationToken>())).Returns(new ConsumeResult<Ignore, string>
        {
            Topic = "settings", Partition = 0, Offset = 42, Message = new() { Value = "synthetic" }
        });
        var owner = new KafkaConsumer(NullLogger<KafkaConsumer>.Instance, configuration);
        Assert.ThrowsAsync<InvalidOperationException>(() => owner.ConsumeBatchCore<string>(rotation.Configure(ConsumerSettings()),
            ["settings"], _ => throw new InvalidOperationException("synthetic action failure"), CancellationToken.None,
            1, rotation: rotation, createConsumer: (config, ready) =>
            {
                ready.TrySetResult();
                Assert.That(config.EnableAutoCommit, Is.False);
                Assert.That(config.EnableAutoOffsetStore, Is.False);
                return consumer.Object;
            }));
        consumer.Verify(c => c.Close(), Times.Once);
        consumer.Verify(c => c.Commit(It.IsAny<IEnumerable<TopicPartitionOffset>>()), Times.Never);
    }

    [Test]
    public async Task CandidateThatExpiresWhileDrainingKeepsTheOldHandle()
    {
        var clock = new MutableClock();
        var log = new FailureLog();
        await using var rotation = await KafkaTlsRotation.CreateAsync(configuration, baseline, ["settings"], log,
            _ => Task.CompletedTask, clock);
        var old = rotation.Configure(ConsumerSettings()).SslCertificatePem;
        var processing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var consumer = new Mock<IConsumer<Ignore, string>>();
        var calls = 0;
        consumer.Setup(c => c.Consume(It.IsAny<CancellationToken>())).Returns((CancellationToken token) =>
        {
            if (++calls == 1) return new() { Topic = "settings", Partition = 0, Offset = 42, Message = new() { Value = "synthetic" } };
            idle.TrySetResult();
            token.WaitHandle.WaitOne();
            token.ThrowIfCancellationRequested();
            throw new AssertionException("Unexpected consume continuation.");
        });
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var owner = new KafkaConsumer(NullLogger<KafkaConsumer>.Instance, configuration);
        var task = Task.Run(() => owner.ConsumeBatchCore<string>(rotation.Configure(ConsumerSettings()), ["settings"],
            async _ => { processing.TrySetResult(); await release.Task; }, stop.Token, 1,
            rotation: rotation, createConsumer: (_, ready) => { ready.TrySetResult(); return consumer.Object; }));
        await processing.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Publish(validity: TimeSpan.FromHours(1));
        await Task.Delay(Timeout.Infinite, rotation.RotationToken).ContinueWith(_ => { }).WaitAsync(TimeSpan.FromSeconds(5));
        clock.Now = DateTimeOffset.UtcNow.AddMinutes(61);
        release.TrySetResult();
        await log.Failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await idle.Task.WaitAsync(TimeSpan.FromSeconds(5));
        consumer.Verify(c => c.Close(), Times.Never);
        Assert.That(rotation.Configure(ConsumerSettings()).SslCertificatePem, Is.EqualTo(old));
        stop.Cancel();
        await task.WaitAsync(TimeSpan.FromSeconds(5));
        consumer.Verify(c => c.Close(), Times.Once);
    }

    private sealed class MutableClock : TimeProvider
    {
        internal DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Test]
    public async Task CandidateBuildFailureRetainsTheOriginalHandleThenRetriesWithoutFileChange()
    {
        var retries = new RetryDelays();
        await using var rotation = await KafkaTlsRotation.CreateAsync(configuration, baseline, ["settings"],
            NullLogger.Instance, _ => Task.CompletedTask, delay: retries.Delay, watch: false);
        var log = new FailureLog();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var firstReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var old = IdleConsumer(firstReady);
        var builds = 0;
        var owner = new KafkaConsumer(log, configuration);
        var consuming = Task.Run(() => owner.ConsumeBatchCore<string>(rotation.Configure(ConsumerSettings()), ["settings"],
            _ => Task.CompletedTask, stop.Token, 1, rotation: rotation, createConsumer: (_, ready) =>
            {
                if (++builds == 1) { ready.TrySetResult(); return old.Object; }
                if (builds <= 3) throw new InvalidOperationException("synthetic candidate build failure");
                return IdleConsumer(ready).Object;
            }));
        await firstReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Publish();
        rotation.SignalChange();
        await log.Failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        old.Verify(c => c.Close(), Times.Never);
        Assert.That(log.Message, Does.Contain("current handle retained"));
        var retry = await retries.Next();
        retry.Release.SetResult();
        var secondRetry = await retries.Next();
        Assert.That(secondRetry.Wait, Is.EqualTo(TimeSpan.FromSeconds(60)));
        secondRetry.Release.SetResult();
        await log.Activated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(builds, Is.EqualTo(4));
        Assert.That(log.Errors, Is.EqualTo(1));
        stop.Cancel();
        await consuming.WaitAsync(TimeSpan.FromSeconds(5));
        old.Verify(c => c.Close(), Times.Once);
    }

    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    public async Task FailureAfterCloseRebuildsPreviousSnapshotWithoutClaimingHandleRetention(bool expireDuringClose, bool expireDuringRecovery)
    {
        var clock = new MutableClock();
        await using var rotation = await KafkaTlsRotation.CreateAsync(configuration, baseline, ["settings"],
            NullLogger.Instance, _ => Task.CompletedTask, clock);
        var original = rotation.Configure(ConsumerSettings()).SslCertificatePem;
        var log = new FailureLog();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var firstReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var old = IdleConsumer(firstReady);
        if (expireDuringClose) old.Setup(c => c.Close()).Callback(() => clock.Now = DateTimeOffset.UtcNow.AddMinutes(61));
        var builds = 0;
        ConsumerConfig? restored = null;
        var candidateDisposed = false;
        Mock<IConsumer<Ignore, string>>? recovered = null;
        var owner = new KafkaConsumer(log, configuration);
        var consuming = Task.Run(() => owner.ConsumeBatchCore<string>(rotation.Configure(ConsumerSettings()), ["settings"],
            _ => Task.CompletedTask, stop.Token, 1, rotation: rotation, createConsumer: (config, ready) =>
            {
                if (++builds == 1) { ready.TrySetResult(); return old.Object; }
                if (builds == 2)
                {
                    var invalid = IdleConsumer(ready);
                    invalid.Setup(c => c.Dispose()).Callback(() => candidateDisposed = true);
                    invalid.Setup(c => c.Subscribe(It.IsAny<IEnumerable<string>>())).Callback(() =>
                        ready.TrySetException(new KafkaException(new Error(ErrorCode.GroupAuthorizationFailed))));
                    return invalid.Object;
                }
                Assert.That(candidateDisposed, Is.True, "The failed candidate must leave/dispose before recovery joins its group.");
                restored = config;
                recovered = IdleConsumer(ready);
                if (expireDuringRecovery)
                    recovered.Setup(c => c.Subscribe(It.IsAny<IEnumerable<string>>())).Callback(() =>
                    {
                        clock.Now = DateTimeOffset.UtcNow.AddHours(3);
                        ready.TrySetResult();
                    });
                return recovered.Object;
            }));
        await firstReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Publish(validity: TimeSpan.FromHours(1));
        if (expireDuringRecovery)
        {
            var failure = Assert.ThrowsAsync<InvalidOperationException>(async () => await consuming.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.That(failure!.Message, Does.Contain("expired during recovery"));
            Assert.That(log.Failed.Task.IsCompleted, Is.False, "Expired recovery must not be reported as rebuilt.");
            recovered!.Verify(c => c.Close(), Times.Once);
            return;
        }
        await log.Failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Multiple(() =>
        {
            Assert.That(restored?.SslCertificatePem, Is.EqualTo(original));
            Assert.That(log.Message, Does.Contain("after Close").And.Contain("not retained"));
            Assert.That(rotation.Configure(ConsumerSettings()).SslCertificatePem, Is.EqualTo(original));
        });
        old.Verify(c => c.Close(), Times.Once);
        stop.Cancel();
        await consuming.WaitAsync(TimeSpan.FromSeconds(5));
        recovered!.Verify(c => c.Close(), Times.Once);
    }

    [Test]
    public async Task CommitFailureMayReplayButSuccessfulRenewalDoesNotDropTheRecord()
    {
        await using var rotation = await KafkaTlsRotation.CreateAsync(configuration, baseline, ["settings"],
            NullLogger.Instance, _ => Task.CompletedTask);
        var processing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var old = new Mock<IConsumer<Ignore, string>>();
        var replacement = new Mock<IConsumer<Ignore, string>>();
        ConsumeResult<Ignore, string> Record() => new() { Topic = "settings", Partition = 0, Offset = 42, Message = new() { Value = "synthetic" } };
        old.Setup(c => c.Consume(It.IsAny<CancellationToken>())).Returns(Record);
        old.Setup(c => c.Commit(It.IsAny<IEnumerable<TopicPartitionOffset>>())).Throws(new KafkaException(new Error(ErrorCode.Local_TimedOut)));
        replacement.Setup(c => c.Consume(It.IsAny<CancellationToken>())).Returns(Record);
        var committed = new List<long>();
        replacement.Setup(c => c.Commit(It.IsAny<IEnumerable<TopicPartitionOffset>>())).Callback<IEnumerable<TopicPartitionOffset>>(
            offsets => committed.AddRange(offsets.Select(offset => offset.Offset.Value)));
        var actions = 0;
        var builds = 0;
        var owner = new KafkaConsumer(NullLogger<KafkaConsumer>.Instance, configuration);
        var consuming = owner.ConsumeBatchCore<string>(rotation.Configure(ConsumerSettings()), ["settings"], async _ =>
        {
            if (++actions == 1) { processing.TrySetResult(); await release.Task; }
            else stop.Cancel();
        }, stop.Token, 1, rotation: rotation, createConsumer: (_, ready) =>
        {
            if (++builds == 1) { ready.TrySetResult(); return old.Object; }
            replacement.Setup(c => c.Subscribe(It.IsAny<IEnumerable<string>>())).Callback(() => ready.TrySetResult());
            return replacement.Object;
        });
        await processing.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Publish();
        await Task.Delay(Timeout.Infinite, rotation.RotationToken).ContinueWith(_ => { }).WaitAsync(TimeSpan.FromSeconds(5));
        release.TrySetResult();
        await consuming.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(actions, Is.EqualTo(2));
        Assert.That(committed, Is.EqualTo(new long[] { 43 }));
        replacement.Verify(c => c.Commit(It.IsAny<IEnumerable<TopicPartitionOffset>>()), Times.Once);
    }

    [Test]
    public async Task LegacyOffsetAndCloseBehaviorRemainUnchangedWithoutOptIn()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var consumer = new Mock<IConsumer<Ignore, string>>();
        consumer.Setup(c => c.Consume(It.IsAny<CancellationToken>())).Returns(new ConsumeResult<Ignore, string>
        {
            Topic = "settings", Partition = 0, Offset = 42, Message = new() { Value = "synthetic" }
        });
        var committed = new List<long>();
        consumer.Setup(c => c.Commit(It.IsAny<IEnumerable<TopicPartitionOffset>>())).Callback<IEnumerable<TopicPartitionOffset>>(
            offsets => committed.AddRange(offsets.Select(offset => offset.Offset.Value)));
        var owner = new KafkaConsumer(NullLogger<KafkaConsumer>.Instance, configuration);
        await owner.ConsumeBatchCore<string>(new ConsumerConfig { EnableAutoCommit = false, EnableAutoOffsetStore = true },
            ["settings"], _ => { stop.Cancel(); return Task.CompletedTask; }, stop.Token, 1,
            createConsumer: (_, ready) => { ready.TrySetResult(); return consumer.Object; });
        Assert.That(committed, Is.EqualTo(new long[] { 42 }));
        consumer.Verify(c => c.Commit(It.IsAny<IEnumerable<TopicPartitionOffset>>()), Times.Once);
        consumer.Verify(c => c.Close(), Times.Never);
        consumer.Verify(c => c.Dispose(), Times.Once);
    }

    [Test]
    public async Task ConsecutiveGenerationsDrainARecordFetchedDuringAssignmentBeforeAnotherHandoff()
    {
        var thirdPreflight = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var preflights = 0;
        await using var rotation = await KafkaTlsRotation.CreateAsync(configuration, baseline, ["settings"], NullLogger.Instance,
            _ => { if (++preflights == 3) thirdPreflight.TrySetResult(); return Task.CompletedTask; });
        var initialAssigned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var initial = IdleConsumer(initialAssigned);
        var events = new ConcurrentQueue<string>();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var log = new FailureLog();
        var builds = 0;
        var owner = new KafkaConsumer(log, configuration);
        var consuming = Task.Run(() => owner.ConsumeBatchCore<string>(rotation.Configure(ConsumerSettings()), ["settings"],
            _ => { events.Enqueue("action"); return Task.CompletedTask; }, stop.Token, 1, rotation: rotation,
            createConsumer: (_, ready) =>
            {
                if (++builds == 1) { ready.TrySetResult(); return initial.Object; }
                if (builds > 2) return IdleConsumer(ready).Object;
                var first = new Mock<IConsumer<Ignore, string>>();
                first.Setup(c => c.Consume(It.IsAny<CancellationToken>())).Returns(() =>
                {
                    ready.TrySetResult();
                    Publish();
                    thirdPreflight.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                    return new() { Topic = "settings", Partition = 0, Offset = 42, Message = new() { Value = "synthetic" } };
                });
                first.Setup(c => c.Commit(It.IsAny<IEnumerable<TopicPartitionOffset>>())).Callback(() => events.Enqueue("commit"));
                first.Setup(c => c.Close()).Callback(() => events.Enqueue("close"));
                return first.Object;
            }));
        await initialAssigned.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Publish();
        await log.TwiceActivated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(events, Is.EqualTo(new[] { "action", "commit", "close" }));
        stop.Cancel();
        await consuming.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test, Explicit("Starts an isolated cached Redpanda container; never uses a production broker.")]
    public async Task NativeFullChainRotationRebuildsTheHandleAndDeliversWithStrictServerValidation()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var name = "coflnet-kafka-pki-" + Guid.NewGuid().ToString("N");
        var topic = "settings-rotation-" + Guid.NewGuid().ToString("N");
        using var serverKey = RSA.Create(2048);
        var request = new CertificateRequest("CN=fixture-broker", serverKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        using var serverIssuerKey = RSA.Create(2048);
        var serverIssuerRequest = new CertificateRequest("CN=broker-intermediate", serverIssuerKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        serverIssuerRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        serverIssuerRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        using var serverIssuerSigned = serverIssuerRequest.Create(root, DateTimeOffset.UtcNow.AddSeconds(-45), DateTimeOffset.UtcNow.AddHours(3), RandomNumberGenerator.GetBytes(16));
        using var serverIssuer = serverIssuerSigned.CopyWithPrivateKey(serverIssuerKey);
        using var server = request.Create(serverIssuer, DateTimeOffset.UtcNow.AddSeconds(-30), DateTimeOffset.UtcNow.AddHours(1), RandomNumberGenerator.GetBytes(16));
        File.WriteAllText(Path.Combine(directory, "server.crt"), server.ExportCertificatePem() + "\n" + serverIssuer.ExportCertificatePem());
        File.WriteAllText(Path.Combine(directory, "server.key"), serverKey.ExportPkcs8PrivateKeyPem());
        File.WriteAllText(Path.Combine(directory, "broker.yaml"), $$"""
            redpanda:
              data_directory: /var/lib/redpanda/data
              node_id: 0
              seed_servers: []
              rpc_server: {address: 0.0.0.0, port: 33145}
              advertised_rpc_api: {address: 127.0.0.1, port: 33145}
              kafka_api: [{name: tls, address: 0.0.0.0, port: 9092, authentication_method: sasl}]
              enable_sasl: true
              superusers: [synthetic-user]
              advertised_kafka_api: [{name: tls, address: 127.0.0.1, port: {{port}}}]
              kafka_api_tls:
                - name: tls
                  enabled: true
                  require_client_auth: true
                  cert_file: /test/server.crt
                  key_file: /test/server.key
                  truststore_file: /test/ca.crt
              admin: [{address: 0.0.0.0, port: 9644}]
              developer_mode: true
            """);
        var nativeErrors = new ConcurrentQueue<string>();
        Docker("network", "create", name);
        try
        {
            Docker("run", "-d", "--pull=never", "--name", name, "--network", name, "--user", "0:0",
                "-p", $"127.0.0.1:{port}:9092", "-v", directory + ":/test:ro", "--entrypoint", "sh",
                "redpandadata/redpanda@sha256:df0d30a08e464fd4f2dfde9735f9b5e73f7cd1d81de87a86a97df859c01bf091", "-c",
                "cp /test/broker.yaml /tmp/broker.yaml && exec rpk redpanda start --config /tmp/broker.yaml --check=false --overprovisioned --smp=1 --memory=512M --reserve-memory=0M");
            await WaitForBroker(name);
            TestContext.Progress.WriteLine("Isolated broker startup event received.");
            Docker("exec", name, "rpk", "acl", "user", "create", "synthetic-user", "--password", "synthetic-password",
                "-X", "admin.hosts=127.0.0.1:9644");
            TestContext.Progress.WriteLine("Synthetic SCRAM identity prepared.");
            var actual = new ClientConfig
            {
                BootstrapServers = "127.0.0.1:" + port, SecurityProtocol = SecurityProtocol.SaslSsl,
                BrokerAddressFamily = BrokerAddressFamily.V4,
                SaslMechanism = SaslMechanism.ScramSha256, SaslUsername = "synthetic-user", SaslPassword = "synthetic-password",
                SslCaLocation = Path.Combine(directory, "ca.crt"),
                SslCertificateLocation = Path.Combine(directory, "client.crt"),
                SslKeyLocation = Path.Combine(directory, "client.key"),
                EnableSslCertificateVerification = true,
                SslEndpointIdentificationAlgorithm = SslEndpointIdentificationAlgorithm.Https
            };
            using (var admin = new AdminClientBuilder(new AdminClientConfig(actual.ToDictionary(item => item.Key, item => item.Value)))
                .SetLogHandler((_, _) => { }).SetErrorHandler((_, error) => nativeErrors.Enqueue(error.Code + ": " + error.Reason.Replace("synthetic-password", "[redacted]"))).Build())
            {
                admin.GetMetadata(TimeSpan.FromSeconds(40));
                await admin.CreateTopicsAsync([new TopicSpecification { Name = topic, NumPartitions = 1, ReplicationFactor = 1 }]);
            }
            TestContext.Progress.WriteLine("SASL/mTLS metadata and synthetic topic ready.");
            using (var baselineProducer = new ProducerBuilder<Null, string>(new ProducerConfig(actual.ToDictionary(item => item.Key, item => item.Value)) { MessageTimeoutMs = 10000 })
                .SetLogHandler((_, _) => { }).Build())
            using (var baselineConsumer = new ConsumerBuilder<Ignore, string>(new ConsumerConfig(actual.ToDictionary(item => item.Key, item => item.Value))
            {
                GroupId = "file-baseline-" + Guid.NewGuid(), AutoOffsetReset = AutoOffsetReset.Earliest,
                EnableAutoCommit = false, EnableAutoOffsetStore = false
            }).SetLogHandler((_, _) => { }).Build())
            using (var baselineDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
            {
                baselineConsumer.Subscribe(topic);
                await baselineProducer.ProduceAsync(topic, new Message<Null, string> { Value = "\"file-baseline\"" }, baselineDeadline.Token);
                Assert.That(baselineConsumer.Consume(baselineDeadline.Token).Message.Value, Is.EqualTo("\"file-baseline\""));
                baselineConsumer.Close();
            }
            TestContext.Progress.WriteLine("Legacy FILE-location SASL/mTLS metadata and consumer delivery verified.");
            var nativeConfiguration = new ConfigurationBuilder().AddConfiguration(configuration).AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["KAFKA:BROKERS"] = "127.0.0.1:" + port,
                ["KAFKA:USERNAME"] = "synthetic-user", ["KAFKA:PASSWORD"] = "synthetic-password"
            }).Build();
            var before = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var after = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(150));
            var log = new FailureLog();
            var peerLog = new FailureLog();
            var owner = new KafkaConsumer(log, nativeConfiguration);
            var peer = new KafkaConsumer(peerLog, nativeConfiguration);
            var group = "rotation-test-" + Guid.NewGuid();
            var beforeCount = 0;
            var afterCount = 0;
            var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task Process(string value)
            {
                if (value == "before") { Interlocked.Increment(ref beforeCount); before.TrySetResult(); }
                else if (value == "after") { Interlocked.Increment(ref afterCount); after.TrySetResult(); }
                else if (value == "barrier") { barrier.TrySetResult(); stop.Cancel(); }
                return Task.CompletedTask;
            }
            var consuming = owner.Consume<string>(topic, Process, stop.Token, group);
            using var producer = new ProducerBuilder<Null, string>(new ProducerConfig(actual.ToDictionary(item => item.Key, item => item.Value)) { MessageTimeoutMs = 10000 }).SetLogHandler((_, _) => { }).Build();
            await producer.ProduceAsync(topic, new Message<Null, string> { Value = "\"before\"" }, stop.Token);
            await before.Task.WaitAsync(TimeSpan.FromSeconds(30));
            TestContext.Progress.WriteLine("Initial native consumer delivery verified.");
            var peerConsuming = peer.Consume<string>(topic, Process, stop.Token, group);
            var oldPem = File.ReadAllText(Path.Combine(directory, "client.crt"));
            Publish();
            await Task.WhenAll(log.Activated.Task, peerLog.Activated.Task).WaitAsync(TimeSpan.FromSeconds(70));
            using (var groupAdmin = new AdminClientBuilder(new AdminClientConfig(actual.ToDictionary(item => item.Key, item => item.Value)))
                .SetLogHandler((_, _) => { }).Build())
            {
                var described = await groupAdmin.DescribeConsumerGroupsAsync([group], new DescribeConsumerGroupsOptions { RequestTimeout = TimeSpan.FromSeconds(10) });
                var members = described.ConsumerGroupDescriptions.Single().Members;
                Assert.That(members.Select(m => m.Assignment.TopicPartitions.Count).Order(), Is.EqualTo(new[] { 0, 1 }));
            }
            Assert.That(log.Failed.Task.IsCompleted || peerLog.Failed.Task.IsCompleted, Is.False, "Neither member may timeout/rebuild during renewal.");
            TestContext.Progress.WriteLine("Both public opt-in members renewed without new messages; broker confirms assignments [0,1].");
            Assert.That(File.ReadAllText(Path.Combine(directory, "client.crt")), Is.Not.EqualTo(oldPem));
            await producer.ProduceAsync(topic, new Message<Null, string> { Value = "\"after\"" }, stop.Token);
            await after.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await producer.ProduceAsync(topic, new Message<Null, string> { Value = "\"barrier\"" }, stop.Token);
            await barrier.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await Task.WhenAll(consuming, peerConsuming).WaitAsync(TimeSpan.FromSeconds(10));
            TestContext.Progress.WriteLine("Post-renewal native record delivered once across two members, followed by committed-order barrier.");
            Assert.That(beforeCount, Is.EqualTo(1), "Successful next-offset commit must not replay the last old-generation record.");
            Assert.That(afterCount, Is.EqualTo(1));

            void Reject(ClientConfig invalid)
            {
                using var admin = new AdminClientBuilder(new AdminClientConfig(invalid)).SetLogHandler((_, _) => { }).Build();
                Assert.Throws<KafkaException>(() => admin.GetMetadata(TimeSpan.FromSeconds(2)));
            }
            Reject(new ClientConfig(actual.ToDictionary(item => item.Key, item => item.Value)) { SslCertificateLocation = null, SslKeyLocation = null, SslCertificatePem = null, SslKeyPem = null });
            using var leafOnly = X509Certificate2.CreateFromPem(File.ReadAllText(actual.SslCertificateLocation!));
            Reject(new ClientConfig(actual.ToDictionary(item => item.Key, item => item.Value)) { SslCertificateLocation = null, SslCertificatePem = leafOnly.ExportCertificatePem() });
            Reject(new ClientConfig(actual.ToDictionary(item => item.Key, item => item.Value)) { BootstrapServers = "localhost:" + port });
            using var wrongKey = RSA.Create(2048);
            var wrongRequest = new CertificateRequest("CN=wrong-root", wrongKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            wrongRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            using var wrongRoot = wrongRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
            Reject(new ClientConfig(actual.ToDictionary(item => item.Key, item => item.Value)) { SslCaLocation = null, SslCaPem = wrongRoot.ExportCertificatePem() });
            var missing = "missing-" + Guid.NewGuid().ToString("N");
            Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await using var absent = await KafkaTlsRotation.CreateAsync(configuration, actual, [missing], NullLogger.Instance);
            });
            using var listing = new AdminClientBuilder(new AdminClientConfig(actual.ToDictionary(item => item.Key, item => item.Value)))
                .SetLogHandler((_, _) => { }).Build();
            Assert.That(listing.GetMetadata(TimeSpan.FromSeconds(5)).Topics.Select(t => t.Topic), Does.Not.Contain(missing));
            TestContext.Progress.WriteLine("Missing-topic preflight rejected without topic creation.");
        }
        catch
        {
            foreach (var error in nativeErrors.TakeLast(3)) TestContext.Progress.WriteLine(error[..Math.Min(error.Length, 240)]);
            var diagnostics = Docker("logs", name).Split('\n')
                .Where(line => line.Contains("ERROR", StringComparison.OrdinalIgnoreCase) || line.Contains("exception", StringComparison.OrdinalIgnoreCase) || line.Contains("Kafka API", StringComparison.OrdinalIgnoreCase))
                .TakeLast(6);
            foreach (var line in diagnostics) TestContext.Progress.WriteLine(line[..Math.Min(line.Length, 240)]);
            throw;
        }
        finally
        {
            Docker("rm", "-f", "-v", name);
            Docker("network", "rm", name);
        }
    }

    private static async Task WaitForBroker(string name)
    {
        var start = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "logs", "--follow", name }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Read(StreamReader stream)
        {
            while (await stream.ReadLineAsync(timeout.Token) is { } line)
                if (line.Contains("Successfully started Redpanda", StringComparison.Ordinal)) ready.TrySetResult();
        }
        var readers = Task.WhenAll(Read(process.StandardOutput), Read(process.StandardError));
        try
        {
            var completed = await Task.WhenAny(ready.Task, readers).WaitAsync(timeout.Token);
            if (completed != ready.Task && !ready.Task.IsCompleted)
                throw new InvalidOperationException("Isolated broker exited before startup completion.");
            await ready.Task;
        }
        finally
        {
            if (!process.HasExited) process.Kill();
            timeout.Cancel();
            try { await readers; } catch (OperationCanceledException) { }
        }
    }

    private static string Docker(params string[] arguments)
    {
        var start = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60000))
        {
            process.Kill();
            throw new TimeoutException("Isolated Kafka test container operation timed out.");
        }
        if (process.ExitCode != 0) throw new InvalidOperationException("Isolated Kafka test container operation failed.");
        return (output.GetAwaiter().GetResult() + (arguments[0] == "logs" ? error.GetAwaiter().GetResult() : "")).Trim();
    }

    private sealed class FailureLog : ILogger<KafkaConsumer>
    {
        internal readonly TaskCompletionSource Failed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource SecondFailure = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Errors;
        internal readonly TaskCompletionSource Activated = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource TwiceActivated = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int activations;
        internal string Message = "";
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Message = formatter(state, exception);
            if (logLevel == LogLevel.Error)
            {
                Failed.TrySetResult();
                if (Interlocked.Increment(ref Errors) == 2) SecondFailure.TrySetResult();
            }
            if (logLevel == LogLevel.Information)
            {
                Activated.TrySetResult();
                if (++activations == 2) TwiceActivated.TrySetResult();
            }
        }
    }
}
