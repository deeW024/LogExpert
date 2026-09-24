using System.Text;

using LogExpert.Core.Classes.Log;
using LogExpert.Core.Classes.MinecraftLogs;
using LogExpert.Core.Entities;

using NUnit.Framework;

namespace LogExpert.Tests;

[TestFixture]
internal sealed class MinecraftWorkspaceTimelineTests
{
    private const string WorkspaceId = "workspace-test";
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private readonly DateTimeOffset _fallbackUtc = new(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);
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
    public void Source_timestamps_normalize_to_utc_and_merge_chronologically ()
    {
        MinecraftWorkspaceIngressEvent later = CreateIngress(
            1, "later", fileId: "later-file", timestamp: SourceTime("2026-09-25T09:30:00-01:00"));
        MinecraftWorkspaceIngressEvent earlier = CreateIngress(
            2, "earlier", fileId: "earlier-file", timestamp: SourceTime("2026-09-25T12:00:00+02:00"));
        var timeline = new MinecraftWorkspaceTimeline(WorkspaceId);

        timeline.AppendBatch([later, earlier]);
        IReadOnlyList<MinecraftWorkspaceTimelineEntry> snapshot = timeline.GetOrderedSnapshot();

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Select(entry => entry.IngressEvent.Event.Message), Is.EqualTo(new[] { "earlier", "later" }));
            Assert.That(snapshot[0].CandidateTimestampUtc, Is.EqualTo(new DateTimeOffset(2026, 9, 25, 10, 0, 0, TimeSpan.Zero)));
            Assert.That(snapshot[1].CandidateTimestampUtc, Is.EqualTo(new DateTimeOffset(2026, 9, 25, 10, 30, 0, TimeSpan.Zero)));
            Assert.That(snapshot.All(entry => entry.CandidateTimestampUtc.Offset == TimeSpan.Zero), Is.True);
            Assert.That(snapshot.All(entry => entry.TimestampBasis == MinecraftWorkspaceTimelineTimestampBasis.Source), Is.True);
        });
    }

    [Test]
    public void Equal_timestamp_uses_sequence_file_generation_offset_and_ingress_tie_breakers ()
    {
        EventTimestamp timestamp = SourceTime("2026-09-25T10:00:00Z");
        MinecraftWorkspaceIngressEvent[] batch =
        [
            CreateIngress(30, "source-sequence-two", fileId: "a-seq-two", sourceSequence: 2, timestamp: timestamp),
            CreateIngress(20, "source-sequence-one", fileId: "z-seq-one", sourceSequence: 1, timestamp: timestamp),
            CreateIngress(8, "same-offset-later-ingress", fileId: "a-file", sourceSequence: 5, startOffset: 3, timestamp: timestamp),
            CreateIngress(6, "same-offset-earlier-ingress", fileId: "a-file", sourceSequence: 5, startOffset: 3, timestamp: timestamp),
            CreateIngress(9, "later-offset", fileId: "a-file", sourceSequence: 5, startOffset: 9, timestamp: timestamp),
            CreateIngress(5, "later-generation", fileId: "a-file", sourceSequence: 5, generation: 2, startOffset: 3, timestamp: timestamp),
            CreateIngress(4, "ordinal-file", fileId: "b-file", sourceSequence: 5, timestamp: timestamp)
        ];
        var timeline = new MinecraftWorkspaceTimeline(WorkspaceId);

        timeline.AppendBatch(batch);

        Assert.That(timeline.GetOrderedSnapshot().Select(entry => entry.IngressEvent.Event.Message), Is.EqualTo(new[]
        {
            "source-sequence-one",
            "source-sequence-two",
            "same-offset-earlier-ingress",
            "same-offset-later-ingress",
            "later-offset",
            "later-generation",
            "ordinal-file"
        }));
    }

    [Test]
    public void Repeated_snapshot_is_deterministic_and_read_only ()
    {
        var timeline = new MinecraftWorkspaceTimeline(WorkspaceId);
        timeline.AppendBatch(
        [
            CreateIngress(3, "c", fileId: "file-c", timestamp: SourceTime("2026-09-25T10:02:00Z")),
            CreateIngress(1, "a", fileId: "file-a", timestamp: SourceTime("2026-09-25T10:00:00Z")),
            CreateIngress(2, "b", fileId: "file-b", timestamp: SourceTime("2026-09-25T10:01:00Z"))
        ]);

        IReadOnlyList<MinecraftWorkspaceTimelineEntry> first = timeline.GetOrderedSnapshot();
        IReadOnlyList<MinecraftWorkspaceTimelineEntry> second = timeline.GetOrderedSnapshot();

        Assert.Multiple(() =>
        {
            Assert.That(second.Select(entry => entry.Identity), Is.EqualTo(first.Select(entry => entry.Identity)));
            Assert.That(first, Is.InstanceOf<IList<MinecraftWorkspaceTimelineEntry>>());
            Assert.That(((IList<MinecraftWorkspaceTimelineEntry>)first).IsReadOnly, Is.True);
        });
    }

    [Test]
    public void Backwards_source_clock_preserves_physical_source_order_and_marks_adjustment ()
    {
        var first = CreateIngress(1, "first", fileId: "one-file", sourceSequence: 1, timestamp: SourceTime("2026-09-25T10:00:00Z"));
        var second = CreateIngress(2, "clock-rollback", fileId: "one-file", sourceSequence: 2, timestamp: SourceTime("2026-09-25T09:59:00Z"));
        var timeline = new MinecraftWorkspaceTimeline(WorkspaceId);

        timeline.AppendBatch([second, first]);
        IReadOnlyList<MinecraftWorkspaceTimelineEntry> snapshot = timeline.GetOrderedSnapshot();

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Select(entry => entry.IngressEvent.Event.Message), Is.EqualTo(new[] { "first", "clock-rollback" }));
            Assert.That(snapshot[1].CandidateTimestampUtc, Is.EqualTo(new DateTimeOffset(2026, 9, 25, 9, 59, 0, TimeSpan.Zero)));
            Assert.That(snapshot[1].EffectiveTimestampUtc, Is.EqualTo(snapshot[0].EffectiveTimestampUtc));
            Assert.That(snapshot[1].WasTimestampAdjustedForSourceOrder, Is.True);
            Assert.That(snapshot.All(entry => !entry.IsLate), Is.True);
        });
    }

    [Test]
    public void Producer_sequence_track_spans_physical_files_within_one_logical_source ()
    {
        var first = CreateIngress(
            1, "producer-one", sourceId: "cfm-session", fileId: "part-file", sourceSequence: 40,
            producerSequence: 1, timestamp: SourceTime("2026-09-25T10:00:00Z"));
        var second = CreateIngress(
            2, "producer-two", sourceId: "cfm-session", fileId: "final-file", sourceSequence: 1,
            producerSequence: 2, timestamp: SourceTime("2026-09-25T09:00:00Z"));
        var timeline = new MinecraftWorkspaceTimeline(WorkspaceId);

        timeline.AppendBatch([second, first]);
        IReadOnlyList<MinecraftWorkspaceTimelineEntry> snapshot = timeline.GetOrderedSnapshot();

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Select(entry => entry.IngressEvent.Event.Message), Is.EqualTo(new[] { "producer-one", "producer-two" }));
            Assert.That(snapshot[1].EffectiveTimestampUtc, Is.EqualTo(snapshot[0].EffectiveTimestampUtc));
            Assert.That(snapshot[1].WasTimestampAdjustedForSourceOrder, Is.True);
            Assert.That(snapshot[1].IngressEvent.FileId, Is.EqualTo("final-file"));
        });
    }

    [Test]
    public void Valid_source_timestamp_takes_precedence_over_workspace_ingress_fallback ()
    {
        var ingress = CreateIngress(
            1, "source-time", timestamp: SourceTime("2026-09-25T10:00:00Z"),
            ingestedAtUtc: new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero));
        var timeline = new MinecraftWorkspaceTimeline(WorkspaceId);

        timeline.AppendBatch([ingress]);
        MinecraftWorkspaceTimelineEntry entry = timeline.GetOrderedSnapshot().Single();

        Assert.Multiple(() =>
        {
            Assert.That(entry.TimestampBasis, Is.EqualTo(MinecraftWorkspaceTimelineTimestampBasis.Source));
            Assert.That(entry.CandidateTimestampUtc, Is.EqualTo(new DateTimeOffset(2026, 9, 25, 10, 0, 0, TimeSpan.Zero)));
        });
    }

    [Test]
    public void Explicit_event_ingest_timestamp_precedes_workspace_fallback_and_normalizes_to_utc ()
    {
        var ingress = CreateIngress(
            1, "event-ingest", timestamp: EventTimestamp.FromIngest(new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.FromHours(2))));
        var timeline = new MinecraftWorkspaceTimeline(WorkspaceId);

        timeline.AppendBatch([ingress]);
        MinecraftWorkspaceTimelineEntry entry = timeline.GetOrderedSnapshot().Single();

        Assert.Multiple(() =>
        {
            Assert.That(entry.TimestampBasis, Is.EqualTo(MinecraftWorkspaceTimelineTimestampBasis.EventIngest));
            Assert.That(entry.CandidateTimestampUtc, Is.EqualTo(new DateTimeOffset(2026, 9, 25, 10, 0, 0, TimeSpan.Zero)));
            Assert.That(entry.CandidateTimestampUtc.Offset, Is.EqualTo(TimeSpan.Zero));
        });
    }

    [Test]
    public void Unknown_timestamp_uses_ingress_utc_and_explicit_fallback_basis ()
    {
        var ingress = CreateIngress(1, "unknown-time", ingestedAtUtc: _fallbackUtc);
        var timeline = new MinecraftWorkspaceTimeline(WorkspaceId);

        timeline.AppendBatch([ingress]);
        MinecraftWorkspaceTimelineEntry entry = timeline.GetOrderedSnapshot().Single();

        Assert.Multiple(() =>
        {
            Assert.That(entry.TimestampBasis, Is.EqualTo(MinecraftWorkspaceTimelineTimestampBasis.WorkspaceIngressFallback));
            Assert.That(entry.CandidateTimestampUtc, Is.EqualTo(_fallbackUtc));
        });
    }

    [Test]
    public void Latest_log_time_of_day_stays_unknown_and_timeline_uses_ingress_fallback ()
    {
        const string rawText = "[18:41:03] [Render thread/ERROR]: Synthetic event";
        var file = new FileRef("latest-file", "logs/latest.log", 1);
        var input = new LogParserInput(file, 1, 0, Utf8.GetByteCount(rawText), 1, 1, rawText, IsComplete: true);
        NormalizedLogEvent parsed = new MinecraftLatestLogParser().Parse(input);
        var ingress = Wrap(parsed, 1, "latest-source", "latest-file", _fallbackUtc);
        var timeline = new MinecraftWorkspaceTimeline(WorkspaceId);

        timeline.AppendBatch([ingress]);
        MinecraftWorkspaceTimelineEntry entry = timeline.GetOrderedSnapshot().Single();

        Assert.Multiple(() =>
        {
            Assert.That(parsed.Timestamp.Provenance, Is.EqualTo(TimestampProvenance.Unknown));
            Assert.That(parsed.Timestamp.Value, Is.Null);
            Assert.That(parsed.Timestamp.RawValue, Is.EqualTo("18:41:03"));
            Assert.That(entry.TimestampBasis, Is.EqualTo(MinecraftWorkspaceTimelineTimestampBasis.WorkspaceIngressFallback));
            Assert.That(entry.CandidateTimestampUtc, Is.EqualTo(_fallbackUtc));
        });
    }

    [Test]
    public void Late_event_in_a_later_batch_is_marked_and_inserted_at_constrained_time_position ()
    {
        var timeline = new MinecraftWorkspaceTimeline(WorkspaceId);
        MinecraftWorkspaceIngressEvent committed = CreateIngress(
            1, "committed", fileId: "later-track", timestamp: SourceTime("2026-09-25T12:00:00Z"));
        timeline.AppendBatch([committed]);
        MinecraftWorkspaceIngressEvent late = CreateIngress(
            2, "late", fileId: "earlier-track", timestamp: SourceTime("2026-09-25T11:00:00Z"));

        timeline.AppendBatch([late]);
        IReadOnlyList<MinecraftWorkspaceTimelineEntry> snapshot = timeline.GetOrderedSnapshot();

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Select(entry => entry.IngressEvent.Event.Message), Is.EqualTo(new[] { "late", "committed" }));
            Assert.That(snapshot[0].IsLate, Is.True);
            Assert.That(snapshot[1].IsLate, Is.False);
            Assert.That(snapshot[0].CandidateTimestampUtc, Is.LessThan(snapshot[1].CandidateTimestampUtc));
            Assert.That(timeline.Count, Is.EqualTo(2));
            Assert.That(timeline.WatermarkUtc, Is.EqualTo(snapshot[1].CandidateTimestampUtc));
        });
    }

    [Test]
    public void Events_in_one_initial_batch_do_not_mark_each_other_late ()
    {
        var timeline = new MinecraftWorkspaceTimeline(WorkspaceId);
        timeline.AppendBatch(
        [
            CreateIngress(1, "newer", fileId: "newer-track", timestamp: SourceTime("2026-09-25T12:00:00Z")),
            CreateIngress(2, "older", fileId: "older-track", timestamp: SourceTime("2026-09-25T11:00:00Z"))
        ]);

        Assert.That(timeline.GetOrderedSnapshot().All(entry => !entry.IsLate), Is.True);
    }

    [Test]
    public void Exact_duplicate_ingress_delivery_is_idempotent_within_and_across_batches ()
    {
        MinecraftWorkspaceIngressEvent ingress = CreateIngress(1, "same");
        var timeline = new MinecraftWorkspaceTimeline(WorkspaceId);

        timeline.AppendBatch([ingress, ingress]);
        timeline.AppendBatch([ingress]);

        Assert.Multiple(() =>
        {
            Assert.That(timeline.Count, Is.EqualTo(1));
            Assert.That(timeline.GetOrderedSnapshot().Select(entry => entry.Identity), Is.EqualTo(new long[] { 1 }));
        });
    }

    [Test]
    public void Conflicting_reuse_of_ingress_sequence_is_rejected_without_partial_acceptance ()
    {
        MinecraftWorkspaceIngressEvent accepted = CreateIngress(1, "accepted");
        MinecraftWorkspaceIngressEvent conflicting = accepted with
        {
            Event = accepted.Event with { Message = accepted.Event.Message + " changed", RawText = accepted.Event.RawText + " changed" }
        };
        var timeline = new MinecraftWorkspaceTimeline(WorkspaceId);
        timeline.AppendBatch([accepted]);

        Assert.Throws<InvalidOperationException>(() => timeline.AppendBatch([CreateIngress(2, "would-be-partial"), conflicting]));

        Assert.Multiple(() =>
        {
            Assert.That(timeline.Count, Is.EqualTo(1));
            Assert.That(timeline.GetOrderedSnapshot().Single().IngressEvent.Event.Message, Is.EqualTo("accepted"));
        });
    }

    [Test]
    public void Workspace_mismatch_is_rejected_without_acceptance ()
    {
        var timeline = new MinecraftWorkspaceTimeline(WorkspaceId);
        MinecraftWorkspaceIngressEvent mismatch = CreateIngress(1, "other-workspace", workspaceId: "different-workspace");

        Assert.Throws<ArgumentException>(() => timeline.AppendBatch([mismatch]));
        Assert.That(timeline.Count, Is.Zero);
    }

    [Test]
    public void Timeline_preserves_original_event_timestamp_and_source_provenance_objects ()
    {
        MinecraftWorkspaceIngressEvent first = CreateIngress(
            1, "first", fileId: "one-file", sourceSequence: 1, timestamp: SourceTime("2026-09-25T10:00:00Z"), producerSequence: 8);
        MinecraftWorkspaceIngressEvent second = CreateIngress(
            2, "second", fileId: "one-file", sourceSequence: 2, timestamp: SourceTime("2026-09-25T09:00:00Z"), producerSequence: 9);
        EventRef originalEventRef = second.Event.Ref;
        FileRef originalFileRef = second.Event.Ref.File;
        EventTimestamp originalTimestamp = second.Event.Timestamp;
        long? originalProducerSequence = second.Event.ProducerSequence;
        var timeline = new MinecraftWorkspaceTimeline(WorkspaceId);

        timeline.AppendBatch([first, second]);
        MinecraftWorkspaceTimelineEntry entry = timeline.GetOrderedSnapshot().Single(item => item.Identity == 2);

        Assert.Multiple(() =>
        {
            Assert.That(entry.IngressEvent, Is.SameAs(second));
            Assert.That(entry.IngressEvent.Event, Is.SameAs(second.Event));
            Assert.That(entry.IngressEvent.Event.Ref, Is.SameAs(originalEventRef));
            Assert.That(entry.IngressEvent.Event.Ref.File, Is.SameAs(originalFileRef));
            Assert.That(entry.IngressEvent.Event.Timestamp, Is.SameAs(originalTimestamp));
            Assert.That(entry.IngressEvent.Event.Timestamp.Value, Is.EqualTo(new DateTimeOffset(2026, 9, 25, 9, 0, 0, TimeSpan.Zero)));
            Assert.That(entry.IngressEvent.Event.Ref.SourceLocalSequence, Is.EqualTo(2));
            Assert.That(entry.IngressEvent.Event.ProducerSequence, Is.EqualTo(originalProducerSequence));
            Assert.That(entry.WasTimestampAdjustedForSourceOrder, Is.True);
        });
    }

    [Test]
    public void Priority_queue_merge_keeps_multiple_tracks_once_in_deterministic_priority_order ()
    {
        var timeline = new MinecraftWorkspaceTimeline(WorkspaceId);
        MinecraftWorkspaceIngressEvent[] events =
        [
            CreateIngress(5, "alpha-two", sourceId: "alpha", fileId: "alpha-file", sourceSequence: 2, timestamp: SourceTime("2026-09-25T10:02:00Z")),
            CreateIngress(2, "beta-one", sourceId: "beta", fileId: "beta-file", sourceSequence: 1, timestamp: SourceTime("2026-09-25T10:01:00Z")),
            CreateIngress(1, "alpha-one", sourceId: "alpha", fileId: "alpha-file", sourceSequence: 1, timestamp: SourceTime("2026-09-25T10:00:00Z")),
            CreateIngress(3, "gamma-one", sourceId: "gamma", fileId: "gamma-file", sourceSequence: 1, timestamp: SourceTime("2026-09-25T10:01:30Z")),
            CreateIngress(4, "beta-two", sourceId: "beta", fileId: "beta-file", sourceSequence: 2, timestamp: SourceTime("2026-09-25T10:03:00Z"))
        ];

        timeline.AppendBatch(events);
        IReadOnlyList<MinecraftWorkspaceTimelineEntry> first = timeline.GetOrderedSnapshot();
        IReadOnlyList<MinecraftWorkspaceTimelineEntry> second = timeline.GetOrderedSnapshot();

        Assert.Multiple(() =>
        {
            Assert.That(first.Select(entry => entry.IngressEvent.Event.Message), Is.EqualTo(new[]
            {
                "alpha-one", "beta-one", "gamma-one", "alpha-two", "beta-two"
            }));
            Assert.That(first.Select(entry => entry.Identity).Distinct().Count(), Is.EqualTo(events.Length));
            Assert.That(first.Select(entry => entry.Identity), Is.EqualTo(second.Select(entry => entry.Identity)));
            Assert.That(timeline.Count, Is.EqualTo(events.Length));
        });
    }

    [Test]
    public async Task Real_reader_coordinator_batches_merge_and_later_late_event_is_inserted_once ()
    {
        const long AlphaFirstMillis = 1_790_181_663_412;
        const long BetaFirstMillis = AlphaFirstMillis + 1_000;
        string alphaInitial = $"{{\"type\":\"CYCLE\",\"sessionId\":\"alpha\",\"sequence\":1,\"timestampEpochMillis\":{AlphaFirstMillis}}}\n";
        string betaInitial = $"{{\"type\":\"CYCLE\",\"sessionId\":\"beta\",\"sequence\":1,\"timestampEpochMillis\":{BetaFirstMillis}}}\n";
        string alphaLate = $"{{\"type\":\"CYCLE\",\"sessionId\":\"alpha\",\"sequence\":2,\"timestampEpochMillis\":{AlphaFirstMillis - 500}}}\n";
        string alphaPath = CreateFile("cactusmonitor/sessions/cfm-alpha.jsonl.part", alphaInitial);
        string betaPath = CreateFile("cactusmonitor/sessions/cfm-beta.jsonl.part", betaInitial);
        var discovery = new MinecraftSourceDiscovery(new MinecraftWorkspace(_testDirectory));
        var factory = new MinecraftWorkspaceLiveSourceSessionFactory(
            PluginRegistry.PluginRegistry.Instance,
            new EncodingOptions { Encoding = Utf8 },
            maximumLineLength: 512);
        var clock = new IncrementingTimeProvider(new DateTimeOffset(2026, 9, 25, 10, 0, 0, TimeSpan.Zero));
        using var coordinator = new MinecraftWorkspaceLiveCoordinator(discovery, factory, clock);
        coordinator.Reconcile();

        await WaitUntil(() => coordinator.PendingCount >= 2, "Initial records from both physical sources were not ingressed").ConfigureAwait(false);
        IReadOnlyList<MinecraftWorkspaceIngressEvent> initial = coordinator.DrainPendingEvents();
        string workspaceId = initial[0].WorkspaceId;
        var timeline = new MinecraftWorkspaceTimeline(workspaceId);
        timeline.AppendBatch(initial);

        await File.AppendAllTextAsync(alphaPath, alphaLate, Utf8).ConfigureAwait(false);
        await WaitUntil(() => coordinator.PendingCount >= 1, "The later CFM record was not ingressed").ConfigureAwait(false);
        IReadOnlyList<MinecraftWorkspaceIngressEvent> laterBatch = coordinator.DrainPendingEvents();
        timeline.AppendBatch(laterBatch);

        IReadOnlyList<MinecraftWorkspaceTimelineEntry> snapshot = timeline.GetOrderedSnapshot();
        MinecraftWorkspaceTimelineEntry late = snapshot.Single(entry => entry.IngressEvent.Event.RawText.Contains("\"sequence\":2", StringComparison.Ordinal));
        DiscoveredSourceFile alphaSource = discovery.Rescan().Files.Single(source => source.FullPath == alphaPath);
        DiscoveredSourceFile betaSource = discovery.Rescan().Files.Single(source => source.FullPath == betaPath);

        Assert.Multiple(() =>
        {
            Assert.That(initial, Has.Count.EqualTo(2));
            Assert.That(initial.All(item => item.IngestedAtUtc.Offset == TimeSpan.Zero), Is.True);
            Assert.That(initial.OrderBy(item => item.IngressSequence).Select(item => item.IngestedAtUtc), Is.Ordered);
            Assert.That(initial.Select(item => item.FileId), Is.EquivalentTo(new[] { alphaSource.FileId, betaSource.FileId }));
            Assert.That(laterBatch, Has.Count.EqualTo(1));
            Assert.That(late.IsLate, Is.True);
            Assert.That(late.WasTimestampAdjustedForSourceOrder, Is.True);
            Assert.That(late.CandidateTimestampUtc, Is.LessThan(late.EffectiveTimestampUtc));
            Assert.That(snapshot.Select(entry => entry.IngressEvent.Event.RawText).ToArray(), Is.EqualTo(new[]
            {
                alphaInitial.TrimEnd('\n'), alphaLate.TrimEnd('\n'), betaInitial.TrimEnd('\n')
            }));
            Assert.That(snapshot.Select(entry => entry.Identity).Distinct().Count(), Is.EqualTo(3));
            Assert.That(timeline.Count, Is.EqualTo(3));
            Assert.That(late.IngressEvent.FileId, Is.EqualTo(alphaSource.FileId));
        });
    }

    [Test]
    public async Task Concurrent_appends_and_snapshots_keep_every_ingress_identity ()
    {
        const int TrackCount = 4;
        const int EventsPerTrack = 20;
        var timeline = new MinecraftWorkspaceTimeline(WorkspaceId);
        Task[] producers = Enumerable.Range(0, TrackCount).Select(track => Task.Run(() =>
        {
            for (int sequence = 1; sequence <= EventsPerTrack; sequence++)
            {
                long ingressSequence = (track * EventsPerTrack) + sequence;
                var ingress = CreateIngress(
                    ingressSequence,
                    $"track-{track}-{sequence}",
                    sourceId: $"source-{track}",
                    fileId: $"file-{track}",
                    sourceSequence: sequence,
                    timestamp: SourceTime($"2026-09-25T10:{sequence:D2}:00Z"));
                timeline.AppendBatch([ingress]);
                _ = timeline.GetOrderedSnapshot();
            }
        })).ToArray();

        await Task.WhenAll(producers).ConfigureAwait(false);
        IReadOnlyList<MinecraftWorkspaceTimelineEntry> snapshot = timeline.GetOrderedSnapshot();

        Assert.Multiple(() =>
        {
            Assert.That(timeline.Count, Is.EqualTo(TrackCount * EventsPerTrack));
            Assert.That(snapshot, Has.Count.EqualTo(TrackCount * EventsPerTrack));
            Assert.That(snapshot.Select(entry => entry.Identity).Distinct().Count(), Is.EqualTo(TrackCount * EventsPerTrack));
        });
    }

    private string CreateFile (string relativePath, string content)
    {
        string fullPath = Path.Combine(_testDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
        _ = Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content, Utf8);
        return fullPath;
    }

    private static MinecraftWorkspaceIngressEvent CreateIngress (
        long ingressSequence,
        string eventName,
        string workspaceId = WorkspaceId,
        string sourceId = "source",
        string fileId = "file",
        long sourceSequence = 1,
        long generation = 1,
        long startOffset = 0,
        long? producerSequence = null,
        EventTimestamp? timestamp = null,
        DateTimeOffset? ingestedAtUtc = null)
    {
        var file = new FileRef(fileId, $"C:/workspace/{fileId}.log", generation);
        var eventRef = new EventRef(file, sourceSequence, startOffset, startOffset + 1, 1, 1);
        var parsed = new NormalizedLogEvent(
            eventRef,
            Attribution.Unknown<string>(),
            Attribution.Unknown<string>(),
            Attribution.Unknown<LogLevel>(),
            null,
            Attribution.Unknown<string>(),
            timestamp ?? EventTimestamp.Unknown(),
            EventParseStatus.Parsed,
            eventName,
            eventName,
            producerSequence);
        return Wrap(parsed, ingressSequence, sourceId, fileId, ingestedAtUtc ?? new DateTimeOffset(2026, 9, 25, 10, 0, 0, TimeSpan.Zero), workspaceId);
    }

    private static MinecraftWorkspaceIngressEvent Wrap (
        NormalizedLogEvent parsed,
        long ingressSequence,
        string sourceId,
        string fileId,
        DateTimeOffset ingestedAtUtc,
        string workspaceId = WorkspaceId) =>
        new(
            ingressSequence,
            workspaceId,
            sourceId,
            fileId,
            MinecraftSourceSegmentRole.Primary,
            parsed,
            ingestedAtUtc);

    private static EventTimestamp SourceTime (string value) =>
        EventTimestamp.FromSource(DateTimeOffset.Parse(value, System.Globalization.CultureInfo.InvariantCulture), value);

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

    private sealed class IncrementingTimeProvider (DateTimeOffset baseUtc) : TimeProvider
    {
        private long _tick;

        public override DateTimeOffset GetUtcNow () => baseUtc.AddTicks(Interlocked.Increment(ref _tick));
    }
}
