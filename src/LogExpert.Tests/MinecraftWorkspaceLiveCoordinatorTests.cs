using System.Text;

using LogExpert.Core.Classes.Log;
using LogExpert.Core.Classes.MinecraftLogs;
using LogExpert.Core.Entities;
using LogExpert.Core.Enums;
using LogExpert.Core.Interfaces;

using NUnit.Framework;

namespace LogExpert.Tests;

[TestFixture]
internal sealed class MinecraftWorkspaceLiveCoordinatorTests
{
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private string _testDirectory = null!;

    [SetUp]
    public void SetUp ()
    {
        _testDirectory = Path.Join(Path.GetTempPath(), "LogExpertTests", Guid.NewGuid().ToString());
        _ = Directory.CreateDirectory(_testDirectory);
        _ = PluginRegistry.PluginRegistry.Create(_testDirectory, 500);
    }

    [TearDown]
    public void TearDown ()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }

    [Test]
    public void Empty_workspace_has_no_runtime_sources_or_sessions ()
    {
        var factory = new FakeSessionFactory();
        using var coordinator = CreateCoordinator(factory);

        coordinator.Reconcile();

        Assert.Multiple(() =>
        {
            Assert.That(coordinator.GetRuntimeSources(), Is.Empty);
            Assert.That(factory.CreatedFileIds, Is.Empty);
            Assert.That(coordinator.PendingCount, Is.Zero);
        });
    }

    [Test]
    public void Initial_sources_start_once_in_discovery_order ()
    {
        CreateFile("cactusmonitor/sessions/cfm-live.jsonl");
        CreateFile("logs/reccactus/reccactus-live.txt");
        CreateFile("logs/yeezus.log.1");
        CreateFile("logs/yeezus.log");
        CreateFile("logs/latest.log");
        var discovery = CreateDiscovery();
        string[] expectedFileIds = new MinecraftSourceDiscovery(new MinecraftWorkspace(_testDirectory))
            .Rescan()
            .Files
            .Select(source => source.FileId)
            .ToArray();
        var factory = new FakeSessionFactory();
        using var coordinator = new MinecraftWorkspaceLiveCoordinator(discovery, factory);

        coordinator.Reconcile();

        Assert.Multiple(() =>
        {
            Assert.That(factory.CreatedFileIds, Is.EqualTo(expectedFileIds));
            Assert.That(factory.Sessions.Values.Select(session => session.StartCount), Is.All.EqualTo(1));
            Assert.That(coordinator.GetRuntimeSources().Select(state => state.Source.FileId), Is.EqualTo(expectedFileIds));
        });
    }

    [Test]
    public void Repeated_unchanged_reconcile_does_not_restart_or_replay ()
    {
        string path = CreateFile("logs/latest.log");
        var factory = new FakeSessionFactory();
        using var coordinator = CreateCoordinator(factory);

        coordinator.Reconcile();
        FakeSession session = factory.GetSession(path);
        session.Emit(CreateEvent(GetSource(path), 1, "initial"));
        int pendingBefore = coordinator.PendingCount;

        coordinator.Reconcile();
        coordinator.Reconcile();

        Assert.Multiple(() =>
        {
            Assert.That(factory.CreatedFileIds, Has.Count.EqualTo(1));
            Assert.That(session.StartCount, Is.EqualTo(1));
            Assert.That(session.DisposeCount, Is.Zero);
            Assert.That(coordinator.PendingCount, Is.EqualTo(pendingBefore));
        });
    }

    [Test]
    public void Newly_appeared_independent_source_starts_once ()
    {
        CreateFile("logs/latest.log");
        var factory = new FakeSessionFactory();
        using var coordinator = CreateCoordinator(factory);
        coordinator.Reconcile();

        string appearedPath = CreateFile("logs/reccactus/reccactus-new.txt");
        coordinator.Reconcile();
        coordinator.Reconcile();

        FakeSession appeared = factory.GetSession(appearedPath);
        Assert.Multiple(() =>
        {
            Assert.That(factory.CreatedFileIds.Count, Is.EqualTo(2));
            Assert.That(appeared.StartCount, Is.EqualTo(1));
            Assert.That(appeared.DisposeCount, Is.Zero);
        });
    }

    [Test]
    public void Disappearance_disposes_once_after_capturing_final_events ()
    {
        string path = CreateFile("logs/latest.log");
        DiscoveredSourceFile source = GetSource(path);
        var factory = new FakeSessionFactory();
        using var coordinator = CreateCoordinator(factory);
        coordinator.Reconcile();
        FakeSession session = factory.GetSession(path);
        session.FinalEvents = [CreateEvent(source, 7, "final")];
        File.Delete(path);

        coordinator.Reconcile();
        coordinator.Reconcile();
        IReadOnlyList<MinecraftWorkspaceIngressEvent> events = coordinator.DrainPendingEvents();

        Assert.Multiple(() =>
        {
            Assert.That(session.DisposeCount, Is.EqualTo(1));
            Assert.That(events, Has.Count.EqualTo(1));
            Assert.That(events[0].Event.Message, Is.EqualTo("final"));
            Assert.That(events[0].IngressSequence, Is.EqualTo(1));
            Assert.That(coordinator.GetRuntimeSources(), Is.Empty);
        });
    }

    [TestCase(true)]
    [TestCase(false)]
    public void Creation_or_start_failure_is_faulted_without_stopping_other_sources (bool failDuringCreation)
    {
        string failedPath = CreateFile("logs/latest.log");
        string healthyPath = CreateFile("logs/reccactus/reccactus-healthy.txt");
        var factory = new FakeSessionFactory();
        if (failDuringCreation)
        {
            factory.CreationFailurePaths.Add(failedPath);
        }
        else
        {
            factory.StartFailurePaths.Add(failedPath);
        }

        using var coordinator = CreateCoordinator(factory);
        coordinator.Reconcile();
        MinecraftWorkspaceSourceRuntimeState failed = FindState(coordinator, failedPath);
        MinecraftWorkspaceSourceRuntimeState healthy = FindState(coordinator, healthyPath);
        int attemptedCount = factory.CreatedFileIds.Count;

        coordinator.Reconcile();

        Assert.Multiple(() =>
        {
            Assert.That(failed.Status, Is.EqualTo(MinecraftWorkspaceSourceStatus.Faulted));
            Assert.That(failed.Reason, Is.EqualTo(failDuringCreation
                ? MinecraftWorkspaceSourceReason.SessionCreationFailed
                : MinecraftWorkspaceSourceReason.SessionStartFailed));
            Assert.That(healthy.Status, Is.EqualTo(MinecraftWorkspaceSourceStatus.Active));
            Assert.That(factory.GetSession(healthyPath).StartCount, Is.EqualTo(1));
            Assert.That(factory.CreatedFileIds.Count, Is.EqualTo(attemptedCount), "unchanged faulted sources are not retried");
        });

        factory.CreationFailurePaths.Remove(failedPath);
        factory.StartFailurePaths.Remove(failedPath);
        File.Delete(failedPath);
        coordinator.Reconcile();
        CreateFile("logs/latest.log");
        coordinator.Reconcile();

        Assert.Multiple(() =>
        {
            Assert.That(FindState(coordinator, failedPath).Status, Is.EqualTo(MinecraftWorkspaceSourceStatus.Active));
            Assert.That(factory.GetSession(failedPath).StartCount, Is.EqualTo(1));
            Assert.That(factory.CreatedFileIds.Count, Is.EqualTo(attemptedCount + 1));
        });
    }

    [Test]
    public async Task Concurrent_callbacks_are_drained_in_unique_ingress_order_without_duplicates ()
    {
        string firstPath = CreateFile("logs/latest.log");
        string secondPath = CreateFile("logs/reccactus/reccactus-concurrent.txt");
        var factory = new FakeSessionFactory();
        using var coordinator = CreateCoordinator(factory);
        coordinator.Reconcile();
        DiscoveredSourceFile firstSource = GetSource(firstPath);
        DiscoveredSourceFile secondSource = GetSource(secondPath);
        FakeSession first = factory.GetSession(firstPath);
        FakeSession second = factory.GetSession(secondPath);
        const int EventsPerSource = 60;

        Task firstProducer = Task.Run(() => Parallel.For(1, EventsPerSource + 1,
            sequence => first.Emit(CreateEvent(firstSource, sequence, $"first-{sequence}"))));
        Task secondProducer = Task.Run(() => Parallel.For(1, EventsPerSource + 1,
            sequence => second.Emit(CreateEvent(secondSource, sequence, $"second-{sequence}"))));
        await Task.WhenAll(firstProducer, secondProducer).ConfigureAwait(false);

        IReadOnlyList<MinecraftWorkspaceIngressEvent> drained = coordinator.DrainPendingEvents();
        IReadOnlyList<MinecraftWorkspaceIngressEvent> drainedAgain = coordinator.DrainPendingEvents();

        Assert.Multiple(() =>
        {
            Assert.That(drained, Has.Count.EqualTo(EventsPerSource * 2));
            Assert.That(drained.Select(item => item.IngressSequence), Is.EqualTo(Enumerable.Range(1, EventsPerSource * 2).Select(value => (long)value)));
            Assert.That(drained.Select(item => item.IngressSequence).Distinct().Count(), Is.EqualTo(EventsPerSource * 2));
            Assert.That(drainedAgain, Is.Empty);
            Assert.That(coordinator.PendingCount, Is.Zero);
        });
    }

    [Test]
    public void Stop_and_dispose_are_idempotent_and_capture_final_events ()
    {
        string firstPath = CreateFile("logs/latest.log");
        string secondPath = CreateFile("logs/reccactus/reccactus-stop.txt");
        var factory = new FakeSessionFactory();
        using var coordinator = CreateCoordinator(factory);
        coordinator.Reconcile();
        FakeSession first = factory.GetSession(firstPath);
        FakeSession second = factory.GetSession(secondPath);
        first.FinalEvents = [CreateEvent(GetSource(firstPath), 4, "first-final")];
        second.FinalEvents = [CreateEvent(GetSource(secondPath), 5, "second-final")];

        coordinator.Stop();
        coordinator.Dispose();
        first.Emit(CreateEvent(GetSource(firstPath), 6, "stale"));

        IReadOnlyList<MinecraftWorkspaceIngressEvent> events = coordinator.DrainPendingEvents();
        Assert.Multiple(() =>
        {
            Assert.That(first.DisposeCount, Is.EqualTo(1));
            Assert.That(second.DisposeCount, Is.EqualTo(1));
            Assert.That(events.Select(item => item.Event.Message), Is.EquivalentTo(new[] { "first-final", "second-final" }));
            Assert.That(coordinator.GetRuntimeSources(), Is.Empty);
            Assert.That(coordinator.PendingCount, Is.Zero);
        });
    }

    [Test]
    public void Historical_yeezus_rotated_file_present_at_startup_may_start ()
    {
        string rotatedPath = CreateFile("logs/yeezus.log.1");
        var factory = new FakeSessionFactory();
        using var coordinator = CreateCoordinator(factory);

        coordinator.Reconcile();

        Assert.Multiple(() =>
        {
            Assert.That(factory.CreatedFileIds, Has.Count.EqualTo(1));
            Assert.That(factory.GetSession(rotatedPath).StartCount, Is.EqualTo(1));
            Assert.That(FindState(coordinator, rotatedPath).Status, Is.EqualTo(MinecraftWorkspaceSourceStatus.Active));
        });
    }

    [Test]
    public void Runtime_yeezus_primary_to_rotated_is_deferred_without_a_successor_reader ()
    {
        CreateFile("logs/yeezus.log");
        var factory = new FakeSessionFactory();
        using var coordinator = CreateCoordinator(factory);
        coordinator.Reconcile();
        string rotatedPath = CreateFile("logs/yeezus.log.1");

        coordinator.Reconcile();
        coordinator.Reconcile();

        MinecraftWorkspaceSourceRuntimeState rotated = FindState(coordinator, rotatedPath);
        Assert.Multiple(() =>
        {
            Assert.That(rotated.Status, Is.EqualTo(MinecraftWorkspaceSourceStatus.DeferredHandoff));
            Assert.That(rotated.Reason, Is.EqualTo(MinecraftWorkspaceSourceReason.RequiresSegmentHandoff));
            Assert.That(factory.CreatedFileIds, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public void Historical_cfm_final_present_at_startup_may_start ()
    {
        string finalPath = CreateFile("cactusmonitor/sessions/cfm-history.jsonl");
        var factory = new FakeSessionFactory();
        using var coordinator = CreateCoordinator(factory);

        coordinator.Reconcile();

        Assert.Multiple(() =>
        {
            Assert.That(factory.CreatedFileIds, Has.Count.EqualTo(1));
            Assert.That(factory.GetSession(finalPath).StartCount, Is.EqualTo(1));
            Assert.That(FindState(coordinator, finalPath).Status, Is.EqualTo(MinecraftWorkspaceSourceStatus.Active));
        });
    }

    [Test]
    public void Runtime_cfm_active_part_to_final_disposes_predecessor_and_defers_successor ()
    {
        string partPath = CreateFile("cactusmonitor/sessions/cfm-live.jsonl.part");
        DiscoveredSourceFile partSource = GetSource(partPath);
        var factory = new FakeSessionFactory();
        using var coordinator = CreateCoordinator(factory);
        coordinator.Reconcile();
        FakeSession partSession = factory.GetSession(partPath);
        partSession.FinalEvents = [CreateEvent(partSource, 8, "part-final")];
        string finalPath = Path.Combine(Path.GetDirectoryName(partPath)!, "cfm-live.jsonl");
        File.Move(partPath, finalPath);

        coordinator.Reconcile();

        MinecraftWorkspaceSourceRuntimeState final = FindState(coordinator, finalPath);
        IReadOnlyList<MinecraftWorkspaceIngressEvent> events = coordinator.DrainPendingEvents();
        Assert.Multiple(() =>
        {
            Assert.That(partSession.DisposeCount, Is.EqualTo(1));
            Assert.That(final.Status, Is.EqualTo(MinecraftWorkspaceSourceStatus.DeferredHandoff));
            Assert.That(final.Reason, Is.EqualTo(MinecraftWorkspaceSourceReason.RequiresSegmentHandoff));
            Assert.That(factory.CreatedFileIds, Has.Count.EqualTo(1), "the Final successor must not be read from byte zero");
            Assert.That(events.Select(item => item.Event.Message), Is.EqualTo(new[] { "part-final" }));
            Assert.That(coordinator.GetRuntimeSources().Any(state => state.Source.FileId == partSource.FileId), Is.False);
        });
    }

    [Test]
    public void Disappearing_deferred_segment_is_removed_from_runtime_state ()
    {
        CreateFile("logs/yeezus.log");
        var factory = new FakeSessionFactory();
        using var coordinator = CreateCoordinator(factory);
        coordinator.Reconcile();
        string rotatedPath = CreateFile("logs/yeezus.log.1");
        coordinator.Reconcile();
        Assert.That(FindState(coordinator, rotatedPath).Status, Is.EqualTo(MinecraftWorkspaceSourceStatus.DeferredHandoff));

        File.Delete(rotatedPath);
        coordinator.Reconcile();

        Assert.That(coordinator.GetRuntimeSources().Any(state => state.Source.FullPath == rotatedPath), Is.False);
    }

    [Test]
    public void Ingress_envelope_preserves_event_reference_generation_and_source_sequence ()
    {
        string path = CreateFile("logs/latest.log");
        var factory = new FakeSessionFactory();
        using var coordinator = CreateCoordinator(factory);
        coordinator.Reconcile();
        DiscoveredSourceFile source = GetSource(path);
        NormalizedLogEvent parsedEvent = CreateEvent(source, 23, "unchanged", generation: 7);

        factory.GetSession(path).Emit(parsedEvent);
        MinecraftWorkspaceIngressEvent envelope = coordinator.DrainPendingEvents().Single();

        Assert.Multiple(() =>
        {
            Assert.That(envelope.Event, Is.SameAs(parsedEvent));
            Assert.That(envelope.Event.Ref, Is.SameAs(parsedEvent.Ref));
            Assert.That(envelope.Event.Ref.File, Is.SameAs(parsedEvent.Ref.File));
            Assert.That(envelope.Event.Ref.File.Generation, Is.EqualTo(7));
            Assert.That(envelope.Event.Ref.SourceLocalSequence, Is.EqualTo(23));
            Assert.That(envelope.WorkspaceId, Is.EqualTo(source.WorkspaceId));
            Assert.That(envelope.FileId, Is.EqualTo(source.FileId));
            Assert.That(envelope.SourceId, Is.EqualTo(source.SourceId));
            Assert.That(envelope.SegmentRole, Is.EqualTo(source.SegmentRole));
            Assert.That(FindState(coordinator, path).EmittedEventCount, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Real_reader_sessions_for_two_sources_publish_initial_loads_and_independent_appends ()
    {
        const string initialAlpha = "{\"type\":\"CYCLE\",\"sessionId\":\"alpha\",\"sequence\":1,\"timestampEpochMillis\":1790181663412}\n";
        const string initialBeta = "{\"type\":\"CYCLE\",\"sessionId\":\"beta\",\"sequence\":1,\"timestampEpochMillis\":1790181663412}\n";
        string alphaPath = CreateFile("cactusmonitor/sessions/cfm-alpha.jsonl", initialAlpha);
        string betaPath = CreateFile("cactusmonitor/sessions/cfm-beta.jsonl", initialBeta);
        var discovery = CreateDiscovery();
        var factory = new MinecraftWorkspaceLiveSourceSessionFactory(
            PluginRegistry.PluginRegistry.Instance,
            new EncodingOptions { Encoding = Utf8 },
            maximumLineLength: 48);
        using var coordinator = new MinecraftWorkspaceLiveCoordinator(discovery, factory);
        coordinator.Reconcile();

        await WaitUntil(() => coordinator.PendingCount >= 2, "Initial events from both reader sessions were not ingressed").ConfigureAwait(false);
        IReadOnlyList<MinecraftWorkspaceIngressEvent> initial = coordinator.DrainPendingEvents();
        await File.AppendAllTextAsync(alphaPath,
            "{\"type\":\"CYCLE\",\"sessionId\":\"alpha\",\"sequence\":2}\n", Utf8).ConfigureAwait(false);
        await File.AppendAllTextAsync(betaPath,
            "{\"type\":\"CYCLE\",\"sessionId\":\"beta\",\"sequence\":2}\n", Utf8).ConfigureAwait(false);
        await WaitUntil(() => coordinator.PendingCount >= 2, "Appended events from both reader sessions were not ingressed").ConfigureAwait(false);
        IReadOnlyList<MinecraftWorkspaceIngressEvent> appended = coordinator.DrainPendingEvents();

        Assert.Multiple(() =>
        {
            Assert.That(initial, Has.Count.EqualTo(2));
            Assert.That(initial.Select(item => item.FileId), Is.EquivalentTo(new[] { GetSource(alphaPath).FileId, GetSource(betaPath).FileId }));
            Assert.That(initial.Select(item => item.Event.Ref.SourceLocalSequence), Is.All.EqualTo(1));
            Assert.That(appended, Has.Count.EqualTo(2));
            Assert.That(appended.Select(item => item.Event.Ref.SourceLocalSequence), Is.All.EqualTo(2));
            Assert.That(appended.Select(item => item.IngressSequence), Is.EqualTo(appended.Select(item => item.IngressSequence).OrderBy(value => value)));
            Assert.That(appended.Select(item => item.Event.RawText), Is.EquivalentTo(new[]
            {
                "{\"type\":\"CYCLE\",\"sessionId\":\"alpha\",\"sequence\":2}",
                "{\"type\":\"CYCLE\",\"sessionId\":\"beta\",\"sequence\":2}"
            }));
        });
    }

    private MinecraftWorkspaceLiveCoordinator CreateCoordinator (FakeSessionFactory factory) =>
        new(CreateDiscovery(), factory);

    private MinecraftSourceDiscovery CreateDiscovery () =>
        new(new MinecraftWorkspace(_testDirectory));

    private string CreateFile (string relativePath, string content = "")
    {
        string fullPath = Path.Combine(_testDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
        _ = Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content, Utf8);
        return fullPath;
    }

    private DiscoveredSourceFile GetSource (string path) =>
        new MinecraftSourceDiscovery(new MinecraftWorkspace(_testDirectory))
            .Rescan()
            .Files
            .Single(source => string.Equals(source.FullPath, path, StringComparison.OrdinalIgnoreCase));

    private static MinecraftWorkspaceSourceRuntimeState FindState (
        MinecraftWorkspaceLiveCoordinator coordinator,
        string path) => coordinator.GetRuntimeSources()
        .Single(state => string.Equals(state.Source.FullPath, path, StringComparison.OrdinalIgnoreCase));

    private static NormalizedLogEvent CreateEvent (
        DiscoveredSourceFile source,
        long sourceLocalSequence,
        string rawText,
        long generation = 3)
    {
        var fileRef = new FileRef(source.FileId, source.FullPath, generation);
        var eventRef = new EventRef(fileRef, sourceLocalSequence, 10, 20, 1, 1);
        return new NormalizedLogEvent(
            eventRef,
            Attribution.Unknown<string>(),
            Attribution.Unknown<string>(),
            Attribution.Unknown<LogLevel>(),
            null,
            Attribution.Unknown<string>(),
            EventTimestamp.Unknown(),
            EventParseStatus.Parsed,
            rawText,
            rawText);
    }

    private static async Task WaitUntil (Func<bool> condition, string failureMessage)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(8);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(20).ConfigureAwait(false);
        }

        Assert.Fail(failureMessage);
    }

    private sealed class FakeSessionFactory : IMinecraftWorkspaceLiveSourceSessionFactory
    {
        public List<string> CreatedFileIds { get; } = [];

        public HashSet<string> CreationFailurePaths { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> StartFailurePaths { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, FakeSession> Sessions { get; } = new(StringComparer.OrdinalIgnoreCase);

        public IMinecraftWorkspaceLiveSourceSession Create (DiscoveredSourceFile source)
        {
            CreatedFileIds.Add(source.FileId);
            if (CreationFailurePaths.Contains(source.FullPath))
            {
                throw new IOException();
            }

            var session = new FakeSession { FailStart = StartFailurePaths.Contains(source.FullPath) };
            Sessions[source.FullPath] = session;
            return session;
        }

        public FakeSession GetSession (string path) => Sessions[path];
    }

    private sealed class FakeSession : IMinecraftWorkspaceLiveSourceSession
    {
        public event EventHandler<MinecraftLiveSourceEventsProducedEventArgs>? EventsProduced;

        public int StartCount { get; private set; }

        public int DisposeCount { get; private set; }

        public bool FailStart { get; init; }

        public IReadOnlyList<NormalizedLogEvent> FinalEvents { get; set; } = Array.Empty<NormalizedLogEvent>();

        public void StartMonitoring ()
        {
            StartCount++;
            if (FailStart)
            {
                throw new InvalidOperationException();
            }
        }

        public void Emit (NormalizedLogEvent parsedEvent) =>
            EventsProduced?.Invoke(this, new MinecraftLiveSourceEventsProducedEventArgs([parsedEvent]));

        public void Dispose ()
        {
            DisposeCount++;
            if (FinalEvents.Count > 0)
            {
                EventsProduced?.Invoke(this, new MinecraftLiveSourceEventsProducedEventArgs(FinalEvents));
            }
        }
    }

}
