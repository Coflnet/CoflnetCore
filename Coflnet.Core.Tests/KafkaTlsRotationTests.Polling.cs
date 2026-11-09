using Coflnet.Kafka;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace Coflnet.Core.Tests;

// Polling fallback for when file watchers are dead or never started.
public partial class KafkaTlsRotationTests
{
    private static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(60);

    private Task<KafkaTlsRotation> PollingRotation(ManualClock clock,
        Func<Confluent.Kafka.ClientConfig, Task>? preflight = null, Func<TimeSpan, CancellationToken, Task>? delay = null,
        ILogger? log = null) =>
        KafkaTlsRotation.CreateAsync(configuration, baseline, ["settings"], log ?? NullLogger.Instance,
            preflight ?? (_ => Task.CompletedTask), clock, delay, watch: false, initialPreflight: false, pollInterval: PollEvery);

    private static async Task Tick(ManualClock clock)
    {
        clock.Advance(PollEvery);
        await Until(() => clock.PendingTimers == 1); // the loop ran its check and re-armed
    }

    [Test]
    public async Task PollingDetectsRenewalWhenNoWatcherFires()
    {
        var clock = new ManualClock();
        await using var rotation = await PollingRotation(clock);
        await Until(() => clock.PendingTimers == 1);
        Publish();
        await Tick(clock);
        await Until(() => rotation.Candidate is not null);
        Assert.That(rotation.RotationToken.IsCancellationRequested, Is.True);
    }

    [Test]
    public async Task UnchangedFilesNeverSignalSoPendingRetryAndBackoffSurvive()
    {
        var clock = new ManualClock();
        var waits = new List<(TimeSpan Wait, CancellationToken Token)>();
        Task Delay(TimeSpan wait, CancellationToken token)
        {
            lock (waits) waits.Add((wait, token));
            return Task.Delay(Timeout.Infinite, token);
        }
        await using var rotation = await PollingRotation(clock, preflight: _ => throw new InvalidOperationException("rejected"), delay: Delay);
        await Until(() => clock.PendingTimers == 1);
        Publish();
        await Tick(clock);
        await Until(() => { lock (waits) return waits.Count == 1; });
        for (var i = 0; i < 4; i++) await Tick(clock);
        lock (waits)
        {
            Assert.That(waits, Has.Count.EqualTo(1), "No signal means no re-read and no second retry.");
            Assert.That(waits[0].Wait, Is.EqualTo(TimeSpan.FromSeconds(30)));
            Assert.That(waits[0].Token.IsCancellationRequested, Is.False, "The pending retry must not be cancelled.");
        }
    }

    [Test]
    public async Task DisposalStopsThePollLoopCleanly()
    {
        var clock = new ManualClock();
        var rotation = await PollingRotation(clock);
        await Until(() => clock.PendingTimers == 1);
        await rotation.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(clock.PendingTimers, Is.Zero);
    }

    [Test]
    public async Task ZeroIntervalDisablesPolling()
    {
        var clock = new ManualClock();
        await using var rotation = await KafkaTlsRotation.CreateAsync(configuration, baseline, ["settings"], NullLogger.Instance,
            _ => Task.CompletedTask, clock, watch: false, initialPreflight: false, pollInterval: TimeSpan.Zero);
        Assert.That(clock.PendingTimers, Is.Zero);
    }

