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
            Assert.That(factory.Sessions.Values.Where(session => session.ReadRequest.ImmutableSnapshot)
                .Select(session => session.DisposeCount), Is.All.EqualTo(1));
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
            Assert.That(factory.GetSession(rotatedPath).ReadRequest.ImmutableSnapshot, Is.True);
            Assert.That(factory.GetSession(rotatedPath).DisposeCount, Is.EqualTo(1));
            Assert.That(FindState(coordinator, rotatedPath).Status, Is.EqualTo(MinecraftWorkspaceSourceStatus.ImmutableComplete));
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
    public async Task Handoff_short_or_failed_successor_keeps_checkpoint_for_a_nonzero_retry ()
    {
        string primaryPath = CreateFile("logs/yeezus.log", "primary");
        DiscoveredSourceFile primary = GetSource(primaryPath);
        var factory = new FakeSessionFactory();
        using var coordinator = CreateCoordinator(factory);
        coordinator.Reconcile();

        string rotatedPath = CreateFile("logs/yeezus.log.1", "x");
        DiscoveredSourceFile rotated = GetSource(rotatedPath);
        var checkpoint = new MinecraftSourceHandoffCheckpoint(
            primary.SourceId,
            new FileRef(primary.FileId, primary.FullPath, 1),
            primary.AdapterHint,
            primary.SegmentRole,
            ConsumedByteFrontier: 24,
            NextPhysicalLineNumber: 6,
            ReplayStartByteOffset: 12,
            ReplayStartPhysicalLineNumber: 3,
            ReplayStartsBeforeConsumedFrontier: true,
            HasUnemittedState: true,
            NextSourceLocalSequence: 9);
        factory.GetSession(primaryPath).RaiseHandoffCheckpoint(new MinecraftWorkspaceHandoffCheckpointEventArgs(
            MinecraftHandoffTransitionKind.YeezusPrimaryToRotated,
            checkpoint,
            predecessorFileLength: 24,
            successorFileLength: 1));

        coordinator.Reconcile();
        MinecraftWorkspaceSourceRuntimeState tooShort = FindState(coordinator, rotatedPath);
        Assert.Multiple(() =>
        {
            Assert.That(tooShort.Status, Is.EqualTo(MinecraftWorkspaceSourceStatus.HandoffFaulted));
            Assert.That(tooShort.Reason, Is.EqualTo(MinecraftWorkspaceSourceReason.SuccessorTooShort));
            Assert.That(tooShort.Checkpoint, Is.EqualTo(checkpoint));
            Assert.That(factory.CreatedFileIds, Is.EqualTo(new[] { primary.FileId }));
        });

        await File.WriteAllTextAsync(rotatedPath, new string('x', 24), Utf8).ConfigureAwait(false);
        factory.StartFailurePaths.Add(rotatedPath);
        coordinator.Reconcile();
        MinecraftWorkspaceSourceRuntimeState failedStart = FindState(coordinator, rotatedPath);
        Assert.Multiple(() =>
        {
            Assert.That(failedStart.Status, Is.EqualTo(MinecraftWorkspaceSourceStatus.HandoffFaulted));
            Assert.That(failedStart.Checkpoint, Is.EqualTo(checkpoint));
            Assert.That(factory.ReadRequestsByPath[rotatedPath].StartByteOffset, Is.EqualTo(12));
            Assert.That(factory.ReadRequestsByPath[rotatedPath].StartPhysicalLineNumber, Is.EqualTo(3));
            Assert.That(factory.ReadRequestsByPath[rotatedPath].InitialSourceLocalSequence, Is.EqualTo(9));
            Assert.That(factory.ReadRequestsByPath[rotatedPath].ImmutableSnapshot, Is.True);
        });

        factory.StartFailurePaths.Remove(rotatedPath);
        coordinator.Reconcile();
        coordinator.Reconcile();
        MinecraftWorkspaceSourceRuntimeState retried = FindState(coordinator, rotatedPath);
        Assert.Multiple(() =>
        {
            Assert.That(retried.Status, Is.EqualTo(MinecraftWorkspaceSourceStatus.ImmutableComplete));
            Assert.That(retried.Checkpoint, Is.EqualTo(checkpoint));
            Assert.That(factory.ReadRequestsByPath[rotatedPath].StartByteOffset, Is.EqualTo(12));
            Assert.That(factory.ReadRequestsByPath[rotatedPath].InitialSourceLocalSequence, Is.EqualTo(9));
            Assert.That(factory.CreatedFileIds.Count(id => id == rotated.FileId), Is.EqualTo(2));
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
            Assert.That(factory.GetSession(finalPath).ReadRequest.ImmutableSnapshot, Is.True);
            Assert.That(factory.GetSession(finalPath).DisposeCount, Is.EqualTo(1));
            Assert.That(FindState(coordinator, finalPath).Status, Is.EqualTo(MinecraftWorkspaceSourceStatus.ImmutableComplete));
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
            Assert.That(final.Status, Is.EqualTo(MinecraftWorkspaceSourceStatus.ImmutableComplete));
            Assert.That(factory.CreatedFileIds, Has.Count.EqualTo(2));
            Assert.That(factory.ReadRequestsByPath[finalPath].ImmutableSnapshot, Is.True);
            Assert.That(factory.ReadRequestsByPath[finalPath].StartByteOffset, Is.Zero);
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
        string alphaPath = CreateFile("cactusmonitor/sessions/cfm-alpha.jsonl.part", initialAlpha);
        string betaPath = CreateFile("cactusmonitor/sessions/cfm-beta.jsonl.part", initialBeta);
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

    [Test]
    public async Task Real_cfm_part_to_final_move_replays_a_pending_line_once ()
    {
        const string firstJson = "{\"type\":\"CYCLE\",\"sessionId\":\"cfm-transition\",\"sequence\":1,\"timestampEpochMillis\":1790181663412}";
        const string secondPrefix = "{\"type\":\"CYCLE\",\"sessionId\":\"cfm-transition\",\"sequence\":2";
        const string secondSuffix = ",\"timestampEpochMillis\":1790181663413}\n";
        string partPath = CreateFile("cactusmonitor/sessions/cfm-transition.jsonl.part", firstJson + "\n");
        var factory = new RecordingRealSessionFactory(
            PluginRegistry.PluginRegistry.Instance,
            new EncodingOptions { Encoding = Utf8 },
            maximumLineLength: 256);
        using var coordinator = new MinecraftWorkspaceLiveCoordinator(CreateDiscovery(), factory);
        coordinator.Reconcile();

        await WaitUntil(() => coordinator.PendingCount == 1, "Initial CFM event was not ingressed").ConfigureAwait(false);
        IReadOnlyList<MinecraftWorkspaceIngressEvent> initial = coordinator.DrainPendingEvents();
        string finalJson = secondPrefix + secondSuffix.TrimEnd('\n');
        string finalPath = partPath[..^".part".Length];
        long secondLineStart = Utf8.GetByteCount(firstJson + "\n");

        await File.AppendAllTextAsync(partPath, secondPrefix, Utf8).ConfigureAwait(false);
        IMinecraftWorkspaceSourceHandoffSession partSession =
            (IMinecraftWorkspaceSourceHandoffSession)factory.GetSession(partPath);
        await WaitUntil(
            () => partSession.GetHandoffCheckpoint().ConsumedByteFrontier == secondLineStart + Utf8.GetByteCount(secondPrefix),
            "The active CFM reader did not observe its unterminated final line").ConfigureAwait(false);
        MinecraftSourceHandoffCheckpoint partialCheckpoint = partSession.GetHandoffCheckpoint();

        await File.AppendAllTextAsync(partPath, secondSuffix, Utf8).ConfigureAwait(false);
        File.Move(partPath, finalPath);
        coordinator.Reconcile();

        IReadOnlyList<MinecraftWorkspaceIngressEvent> afterMove = coordinator.DrainPendingEvents();
        MinecraftWorkspaceSourceRuntimeState finalState = FindState(coordinator, finalPath);
        MinecraftWorkspaceIngressEvent[] secondEvents = initial.Concat(afterMove)
            .Where(item => item.Event.RawText == finalJson)
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(partialCheckpoint.ReplayStartByteOffset, Is.EqualTo(secondLineStart));
            Assert.That(partialCheckpoint.ReplayStartPhysicalLineNumber, Is.EqualTo(2));
            Assert.That(partialCheckpoint.HasUnemittedState, Is.True);
            Assert.That(finalState.Status, Is.EqualTo(MinecraftWorkspaceSourceStatus.ImmutableComplete));
            Assert.That(factory.ReadRequestsByPath[finalPath].ImmutableSnapshot, Is.True);
            Assert.That(factory.ReadRequestsByPath[finalPath].StartByteOffset, Is.EqualTo(secondLineStart));
            Assert.That(factory.ReadRequestsByPath[finalPath].InitialSourceLocalSequence, Is.EqualTo(2));
            Assert.That(secondEvents, Has.Length.EqualTo(1));
            Assert.That(secondEvents[0].Event.Ref.SourceLocalSequence, Is.EqualTo(2));
            Assert.That(coordinator.PendingCount, Is.Zero);
        });
    }

    [Test]
    public async Task Real_yeezus_rotation_reuses_historical_rotated_file_without_regressing_sequence ()
    {
        const string historyHeaderOne = "2026-09-24T09:57:00+02:00 [INFO] [yeezus] [Client thread] prior rotation one";
        const string historyHeaderTwo = "2026-09-24T09:58:00+02:00 [INFO] [yeezus] [Client thread] prior rotation two";
        const string historyHeaderThree = "2026-09-24T09:59:00+02:00 [INFO] [yeezus] [Client thread] prior rotation three";
        const string firstHeader = "2026-09-24T10:00:00+02:00 [ERROR] [yeezus-core] [Client thread] first";
        const string firstStack = "    at example.Client.first(Client.java:1)";
        const string pendingHeader = "2026-09-24T10:00:01+02:00 [ERROR] [yeezus-core] [Client thread] pending";
        const string newPrimaryHeader = "2026-09-24T10:00:02+02:00 [INFO] [yeezus] [Client thread] new primary";
        const string nextPrimaryHeader = "2026-09-24T10:00:03+02:00 [INFO] [yeezus] [Client thread] next";
        string rotatedPath = CreateFile(
            "logs/yeezus.log.1",
            historyHeaderOne + "\n" + historyHeaderTwo + "\n" + historyHeaderThree + "\n");
        string primaryPath = CreateFile("logs/yeezus.log", firstHeader + "\n" + firstStack + "\n" + pendingHeader + "\n");
        var factory = new RecordingRealSessionFactory(
            PluginRegistry.PluginRegistry.Instance,
            new EncodingOptions { Encoding = Utf8 },
            maximumLineLength: 256);
        using var coordinator = new MinecraftWorkspaceLiveCoordinator(CreateDiscovery(), factory);
        coordinator.Reconcile();

        await WaitUntil(() => coordinator.PendingCount >= 4, "Initial Yeezus history and primary events were not ingressed").ConfigureAwait(false);
        IReadOnlyList<MinecraftWorkspaceIngressEvent> initial = coordinator.DrainPendingEvents();
        MinecraftWorkspaceSourceRuntimeState initialRotation = FindState(coordinator, rotatedPath);
        long priorRotatedNextSequence = initialRotation.NextSourceLocalSequence;
        Assert.Multiple(() =>
        {
            Assert.That(initialRotation.Generation, Is.EqualTo(1));
            Assert.That(priorRotatedNextSequence, Is.EqualTo(4), "the historical rotated source has already consumed three events");
        });

        IMinecraftWorkspaceLiveSourceSessionProgress primaryProgress =
            (IMinecraftWorkspaceLiveSourceSessionProgress)factory.GetSession(primaryPath);
        Assert.That(primaryProgress.NextSourceLocalSequence, Is.EqualTo(2), "the second primary record is pending");

        File.Delete(rotatedPath);
        File.Move(primaryPath, rotatedPath);
        await File.WriteAllTextAsync(primaryPath, newPrimaryHeader + "\n", Utf8).ConfigureAwait(false);
        await WaitUntil(
            () => primaryProgress.CurrentFile.Generation == 2,
            "The primary reader did not continue in its next generation").ConfigureAwait(false);
        coordinator.Reconcile();
        DateTimeOffset handoffDeadline = DateTimeOffset.UtcNow.AddSeconds(8);
        while (DateTimeOffset.UtcNow < handoffDeadline &&
            factory.ReadRequestsByPath[rotatedPath].InitialGeneration != initialRotation.Generation + 1)
        {
            coordinator.Reconcile();
            await Task.Delay(20).ConfigureAwait(false);
        }

        MinecraftWorkspaceSourceRuntimeState handoffAttempt = FindState(coordinator, rotatedPath);
        Assert.That(
            factory.ReadRequestsByPath[rotatedPath].InitialGeneration,
            Is.EqualTo(initialRotation.Generation + 1),
            $"rotated state={handoffAttempt.Status}/{handoffAttempt.Reason}; checkpoint={handoffAttempt.Checkpoint}");

        MinecraftWorkspaceSourceRuntimeState replayedRotation = FindState(coordinator, rotatedPath);
        MinecraftSourceHandoffCheckpoint rotationCheckpoint = replayedRotation.Checkpoint!;
        MinecraftWorkspaceSourceReadRequest rotatedReadRequest = factory.ReadRequestsByPath[rotatedPath];
        Assert.That(replayedRotation.Status, Is.EqualTo(MinecraftWorkspaceSourceStatus.ImmutableComplete));
        Assert.That(rotationCheckpoint.ReplayStartByteOffset, Is.EqualTo(Utf8.GetByteCount(firstHeader + "\n" + firstStack + "\n")));
        Assert.That(rotationCheckpoint.ReplayStartPhysicalLineNumber, Is.EqualTo(3));
        Assert.That(rotatedReadRequest.StartByteOffset, Is.EqualTo(rotationCheckpoint.ReplayStartByteOffset));
        Assert.That(rotatedReadRequest.StartPhysicalLineNumber, Is.EqualTo(rotationCheckpoint.ReplayStartPhysicalLineNumber));
        Assert.That(rotatedReadRequest.InitialGeneration, Is.EqualTo(initialRotation.Generation + 1));
        Assert.That(rotatedReadRequest.InitialSourceLocalSequence, Is.EqualTo(priorRotatedNextSequence));

        Assert.That(primaryProgress.NextSourceLocalSequence, Is.EqualTo(3));
        await File.AppendAllTextAsync(primaryPath, nextPrimaryHeader + "\n", Utf8).ConfigureAwait(false);
        await WaitUntil(() => coordinator.PendingCount >= 2, "Rotated pending record and continued primary event were not ingressed").ConfigureAwait(false);
        IReadOnlyList<MinecraftWorkspaceIngressEvent> afterRotation = coordinator.DrainPendingEvents();
        MinecraftWorkspaceIngressEvent replayedPending = afterRotation.Single(item => item.Event.RawText == pendingHeader);
        MinecraftWorkspaceIngressEvent newPrimary = afterRotation.Single(item => item.Event.RawText == newPrimaryHeader);

        Assert.Multiple(() =>
        {
            Assert.That(initial.Any(item => item.Event.RawText == pendingHeader), Is.False);
            Assert.That(initial.Count(item => item.Event.RawText == firstHeader + "\n" + firstStack), Is.EqualTo(1));
            Assert.That(initial.Count(item => item.Event.RawText == historyHeaderOne), Is.EqualTo(1));
            Assert.That(initial.Count(item => item.Event.RawText == historyHeaderTwo), Is.EqualTo(1));
            Assert.That(initial.Count(item => item.Event.RawText == historyHeaderThree), Is.EqualTo(1));
            Assert.That(afterRotation.Any(item => item.Event.RawText == historyHeaderOne), Is.False);
            Assert.That(afterRotation.Any(item => item.Event.RawText == historyHeaderTwo), Is.False);
            Assert.That(afterRotation.Any(item => item.Event.RawText == historyHeaderThree), Is.False);
            Assert.That(
                initial.Concat(afterRotation).Count(item => item.Event.RawText == historyHeaderOne),
                Is.EqualTo(1));
            Assert.That(
                initial.Concat(afterRotation).Count(item => item.Event.RawText == historyHeaderTwo),
                Is.EqualTo(1));
            Assert.That(
                initial.Concat(afterRotation).Count(item => item.Event.RawText == historyHeaderThree),
                Is.EqualTo(1));
            Assert.That(replayedPending.FileId, Is.EqualTo(initialRotation.Source.FileId));
            Assert.That(replayedPending.Event.Ref.File.Generation, Is.EqualTo(2));
            Assert.That(replayedPending.Event.Ref.SourceLocalSequence, Is.GreaterThanOrEqualTo(priorRotatedNextSequence));
            Assert.That(newPrimary.FileId, Is.EqualTo(GetSource(primaryPath).FileId));
            Assert.That(newPrimary.Event.Ref.File.Generation, Is.EqualTo(2));
            Assert.That(newPrimary.Event.Ref.SourceLocalSequence, Is.EqualTo(3));
        });
    }

    [Test]
    public async Task Real_yeezus_move_gap_captures_pending_state_before_disposing_primary ()
    {
        const string historyHeader = "2026-09-24T09:59:00+02:00 [INFO] [yeezus] [Client thread] prior rotation";
        const string firstHeader = "2026-09-24T10:00:00+02:00 [ERROR] [yeezus-core] [Client thread] first";
        const string pendingHeader = "2026-09-24T10:00:01+02:00 [ERROR] [yeezus-core] [Client thread] pending";
        const string nextPrimaryHeader = "2026-09-24T10:00:02+02:00 [INFO] [yeezus] [Client thread] new primary";
        string rotatedPath = CreateFile("logs/yeezus.log.1", historyHeader + "\n");
        string primaryPath = CreateFile("logs/yeezus.log", firstHeader + "\n" + pendingHeader + "\n");
        var factory = new RecordingRealSessionFactory(
            PluginRegistry.PluginRegistry.Instance,
            new EncodingOptions { Encoding = Utf8 },
            maximumLineLength: 256);
        using var coordinator = new MinecraftWorkspaceLiveCoordinator(CreateDiscovery(), factory);
        coordinator.Reconcile();

        await WaitUntil(() => coordinator.PendingCount >= 2, "Initial Yeezus history and primary events were not ingressed").ConfigureAwait(false);
        IReadOnlyList<MinecraftWorkspaceIngressEvent> initial = coordinator.DrainPendingEvents();
        long replayStart = Utf8.GetByteCount(firstHeader + "\n");

        File.Delete(rotatedPath);
        File.Move(primaryPath, rotatedPath);
        coordinator.Reconcile();

        MinecraftWorkspaceSourceRuntimeState replayedRotation = FindState(coordinator, rotatedPath);
        MinecraftWorkspaceIngressEvent replayedPending = coordinator.DrainPendingEvents()
            .Single(item => item.Event.RawText == pendingHeader);
        Assert.Multiple(() =>
        {
            Assert.That(replayedRotation.Status, Is.EqualTo(MinecraftWorkspaceSourceStatus.ImmutableComplete));
            Assert.That(replayedRotation.Checkpoint?.ReplayStartByteOffset, Is.EqualTo(replayStart));
            Assert.That(factory.ReadRequestsByPath[rotatedPath].StartByteOffset, Is.EqualTo(replayStart));
            Assert.That(factory.ReadRequestsByPath[rotatedPath].InitialSourceLocalSequence, Is.EqualTo(2));
            Assert.That(initial.Any(item => item.Event.RawText == pendingHeader), Is.False);
            Assert.That(replayedPending.FileId, Is.EqualTo(GetSource(rotatedPath).FileId));
            Assert.That(replayedPending.Event.Ref.SourceLocalSequence, Is.EqualTo(2));
        });

        await File.WriteAllTextAsync(primaryPath, nextPrimaryHeader + "\n", Utf8).ConfigureAwait(false);
        coordinator.Reconcile();
        IMinecraftWorkspaceLiveSourceSessionProgress continuedPrimary =
            (IMinecraftWorkspaceLiveSourceSessionProgress)factory.GetSession(primaryPath);
        Assert.That(continuedPrimary.CurrentFile.Generation, Is.EqualTo(2));
        Assert.That(continuedPrimary.NextSourceLocalSequence, Is.EqualTo(3));
    }

    private sealed class RecordingRealSessionFactory :
        IMinecraftWorkspaceLiveSourceSessionFactory,
        IMinecraftWorkspaceLiveSourceSessionFactoryWithReadRequest
    {
        private readonly MinecraftWorkspaceLiveSourceSessionFactory _inner;

        public RecordingRealSessionFactory (
            IPluginRegistry pluginRegistry,
            EncodingOptions encodingOptions,
            int maximumLineLength)
        {
            _inner = new MinecraftWorkspaceLiveSourceSessionFactory(pluginRegistry, encodingOptions, maximumLineLength);
        }

        public Dictionary<string, IMinecraftWorkspaceLiveSourceSession> Sessions { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, MinecraftWorkspaceSourceReadRequest> ReadRequestsByPath { get; } = new(StringComparer.OrdinalIgnoreCase);

        public IMinecraftWorkspaceLiveSourceSession Create (DiscoveredSourceFile source) =>
            Create(source, MinecraftWorkspaceSourceReadRequest.Live());

        public IMinecraftWorkspaceLiveSourceSession Create (
            DiscoveredSourceFile source,
            MinecraftWorkspaceSourceReadRequest readRequest)
        {
            IMinecraftWorkspaceLiveSourceSession session = _inner.Create(source, readRequest);
            Sessions[source.FullPath] = session;
            ReadRequestsByPath[source.FullPath] = readRequest;
            return session;
        }

        public IMinecraftWorkspaceLiveSourceSession GetSession (string path) => Sessions[path];
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

    private sealed class FakeSessionFactory :
        IMinecraftWorkspaceLiveSourceSessionFactory,
        IMinecraftWorkspaceLiveSourceSessionFactoryWithReadRequest
    {
        public List<string> CreatedFileIds { get; } = [];

        public HashSet<string> CreationFailurePaths { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> StartFailurePaths { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, FakeSession> Sessions { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, MinecraftWorkspaceSourceReadRequest> ReadRequestsByPath { get; } = new(StringComparer.OrdinalIgnoreCase);

        public IMinecraftWorkspaceLiveSourceSession Create (DiscoveredSourceFile source)
        {
            return Create(source, MinecraftWorkspaceSourceReadRequest.Live());
        }

        public IMinecraftWorkspaceLiveSourceSession Create (
            DiscoveredSourceFile source,
            MinecraftWorkspaceSourceReadRequest readRequest)
        {
            CreatedFileIds.Add(source.FileId);
            if (CreationFailurePaths.Contains(source.FullPath))
            {
                throw new IOException();
            }

            var session = new FakeSession
            {
                FailStart = StartFailurePaths.Contains(source.FullPath),
                ReadRequest = readRequest,
                Source = source
            };
            Sessions[source.FullPath] = session;
            ReadRequestsByPath[source.FullPath] = readRequest;
            return session;
        }

        public FakeSession GetSession (string path) => Sessions[path];
    }

    private sealed class FakeSession :
        IMinecraftWorkspaceLiveSourceSession,
        IMinecraftWorkspaceSourceHandoffSession
    {
        public event EventHandler<MinecraftLiveSourceEventsProducedEventArgs>? EventsProduced;

        public event EventHandler<MinecraftWorkspaceHandoffCheckpointEventArgs>? HandoffCheckpointCaptured;

        public int StartCount { get; private set; }

        public int DisposeCount { get; private set; }

        public bool FailStart { get; init; }

        public MinecraftWorkspaceSourceReadRequest ReadRequest { get; init; } = MinecraftWorkspaceSourceReadRequest.Live();

        public DiscoveredSourceFile Source { get; init; } = null!;

        public IReadOnlyList<NormalizedLogEvent> FinalEvents { get; set; } = Array.Empty<NormalizedLogEvent>();

        public MinecraftSourceHandoffCheckpoint? Checkpoint { get; private set; }

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

        public MinecraftSourceHandoffCheckpoint GetHandoffCheckpoint () => Checkpoint ?? new MinecraftSourceHandoffCheckpoint(
            Source.SourceId,
            new FileRef(Source.FileId, Source.FullPath, ReadRequest.InitialGeneration),
            Source.AdapterHint,
            Source.SegmentRole,
            ConsumedByteFrontier: 0,
            NextPhysicalLineNumber: 1,
            ReplayStartByteOffset: 0,
            ReplayStartPhysicalLineNumber: 1,
            ReplayStartsBeforeConsumedFrontier: false,
            HasUnemittedState: false,
            NextSourceLocalSequence: ReadRequest.InitialSourceLocalSequence);

        public MinecraftSourceHandoffCheckpoint CaptureAndCloseForHandoff () => GetHandoffCheckpoint();

        public void RaiseHandoffCheckpoint (MinecraftWorkspaceHandoffCheckpointEventArgs args)
        {
            Checkpoint = args.Checkpoint;
            HandoffCheckpointCaptured?.Invoke(this, args);
        }

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