    [Test]
    public async Task DeadWatcherIsLoggedOnceWithTypeNameOnly()
    {
        if (OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess) Assert.Ignore("Directory permissions are not enforced here.");
        var hidden = Path.Combine(directory, "hidden");
        Directory.CreateDirectory(hidden);
        File.Copy(Path.Combine(directory, "ca.crt"), Path.Combine(hidden, "ca.crt"));
        File.SetUnixFileMode(hidden, UnixFileMode.UserExecute); // readable files, but inotify needs read access
        try
        {
            var settings = new ConfigurationBuilder().AddConfiguration(configuration)
                .AddInMemoryCollection(new Dictionary<string, string?> { ["KAFKA:TLS:CA_LOCATION"] = Path.Combine(hidden, "ca.crt") }).Build();
            var log = new CaptureLog();
            await using var rotation = await KafkaTlsRotation.CreateAsync(settings, baseline, ["settings"], log,
                _ => Task.CompletedTask, new ManualClock(), initialPreflight: false);
            await Until(() => log.Count("file watcher failed") > 0);
            Assert.That(log.Messages, Has.Some.EqualTo("Kafka TLS file watcher failed (UnauthorizedAccessException); relying on 60s polling."));
            Assert.That(log.Count("file watcher failed"), Is.EqualTo(1));
        }
        finally
        {
            File.SetUnixFileMode(hidden, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Test]
    public async Task WatcherThatCannotBeCreatedDoesNotFailCreation()
    {
        var log = new CaptureLog();
        await using var rotation = await KafkaTlsRotation.CreateAsync(configuration, baseline, ["settings"], log,
            _ => Task.CompletedTask, new ManualClock(), initialPreflight: false,
            watcherFactory: _ => throw new IOException("synthetic inotify exhaustion"));
        Assert.That(rotation.Current, Is.Not.Null);
        Assert.That(log.Messages, Has.Some.EqualTo("Kafka TLS file watcher could not start (IOException); relying on 60s polling."));
        Assert.That(log.Messages, Has.None.Contain("synthetic"));
    }

    [Test]
    public async Task PollSurvivesNonIoFailuresAndDisposeDoesNotThrow()
    {
        var clock = new ManualClock();
        var log = new CaptureLog();
        var rotation = await PollingRotation(clock, log: log);
        await Until(() => clock.PendingTimers == 1);
        var root = (IConfigurationRoot)configuration;
        root["KAFKA:TLS:CA_LOCATION"] = ""; // File.ReadAllText throws ArgumentException
        await Tick(clock);
        await Tick(clock);
        Assert.That(log.Count("could not read the files (ArgumentException)"), Is.EqualTo(1), "Logged once, loop still alive.");
        root["KAFKA:TLS:CA_LOCATION"] = Path.Combine(directory, "ca.crt");
        Publish();
        await Tick(clock);
        await Until(() => rotation.Candidate is not null);
        Assert.DoesNotThrowAsync(() => rotation.DisposeAsync().AsTask());
    }

    [Test]
    public async Task PollAfterAWatcherDrivenRenewalDoesNotBumpTheVersionAgain()
    {
        var clock = new ManualClock();
        await using var rotation = await PollingRotation(clock);
        await Until(() => clock.PendingTimers == 1);
        Publish();
        rotation.SignalChange(); // what a working watcher does
        await Until(() => rotation.Candidate is not null);
        var version = rotation.Version;
        await Tick(clock);
        Assert.That(rotation.Version, Is.EqualTo(version));
        Assert.That(rotation.Candidate, Is.Not.Null);
    }

    [Test]
    public async Task InvalidSnapshotReadIsSignalledAgainOnTheNextTick()
    {
        var clock = new ManualClock();
        var log = new CaptureLog();
        await using var rotation = await PollingRotation(clock, log: log);
        await Until(() => clock.PendingTimers == 1);
        Publish(expired: true); // ReadSnapshot rejects it and no retry exists for an unread candidate
        await Tick(clock);
        await Until(() => log.Count("candidate rejected") == 1);
        var version = rotation.Version;
        await Tick(clock);
        await Until(() => log.Count("candidate rejected") == 2);
        Assert.That(rotation.Version, Is.GreaterThan(version));
    }

    private sealed class CaptureLog : ILogger
    {
        private readonly List<string> messages = new();
        internal string[] Messages { get { lock (messages) return messages.ToArray(); } }
        internal int Count(string part) => Messages.Count(m => m.Contains(part));
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (messages) messages.Add(formatter(state, exception));
        }
    }

    // Wall clock for GetUtcNow, virtual time for timers.
    private sealed class ManualClock : TimeProvider
    {
        private readonly List<ManualTimer> timers = new();
        private TimeSpan elapsed;

        internal int PendingTimers { get { lock (timers) return timers.Count(t => t.IsArmed); } }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            timer.Change(dueTime, period);
            lock (timers) timers.Add(timer);
            return timer;
        }

        internal void Advance(TimeSpan by)
        {
            List<ManualTimer> due;
            lock (timers)
            {
                elapsed += by;
                due = timers.Where(t => t.IsArmed && t.Due <= elapsed).ToList();
            }
            foreach (var timer in due) timer.Fire();
        }

        private sealed class ManualTimer(ManualClock owner, TimerCallback callback, object? state) : ITimer
        {
            internal TimeSpan Due;
            internal bool IsArmed;

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (owner.timers)
                {
                    IsArmed = dueTime != Timeout.InfiniteTimeSpan;
                    Due = owner.elapsed + dueTime;
                }
                return true;
            }

            internal void Fire()
            {
                lock (owner.timers) IsArmed = false;
                callback(state);
            }

            public void Dispose() { lock (owner.timers) { IsArmed = false; owner.timers.Remove(this); } }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
