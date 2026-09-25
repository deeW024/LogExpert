using System.Text;
using System.Diagnostics.CodeAnalysis;

using LogExpert.Core.Classes.Log;
using LogExpert.Core.Classes.MinecraftLogs;
using LogExpert.Core.Entities;

using NUnit.Framework;

namespace LogExpert.Tests;

[TestFixture]
[SuppressMessage("Globalization", "CA1303:Do not pass literals as localized parameters", Justification = "The fixture uses synthetic exact values to exercise filter contracts.")]
internal sealed class MinecraftWorkspaceTimelineFilterTests
{
    private const string WorkspaceId = "filter-workspace";
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private static readonly DateTimeOffset FallbackUtc = new(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);
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
    public void Empty_query_returns_same_entries_and_timeline_order ()
    {
        IReadOnlyList<MinecraftWorkspaceTimelineEntry> snapshot = CreateSnapshot(
            CreateIngress(1, "file-one", "one", timestamp: At("2026-09-25T10:02:00Z")),
            CreateIngress(2, "file-two", "two", timestamp: At("2026-09-25T10:01:00Z")),
            CreateIngress(3, "file-three", "three", timestamp: At("2026-09-25T10:03:00Z")));

        MinecraftWorkspaceTimelineFilterResult result = Filter().Evaluate(snapshot, new MinecraftWorkspaceTimelineFilterQuery());

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(MinecraftWorkspaceFilterStatus.Success));
            Assert.That(result.TotalLoadedCount, Is.EqualTo(3));
            Assert.That(result.MatchedCount, Is.EqualTo(3));
            Assert.That(result.MatchedEntries.Select(entry => entry.Identity), Is.EqualTo(snapshot.Select(entry => entry.Identity)));
            for (int index = 0; index < snapshot.Count; index++)
            {
                Assert.That(result.MatchedEntries[index], Is.SameAs(snapshot[index]));
            }
        });
    }

    [Test]
    public void Default_plain_message_search_is_case_insensitive ()
    {
        IReadOnlyList<MinecraftWorkspaceTimelineEntry> snapshot = CreateSnapshot(
            CreateIngress(1, "file-a", "Synthetic WARNING"),
            CreateIngress(2, "file-b", "other"));

        MinecraftWorkspaceTimelineFilterResult result = Filter().Evaluate(
            snapshot,
            new MinecraftWorkspaceTimelineFilterQuery(searchText: "warning"));

        Assert.That(result.MatchedEntries.Select(Message), Is.EqualTo(new[] { "Synthetic WARNING" }));
    }

    [Test]
    public void Case_sensitive_plain_search_uses_ordinal_matching ()
    {
        IReadOnlyList<MinecraftWorkspaceTimelineEntry> snapshot = CreateSnapshot(
            CreateIngress(1, "file-a", "Synthetic WARNING"),
            CreateIngress(2, "file-b", "synthetic warning"));

        MinecraftWorkspaceTimelineFilterResult result = Filter().Evaluate(
            snapshot,
            new MinecraftWorkspaceTimelineFilterQuery(searchText: "warning", isCaseSensitive: true));

        Assert.That(result.MatchedEntries.Select(Message), Is.EqualTo(new[] { "synthetic warning" }));
    }

    [Test]
    public void Invert_applies_only_to_the_text_predicate ()
    {
        IReadOnlyList<MinecraftWorkspaceTimelineEntry> snapshot = CreateSnapshot(
            CreateIngress(1, "file-a", "needle", source: Known("Yeezus")),
            CreateIngress(2, "file-b", "other", source: Known("Yeezus")),
            CreateIngress(3, "file-c", "other", source: Known("Minecraft")));

        MinecraftWorkspaceTimelineFilterResult result = Filter().Evaluate(
            snapshot,
            new MinecraftWorkspaceTimelineFilterQuery(
                searchText: "needle",
                isInvert: true,
                sources: [Facet<string>("Yeezus")]));

        Assert.That(result.MatchedEntries.Select(Message), Is.EqualTo(new[] { "other" }));
    }

    [Test]
    public void Raw_text_scope_finds_stack_content_not_in_message ()
    {
        IReadOnlyList<MinecraftWorkspaceTimelineEntry> snapshot = CreateSnapshot(
            CreateIngress(1, "file-a", "Synthetic failure", "Synthetic failure\n at example.Stack.run(Stack.java:1)"));

        MinecraftWorkspaceTimelineFilterResult result = Filter().Evaluate(
            snapshot,
            new MinecraftWorkspaceTimelineFilterQuery(
                searchText: "Stack.run",
                textScope: MinecraftWorkspaceTextScope.RawText));

        Assert.That(result.MatchedEntries, Has.Count.EqualTo(1));
        Assert.That(result.MatchedEntries[0].IngressEvent.Event.Message, Is.EqualTo("Synthetic failure"));
    }

    [Test]
    public void Message_or_raw_text_checks_fields_independently_without_concatenating_them ()
    {
        IReadOnlyList<MinecraftWorkspaceTimelineEntry> snapshot = CreateSnapshot(
            CreateIngress(1, "file-message", "message-target", "raw"),
            CreateIngress(2, "file-raw", "message", "raw-target"),
            CreateIngress(3, "file-cross", "left", "right"));
        var filter = Filter();

        MinecraftWorkspaceTimelineFilterResult messageHit = filter.Evaluate(snapshot, new MinecraftWorkspaceTimelineFilterQuery(
            searchText: "message-target",
            textScope: MinecraftWorkspaceTextScope.MessageOrRawText));
        MinecraftWorkspaceTimelineFilterResult rawHit = filter.Evaluate(snapshot, new MinecraftWorkspaceTimelineFilterQuery(
            searchText: "raw-target",
            textScope: MinecraftWorkspaceTextScope.MessageOrRawText));
        MinecraftWorkspaceTimelineFilterResult crossFieldMiss = filter.Evaluate(snapshot, new MinecraftWorkspaceTimelineFilterQuery(
            searchText: "left\nright",
            textScope: MinecraftWorkspaceTextScope.MessageOrRawText));

        Assert.Multiple(() =>
        {
            Assert.That(messageHit.MatchedEntries.Select(Message), Is.EqualTo(new[] { "message-target" }));
            Assert.That(rawHit.MatchedEntries.Select(Message), Is.EqualTo(new[] { "message" }));
            Assert.That(crossFieldMiss.MatchedCount, Is.EqualTo(0));
        });
    }

    [Test]
    public void Valid_regex_is_evaluated_with_safe_regex_semantics ()
    {
        IReadOnlyList<MinecraftWorkspaceTimelineEntry> snapshot = CreateSnapshot(
            CreateIngress(1, "file-a", "WARN: one"),
            CreateIngress(2, "file-b", "info: two"));

        MinecraftWorkspaceTimelineFilterResult result = Filter().Evaluate(snapshot, new MinecraftWorkspaceTimelineFilterQuery(
            searchText: "^warn:", isRegex: true));

        Assert.That(result.MatchedEntries.Select(Message), Is.EqualTo(new[] { "WARN: one" }));
    }

    [Test]
    public void Invalid_regex_returns_explicit_status_without_a_partial_result ()
    {
        IReadOnlyList<MinecraftWorkspaceTimelineEntry> snapshot = CreateSnapshot(CreateIngress(1, "file-a", "some text"));

        MinecraftWorkspaceTimelineFilterResult result = Filter().Evaluate(snapshot, new MinecraftWorkspaceTimelineFilterQuery(
            searchText: "[", isRegex: true));

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(MinecraftWorkspaceFilterStatus.InvalidRegex));
            Assert.That(result.TotalLoadedCount, Is.EqualTo(1));
            Assert.That(result.MatchedCount, Is.Null);
            Assert.That(result.MatchedEntries, Is.Empty);
            Assert.That(result.ErrorDetail, Is.Not.Empty);
            Assert.That(result.FacetBuckets.Sources.All(bucket => bucket.MatchingCount is null), Is.True);
        });
    }

    [Test]
    public void Catastrophic_regex_timeout_returns_no_partial_success_view ()
    {
        IReadOnlyList<MinecraftWorkspaceTimelineEntry> snapshot = CreateSnapshot(
            CreateIngress(1, "file-a", string.Concat(new string('a', 40_000), "!")));
        var filter = new MinecraftWorkspaceTimelineFilter(TimeSpan.FromMilliseconds(1));

        MinecraftWorkspaceTimelineFilterResult result = filter.Evaluate(snapshot, new MinecraftWorkspaceTimelineFilterQuery(
            searchText: "^(a|aa)+$", isRegex: true));

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(MinecraftWorkspaceFilterStatus.RegexTimedOut));
            Assert.That(result.TotalLoadedCount, Is.EqualTo(1));
            Assert.That(result.MatchedCount, Is.Null);
            Assert.That(result.MatchedEntries, Is.Empty);
            Assert.That(result.ErrorDetail, Is.Not.Empty);
            Assert.That(snapshot[0].IngressEvent.Event.Message.Length, Is.EqualTo(40_001));
        });
    }

    [Test]
    public void Known_source_selection_is_exact_ordinal ()
    {
        IReadOnlyList<MinecraftWorkspaceTimelineEntry> snapshot = CreateSnapshot(
            CreateIngress(1, "file-a", "upper", source: Known("Yeezus")),
            CreateIngress(2, "file-b", "lower", source: Known("yeezus")));

        MinecraftWorkspaceTimelineFilterResult result = Filter().Evaluate(snapshot, new MinecraftWorkspaceTimelineFilterQuery(
            sources: [Facet<string>("Yeezus")]));

        Assert.That(result.MatchedEntries.Select(Message), Is.EqualTo(new[] { "upper" }));
    }

    [Test]
    public void Source_unknown_selection_is_distinct_from_known_string_Unknown ()
    {
        IReadOnlyList<MinecraftWorkspaceTimelineEntry> snapshot = CreateSnapshot(
            CreateIngress(1, "file-unknown", "typed unknown"),
            CreateIngress(2, "file-known", "literal", source: Known("Unknown")),
            CreateIngress(3, "file-other", "other", source: Known("Yeezus")));

        MinecraftWorkspaceTimelineFilterResult unknownResult = Filter().Evaluate(snapshot, new MinecraftWorkspaceTimelineFilterQuery(
            sources: [MinecraftWorkspaceFacetValues.Unknown<string>()]));
        MinecraftWorkspaceFacetBucket<MinecraftWorkspaceFacetValue<string>> unknownBucket = GetUnknown(unknownResult.FacetBuckets.Sources);

        Assert.Multiple(() =>
        {
            Assert.That(unknownResult.MatchedEntries.Select(Message), Is.EqualTo(new[] { "typed unknown" }));
            Assert.That(unknownBucket.TotalCount, Is.EqualTo(1));
            Assert.That(unknownResult.FacetBuckets.Sources.Single(bucket => !bucket.Value.IsUnknown && bucket.Value.Value == "Unknown").TotalCount, Is.EqualTo(1));
        });
    }

    [Test]
    public void Unknown_component_level_and_thread_each_remain_selectable ()
    {
        IReadOnlyList<MinecraftWorkspaceTimelineEntry> snapshot = CreateSnapshot(
            CreateIngress(1, "file-known", "known", component: Known("Core"), level: Known(LogLevel.Info), thread: Known("Render")),
            CreateIngress(2, "file-unknown", "unknown"));
        var filter = Filter();

        MinecraftWorkspaceTimelineFilterResult component = filter.Evaluate(snapshot, new MinecraftWorkspaceTimelineFilterQuery(
            components: [MinecraftWorkspaceFacetValues.Unknown<string>()]));
        MinecraftWorkspaceTimelineFilterResult level = filter.Evaluate(snapshot, new MinecraftWorkspaceTimelineFilterQuery(
            levels: [MinecraftWorkspaceFacetValues.Unknown<LogLevel>()]));
        MinecraftWorkspaceTimelineFilterResult thread = filter.Evaluate(snapshot, new MinecraftWorkspaceTimelineFilterQuery(
            threads: [MinecraftWorkspaceFacetValues.Unknown<string>()]));

        Assert.Multiple(() =>
        {
            Assert.That(component.MatchedEntries.Select(Message), Is.EqualTo(new[] { "unknown" }));
            Assert.That(level.MatchedEntries.Select(Message), Is.EqualTo(new[] { "unknown" }));
            Assert.That(thread.MatchedEntries.Select(Message), Is.EqualTo(new[] { "unknown" }));
        });
    }

    [Test]
    public void Several_source_values_in_one_facet_combine_with_or ()
    {
        IReadOnlyList<MinecraftWorkspaceTimelineEntry> snapshot = CreateSnapshot(
            CreateIngress(1, "file-a", "yeezus", source: Known("Yeezus")),
            CreateIngress(2, "file-b", "minecraft", source: Known("Minecraft")),
            CreateIngress(3, "file-c", "other", source: Known("Other")));

        MinecraftWorkspaceTimelineFilterResult result = Filter().Evaluate(snapshot, new MinecraftWorkspaceTimelineFilterQuery(
            sources: [Facet<string>("Yeezus"), Facet<string>("Minecraft")]));

        Assert.That(result.MatchedEntries.Select(Message), Is.EqualTo(new[] { "yeezus", "minecraft" }));
    }

    [Test]
    public void File_source_component_level_and_thread_facets_combine_with_and ()
    {
        IReadOnlyList<MinecraftWorkspaceTimelineEntry> snapshot = CreateSnapshot(
            CreateIngress(1, "target-file", "target", source: Known("Yeezus"), component: Known("Core"), level: Known(LogLevel.Warn), thread: Known("Client")),
            CreateIngress(2, "other-file", "wrong file", source: Known("Yeezus"), component: Known("Core"), level: Known(LogLevel.Warn), thread: Known("Client")),
            CreateIngress(3, "target-file-other-source", "wrong source", source: Known("Other"), component: Known("Core"), level: Known(LogLevel.Warn), thread: Known("Client")),
            CreateIngress(4, "target-file-other-component", "wrong component", source: Known("Yeezus"), component: Known("Other"), level: Known(LogLevel.Warn), thread: Known("Client")),
            CreateIngress(5, "target-file-other-level", "wrong level", source: Known("Yeezus"), component: Known("Core"), level: Known(LogLevel.Error), thread: Known("Client")),
            CreateIngress(6, "target-file-other-thread", "wrong thread", source: Known("Yeezus"), component: Known("Core"), level: Known(LogLevel.Warn), thread: Known("Render")));

        MinecraftWorkspaceTimelineFilterResult result = Filter().Evaluate(snapshot, new MinecraftWorkspaceTimelineFilterQuery(
            fileIds: ["target-file"],
            sources: [Facet<string>("Yeezus")],
            components: [Facet<string>("Core")],
            levels: [Facet<LogLevel>(LogLevel.Warn)],
            threads: [Facet<string>("Client")]));

        Assert.That(result.MatchedEntries.Select(Message), Is.EqualTo(new[] { "target" }));
    }

    [Test]
    public void Empty_facet_selections_are_unconstrained ()
    {
        IReadOnlyList<MinecraftWorkspaceTimelineEntry> snapshot = CreateSnapshot(
            CreateIngress(1, "file-a", "one", source: Known("Yeezus")),
            CreateIngress(2, "file-b", "two", source: Known("Other")));

        MinecraftWorkspaceTimelineFilterResult result = Filter().Evaluate(snapshot, new MinecraftWorkspaceTimelineFilterQuery(
            fileIds: [], sources: [], components: [], levels: [], threads: []));

        Assert.That(result.MatchedCount, Is.EqualTo(2));
    }

    [Test]
    public void Filtering_preserves_relative_timeline_order_including_late_entries ()
    {
        var timeline = new MinecraftWorkspaceTimeline(WorkspaceId);
        timeline.AppendBatch(
        [
            CreateIngress(1, "file-early", "keep first", timestamp: At("2026-09-25T10:00:00Z")),
            CreateIngress(2, "file-late", "keep last", timestamp: At("2026-09-25T12:00:00Z"))
        ]);
        timeline.AppendBatch([CreateIngress(3, "file-middle", "keep late", timestamp: At("2026-09-25T11:00:00Z"))]);
        IReadOnlyList<MinecraftWorkspaceTimelineEntry> snapshot = timeline.GetOrderedSnapshot();

        MinecraftWorkspaceTimelineFilterResult result = Filter().Evaluate(snapshot, new MinecraftWorkspaceTimelineFilterQuery(
            searchText: "keep"));

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Select(entry => entry.Identity), Is.EqualTo(new long[] { 1, 3, 2 }));
            Assert.That(snapshot[1].IsLate, Is.True);
            Assert.That(result.MatchedEntries.Select(entry => entry.Identity), Is.EqualTo(new long[] { 1, 3, 2 }));
        });
    }

    [Test]
    public void Matched_entries_preserve_timeline_and_normalized_event_provenance_objects ()
    {
        MinecraftWorkspaceIngressEvent ingress = CreateIngress(
            1,
            "file-a",
            "message",
            "raw content",
            source: Known("Yeezus"),
            component: Known("Core"),
            level: Known(LogLevel.Error),
            thread: Known("Client"),
            timestamp: At("2026-09-25T10:00:00Z"),
            producerSequence: 7);
        MinecraftWorkspaceTimelineEntry entry = CreateSnapshot(ingress).Single();
        NormalizedLogEvent originalEvent = ingress.Event;
        EventRef originalEventRef = originalEvent.Ref;
        FileRef originalFileRef = originalEventRef.File;
        EventTimestamp originalTimestamp = originalEvent.Timestamp;
        AttributedValue<string> originalSource = originalEvent.Source;
        AttributedValue<string> originalComponent = originalEvent.Component;
        AttributedValue<LogLevel> originalLevel = originalEvent.Level;
        AttributedValue<string> originalThread = originalEvent.Thread;

        MinecraftWorkspaceTimelineFilterResult result = Filter().Evaluate([entry], new MinecraftWorkspaceTimelineFilterQuery());
        MinecraftWorkspaceTimelineEntry matched = result.MatchedEntries.Single();

        Assert.Multiple(() =>
        {
            Assert.That(matched, Is.SameAs(entry));
            Assert.That(matched.IngressEvent, Is.SameAs(ingress));
            Assert.That(matched.IngressEvent.Event, Is.SameAs(originalEvent));
            Assert.That(matched.IngressEvent.Event.Ref, Is.SameAs(originalEventRef));
            Assert.That(matched.IngressEvent.Event.Ref.File, Is.SameAs(originalFileRef));
            Assert.That(matched.IngressEvent.Event.Timestamp, Is.SameAs(originalTimestamp));
            Assert.That(matched.IngressEvent.Event.Source, Is.SameAs(originalSource));
            Assert.That(matched.IngressEvent.Event.Component, Is.SameAs(originalComponent));
            Assert.That(matched.IngressEvent.Event.Level, Is.SameAs(originalLevel));
            Assert.That(matched.IngressEvent.Event.Thread, Is.SameAs(originalThread));
            Assert.That(matched.Identity, Is.EqualTo(1));
            Assert.That(matched.IsLate, Is.False);
            Assert.That(matched.CandidateTimestampUtc, Is.EqualTo(entry.CandidateTimestampUtc));
            Assert.That(matched.EffectiveTimestampUtc, Is.EqualTo(entry.EffectiveTimestampUtc));
            Assert.That(matched.TimestampBasis, Is.EqualTo(entry.TimestampBasis));
        });
    }

    [Test]
    public void Total_counts_cover_all_loaded_entries_independent_of_query ()
    {
        IReadOnlyList<MinecraftWorkspaceTimelineEntry> snapshot = CreateSnapshot(
            CreateIngress(1, "file-a", "hit", source: Known("Yeezus")),
            CreateIngress(2, "file-b", "miss", source: Known("Yeezus")),
            CreateIngress(3, "file-c", "miss", source: Known("Other")));

        MinecraftWorkspaceTimelineFilterResult result = Filter().Evaluate(snapshot, new MinecraftWorkspaceTimelineFilterQuery(
            searchText: "not present"));

        Assert.Multiple(() =>
        {
            Assert.That(result.TotalLoadedCount, Is.EqualTo(3));
            Assert.That(result.MatchedCount, Is.EqualTo(0));
            Assert.That(GetKnown(result.FacetBuckets.Sources, "Yeezus").TotalCount, Is.EqualTo(2));
            Assert.That(GetKnown(result.FacetBuckets.Sources, "Other").TotalCount, Is.EqualTo(1));
        });
    }

    [Test]
    public void Matching_counts_ignore_their_own_facet_and_respect_other_constraints ()
    {
        IReadOnlyList<MinecraftWorkspaceTimelineEntry> snapshot = CreateSnapshot(
            CreateIngress(1, "file-a", "target", source: Known("Yeezus"), component: Known("Core"), level: Known(LogLevel.Warn), thread: Known("Main")),
            CreateIngress(2, "file-b", "target", source: Known("Yeezus"), component: Known("Core"), level: Known(LogLevel.Warn), thread: Known("Main")),
            CreateIngress(3, "file-a", "target", source: Known("Yeezus"), component: Known("Other"), level: Known(LogLevel.Warn), thread: Known("Main")),
            CreateIngress(4, "file-d", "target", source: Known("Other"), component: Known("Core"), level: Known(LogLevel.Warn), thread: Known("Main")),
            CreateIngress(5, "file-d", "target", source: Known("Yeezus"), component: Known("Core"), level: Known(LogLevel.Error), thread: Known("Main")),
            CreateIngress(6, "file-d", "target", source: Known("Yeezus"), component: Known("Core"), level: Known(LogLevel.Warn), thread: Known("Worker")),
            CreateIngress(7, "file-a", "no matching text", source: Known("Yeezus"), component: Known("Core"), level: Known(LogLevel.Warn), thread: Known("Main")));
        MinecraftWorkspaceTimelineFilterResult result = Filter().Evaluate(snapshot, new MinecraftWorkspaceTimelineFilterQuery(
            searchText: "target",
            fileIds: ["file-a", "file-d"],
            sources: [Facet<string>("Yeezus")],
            components: [Facet<string>("Core")],
            levels: [Facet<LogLevel>(LogLevel.Warn)],
            threads: [Facet<string>("Main")]));

        Assert.Multiple(() =>
        {
            Assert.That(result.MatchedEntries.Select(Message), Is.EqualTo(new[] { "target" }));
            Assert.That(GetKnown(result.FacetBuckets.FileIds, "file-b").MatchingCount, Is.EqualTo(1));
            Assert.That(GetKnown(result.FacetBuckets.Sources, "Other").MatchingCount, Is.EqualTo(1));
            Assert.That(GetKnown(result.FacetBuckets.Components, "Other").MatchingCount, Is.EqualTo(1));
            Assert.That(GetKnown(result.FacetBuckets.Levels, LogLevel.Error).MatchingCount, Is.EqualTo(1));
            Assert.That(GetKnown(result.FacetBuckets.Threads, "Worker").MatchingCount, Is.EqualTo(1));
        });
    }

    [Test]
    public void Unknown_bucket_counts_are_typed_and_correct ()
    {
        IReadOnlyList<MinecraftWorkspaceTimelineEntry> snapshot = CreateSnapshot(
            CreateIngress(1, "file-a", "known source", source: Known("Yeezus"), component: Known("Core"), level: Known(LogLevel.Info), thread: Known("Main")),
            CreateIngress(2, "file-b", "unknown one"),
            CreateIngress(3, "file-c", "unknown two"));

        MinecraftWorkspaceTimelineFilterResult result = Filter().Evaluate(snapshot, new MinecraftWorkspaceTimelineFilterQuery(
            searchText: "unknown"));

        Assert.Multiple(() =>
        {
            Assert.That(GetUnknown(result.FacetBuckets.Sources).TotalCount, Is.EqualTo(2));
            Assert.That(GetUnknown(result.FacetBuckets.Sources).MatchingCount, Is.EqualTo(2));
            Assert.That(GetUnknown(result.FacetBuckets.Components).TotalCount, Is.EqualTo(2));
            Assert.That(GetUnknown(result.FacetBuckets.Levels).TotalCount, Is.EqualTo(2));
            Assert.That(GetUnknown(result.FacetBuckets.Threads).TotalCount, Is.EqualTo(2));
        });
    }

    [Test]
    public void Facet_buckets_follow_deterministic_contract_order_with_unknown_last ()
    {
        IReadOnlyList<MinecraftWorkspaceTimelineEntry> snapshot = CreateSnapshot(
            CreateIngress(1, "z-file", "z", source: Known("Beta"), component: Known("Beta"), level: Known(LogLevel.Fatal), thread: Known("Beta")),
            CreateIngress(2, "a-file", "a", source: Known("alpha"), component: Known("alpha"), level: Known(LogLevel.Trace), thread: Known("alpha")),
            CreateIngress(3, "A-file", "A", source: Known("ALPHA"), component: Known("ALPHA"), level: Known(LogLevel.Info), thread: Known("ALPHA")),
            CreateIngress(4, "m-file", "m", source: Known("Middle"), component: Known("Middle"), level: Known(LogLevel.Warn), thread: Known("Middle")),
            CreateIngress(5, "unknown-file", "unknown"));

        MinecraftWorkspaceTimelineFacetBuckets buckets = Filter().Evaluate(snapshot, new MinecraftWorkspaceTimelineFilterQuery()).FacetBuckets;

        Assert.Multiple(() =>
        {
            Assert.That(buckets.FileIds.Select(bucket => bucket.Value), Is.EqualTo(new[] { "A-file", "a-file", "m-file", "unknown-file", "z-file" }));
            Assert.That(buckets.Sources.Select(Display), Is.EqualTo(new[] { "ALPHA", "alpha", "Beta", "Middle", "Unknown" }));
            Assert.That(buckets.Components.Select(Display), Is.EqualTo(new[] { "ALPHA", "alpha", "Beta", "Middle", "Unknown" }));
            Assert.That(buckets.Threads.Select(Display), Is.EqualTo(new[] { "ALPHA", "alpha", "Beta", "Middle", "Unknown" }));
            Assert.That(buckets.Levels.Select(Display), Is.EqualTo(new[] { "Trace", "Info", "Warn", "Fatal", "Unknown" }));
        });
    }

    [Test]
    public void Repeated_evaluation_is_deterministic_and_returns_read_only_snapshots ()
    {
        IReadOnlyList<MinecraftWorkspaceTimelineEntry> snapshot = CreateSnapshot(
            CreateIngress(1, "file-z", "target", source: Known("Yeezus")),
            CreateIngress(2, "file-a", "target", source: Known("Other")));
        var query = new MinecraftWorkspaceTimelineFilterQuery(searchText: "target");
        var filter = Filter();

        MinecraftWorkspaceTimelineFilterResult first = filter.Evaluate(snapshot, query);
        MinecraftWorkspaceTimelineFilterResult second = filter.Evaluate(snapshot, query);

        Assert.Multiple(() =>
        {
            Assert.That(second.MatchedEntries.Select(entry => entry.Identity), Is.EqualTo(first.MatchedEntries.Select(entry => entry.Identity)));
            Assert.That(second.FacetBuckets.Sources.Select(bucket => (Display(bucket), bucket.TotalCount, bucket.MatchingCount)),
                Is.EqualTo(first.FacetBuckets.Sources.Select(bucket => (Display(bucket), bucket.TotalCount, bucket.MatchingCount))));
            Assert.That(first.QuerySnapshot, Is.SameAs(query));
            Assert.That(((IList<MinecraftWorkspaceTimelineEntry>)first.MatchedEntries).IsReadOnly, Is.True);
            Assert.That(((IList<MinecraftWorkspaceFacetBucket<MinecraftWorkspaceFacetValue<string>>>)first.FacetBuckets.Sources).IsReadOnly, Is.True);
        });
    }

    [Test]
    public void Query_selections_are_defensive_read_only_snapshots ()
    {
        var selectedSources = new List<MinecraftWorkspaceFacetValue<string>> { Facet<string>("Yeezus") };
        var query = new MinecraftWorkspaceTimelineFilterQuery(sources: selectedSources);
        selectedSources.Clear();

        Assert.Multiple(() =>
        {
            Assert.That(query.Sources, Has.Count.EqualTo(1));
            Assert.That(query.Sources[0].Value, Is.EqualTo("Yeezus"));
            Assert.That(((IList<MinecraftWorkspaceFacetValue<string>>)query.Sources).IsReadOnly, Is.True);
        });
    }

    [Test]
    public void New_snapshot_inserts_late_match_without_changing_surviving_identities ()
    {
        var timeline = new MinecraftWorkspaceTimeline(WorkspaceId);
        timeline.AppendBatch(
        [
            CreateIngress(1, "file-first", "hit first", timestamp: At("2026-09-25T10:00:00Z")),
            CreateIngress(2, "file-last", "hit last", timestamp: At("2026-09-25T12:00:00Z"))
        ]);
        IReadOnlyList<MinecraftWorkspaceTimelineEntry> before = timeline.GetOrderedSnapshot();
        long[] originalIdentities = before.Select(entry => entry.Identity).ToArray();
        MinecraftWorkspaceTimelineFilter filter = Filter();
        MinecraftWorkspaceTimelineFilterQuery query = new(searchText: "hit");
        MinecraftWorkspaceTimelineFilterResult firstView = filter.Evaluate(before, query);

        timeline.AppendBatch([CreateIngress(3, "file-late", "hit inserted late", timestamp: At("2026-09-25T11:00:00Z"))]);
        IReadOnlyList<MinecraftWorkspaceTimelineEntry> after = timeline.GetOrderedSnapshot();
        MinecraftWorkspaceTimelineFilterResult secondView = filter.Evaluate(after, query);

        Assert.Multiple(() =>
        {
            Assert.That(firstView.MatchedEntries.Select(entry => entry.Identity), Is.EqualTo(originalIdentities));
            Assert.That(secondView.MatchedEntries.Select(entry => entry.Identity), Is.EqualTo(new long[] { 1, 3, 2 }));
            Assert.That(secondView.MatchedEntries[1].IsLate, Is.True);
            Assert.That(before.Select(entry => entry.Identity), Is.EqualTo(originalIdentities));
            Assert.That(before.All(entry => !entry.IsLate), Is.True);
        });
    }

    [Test]
    public async Task Real_coordinator_timeline_filter_preserves_adapters_order_unknown_and_raw_provenance ()
    {
        DateTimeOffset cfmTime = DateTimeOffset.Parse("2026-09-25T10:01:00Z", System.Globalization.CultureInfo.InvariantCulture);
        string cfmRecord = $"{{\"type\":\"CYCLE\",\"sessionId\":\"synthetic-session\",\"sequence\":1,\"timestampEpochMillis\":{cfmTime.ToUnixTimeMilliseconds()}}}\n";
        string latestPath = CreateFile("logs/latest.log",
            "[18:41:03] [Render thread/ERROR] synthetic latest warning\n" +
            "[18:41:04] [Render thread/INFO] synthetic latest boundary\n");
        string yeezusPath = CreateFile("logs/yeezus.log",
            "2026-09-25T10:02:00Z [INFO] [yeezus-core] [Client thread] synthetic ready\n" +
            "    at synthetic.Stack.run(Stack.java:1)\n" +
            "2026-09-25T10:03:00Z [INFO] [yeezus-core] [Client thread] synthetic boundary\n");
        _ = CreateFile("cactusmonitor/sessions/cfm-synthetic-session.jsonl.part", cfmRecord);

        var workspace = new MinecraftWorkspace(_testDirectory);
        var discovery = new MinecraftSourceDiscovery(workspace);
        var factory = new MinecraftWorkspaceLiveSourceSessionFactory(
            PluginRegistry.PluginRegistry.Instance,
            new EncodingOptions { Encoding = Utf8 },
            maximumLineLength: 2048);
        using var coordinator = new MinecraftWorkspaceLiveCoordinator(discovery, factory, new FixedTimeProvider(FallbackUtc));
        coordinator.Reconcile();
        var ingress = new List<MinecraftWorkspaceIngressEvent>();
        await WaitUntil(() =>
        {
            ingress.AddRange(coordinator.DrainPendingEvents());
            return ingress.Any(item => item.Event.RawText.Contains("synthetic ready", StringComparison.Ordinal)) &&
                ingress.Any(item => item.Event.RawText.Contains("synthetic-session", StringComparison.Ordinal)) &&
                ingress.Any(item => item.Event.RawText.Contains("synthetic latest warning", StringComparison.Ordinal));
        }, "Initial events from the three adapter types were not ingressed").ConfigureAwait(false);

        var timeline = new MinecraftWorkspaceTimeline(workspace.WorkspaceId);
        timeline.AppendBatch(ingress);
        IReadOnlyList<MinecraftWorkspaceTimelineEntry> snapshot = timeline.GetOrderedSnapshot();
        var filter = Filter();
        MinecraftWorkspaceTimelineFilterResult all = filter.Evaluate(snapshot, new MinecraftWorkspaceTimelineFilterQuery());
        MinecraftWorkspaceTimelineFilterResult sourceRows = filter.Evaluate(snapshot, new MinecraftWorkspaceTimelineFilterQuery(
            sources: [Facet<string>("Yeezus")]));
        MinecraftWorkspaceTimelineFilterResult rawStack = filter.Evaluate(snapshot, new MinecraftWorkspaceTimelineFilterQuery(
            searchText: "synthetic.Stack.run",
            textScope: MinecraftWorkspaceTextScope.RawText));
        MinecraftWorkspaceTimelineFilterResult unknownLevel = filter.Evaluate(snapshot, new MinecraftWorkspaceTimelineFilterQuery(
            sources: [Facet<string>("Yeezus")],
            levels: [MinecraftWorkspaceFacetValues.Unknown<LogLevel>()]));

        string yeezusFileId = discovery.Rescan().Files.Single(source => source.FullPath == yeezusPath).FileId;
        string latestFileId = discovery.Rescan().Files.Single(source => source.FullPath == latestPath).FileId;
        MinecraftWorkspaceTimelineEntry yeezusEntry = snapshot.Single(entry => entry.IngressEvent.FileId == yeezusFileId &&
            entry.IngressEvent.Event.RawText.Contains("synthetic ready", StringComparison.Ordinal));
        MinecraftWorkspaceTimelineEntry cfmEntry = snapshot.Single(entry => entry.IngressEvent.Event.Component.IsKnown &&
            entry.IngressEvent.Event.Component.Value == "CactusMonitor");
        MinecraftWorkspaceTimelineEntry latestEntry = snapshot.Single(entry => entry.IngressEvent.FileId == latestFileId &&
            entry.IngressEvent.Event.RawText.Contains("synthetic latest warning", StringComparison.Ordinal));
        MinecraftWorkspaceTimelineFilterResult unknownSource = filter.Evaluate(snapshot, new MinecraftWorkspaceTimelineFilterQuery(
            searchText: "synthetic latest warning",
            sources: [MinecraftWorkspaceFacetValues.Unknown<string>()]));

        Assert.Multiple(() =>
        {
            Assert.That(ingress.Count, Is.GreaterThanOrEqualTo(3));
            Assert.That(snapshot.Select(entry => entry.EffectiveTimestampUtc), Is.Ordered);
            Assert.That(sourceRows.MatchedEntries.Select(entry => entry.Identity), Is.EqualTo(snapshot
                .Where(entry => entry.IngressEvent.Event.Source.IsKnown && entry.IngressEvent.Event.Source.Value == "Yeezus")
                .Select(entry => entry.Identity)));
            Assert.That(sourceRows.MatchedEntries.Select(entry => entry.IngressEvent.Event.Message), Does.Contain("synthetic ready\n    at synthetic.Stack.run(Stack.java:1)"));
            Assert.That(all.FacetBuckets.Sources.Any(bucket => bucket.Value.IsUnknown), Is.True);
            Assert.That(all.FacetBuckets.Components.Any(bucket => bucket.Value.IsUnknown), Is.True);
            Assert.That(all.FacetBuckets.Levels.Any(bucket => bucket.Value.IsUnknown), Is.True);
            Assert.That(all.FacetBuckets.Threads.Any(bucket => bucket.Value.IsUnknown), Is.True);
            Assert.That(GetKnown(all.FacetBuckets.Sources, "Yeezus").TotalCount, Is.EqualTo(snapshot.Count(entry =>
                entry.IngressEvent.Event.Source.IsKnown && entry.IngressEvent.Event.Source.Value == "Yeezus")));
            Assert.That(GetKnown(all.FacetBuckets.Components, "Core").TotalCount, Is.EqualTo(snapshot.Count(entry =>
                entry.IngressEvent.Event.Component.IsKnown && entry.IngressEvent.Event.Component.Value == "Core")));
            Assert.That(GetKnown(all.FacetBuckets.Components, "CactusMonitor").TotalCount, Is.EqualTo(snapshot.Count(entry =>
                entry.IngressEvent.Event.Component.IsKnown && entry.IngressEvent.Event.Component.Value == "CactusMonitor")));
            Assert.That(GetKnown(all.FacetBuckets.Levels, LogLevel.Info).TotalCount, Is.EqualTo(snapshot.Count(entry =>
                entry.IngressEvent.Event.Level.IsKnown && entry.IngressEvent.Event.Level.Value == LogLevel.Info)));
            Assert.That(GetKnown(all.FacetBuckets.Threads, "Client thread").TotalCount, Is.EqualTo(snapshot.Count(entry =>
                entry.IngressEvent.Event.Thread.IsKnown && entry.IngressEvent.Event.Thread.Value == "Client thread")));
            Assert.That(unknownSource.MatchedEntries.Select(entry => entry.Identity), Is.EqualTo(new[] { latestEntry.Identity }));
            Assert.That(unknownLevel.MatchedEntries.Select(entry => entry.Identity), Is.EqualTo(new[] { cfmEntry.Identity }));
            Assert.That(rawStack.MatchedEntries, Has.Count.EqualTo(1));
            Assert.That(rawStack.MatchedEntries[0], Is.SameAs(yeezusEntry));
            Assert.That(rawStack.MatchedEntries[0].IngressEvent.Event.Ref, Is.SameAs(yeezusEntry.IngressEvent.Event.Ref));
            Assert.That(rawStack.MatchedEntries[0].IngressEvent.Event.Ref.File, Is.SameAs(yeezusEntry.IngressEvent.Event.Ref.File));
            Assert.That(yeezusEntry.IngressEvent.Event.Source.Value, Is.EqualTo("Yeezus"));
            Assert.That(yeezusEntry.IngressEvent.Event.Component.Value, Is.EqualTo("Core"));
            Assert.That(yeezusEntry.IngressEvent.Event.Level.Value, Is.EqualTo(LogLevel.Info));
            Assert.That(yeezusEntry.IngressEvent.Event.Thread.Value, Is.EqualTo("Client thread"));
            Assert.That(cfmEntry.IngressEvent.Event.Component.Value, Is.EqualTo("CactusMonitor"));
            Assert.That(latestEntry.IngressEvent.Event.Source.IsKnown, Is.False);
        });
    }

    private static MinecraftWorkspaceTimelineFilter Filter () => new();

    private static IReadOnlyList<MinecraftWorkspaceTimelineEntry> CreateSnapshot (params MinecraftWorkspaceIngressEvent[] ingress)
    {
        var timeline = new MinecraftWorkspaceTimeline(WorkspaceId);
        timeline.AppendBatch(ingress);
        return timeline.GetOrderedSnapshot();
    }

    private static MinecraftWorkspaceIngressEvent CreateIngress (
        long ingressSequence,
        string fileId,
        string message,
        string? rawText = null,
        AttributedValue<string>? source = null,
        AttributedValue<string>? component = null,
        AttributedValue<LogLevel>? level = null,
        AttributedValue<string>? thread = null,
        EventTimestamp? timestamp = null,
        DateTimeOffset? ingestedAtUtc = null,
        long? producerSequence = null,
        long generation = 1)
    {
        var file = new FileRef(fileId, $"C:/workspace/{fileId}.log", generation);
        var eventRef = new EventRef(file, ingressSequence, ingressSequence * 10, ingressSequence * 10 + 1, 1, 1);
        var parsed = new NormalizedLogEvent(
            eventRef,
            source ?? Attribution.Unknown<string>(),
            component ?? Attribution.Unknown<string>(),
            level ?? Attribution.Unknown<LogLevel>(),
            null,
            thread ?? Attribution.Unknown<string>(),
            timestamp ?? EventTimestamp.Unknown(),
            EventParseStatus.Parsed,
            message,
            rawText ?? message,
            producerSequence);
        return new MinecraftWorkspaceIngressEvent(
            ingressSequence,
            WorkspaceId,
            $"source:{fileId}",
            fileId,
            MinecraftSourceSegmentRole.Primary,
            parsed,
            ingestedAtUtc ?? FallbackUtc);
    }

    private string CreateFile (string relativePath, string content)
    {
        string fullPath = Path.Combine(_testDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
        _ = Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content, Utf8);
        return fullPath;
    }

    private static AttributedValue<string> Known (string value) =>
        Attribution.From(value, AttributionProvenance.Adapter, AttributionConfidence.High);

    private static AttributedValue<LogLevel> Known (LogLevel value) =>
        Attribution.From(value, AttributionProvenance.Parsed, AttributionConfidence.High);

    private static MinecraftWorkspaceFacetValue<T> Facet<T> (T value)
        where T : notnull => MinecraftWorkspaceFacetValues.Known(value);

    private static EventTimestamp At (string timestamp) =>
        EventTimestamp.FromSource(DateTimeOffset.Parse(timestamp, System.Globalization.CultureInfo.InvariantCulture), timestamp);

    private static string Message (MinecraftWorkspaceTimelineEntry entry) => entry.IngressEvent.Event.Message;

    private static MinecraftWorkspaceFacetBucket<MinecraftWorkspaceFacetValue<T>> GetUnknown<T> (
        IReadOnlyList<MinecraftWorkspaceFacetBucket<MinecraftWorkspaceFacetValue<T>>> buckets)
        where T : notnull => buckets.Single(bucket => bucket.Value.IsUnknown);

    private static MinecraftWorkspaceFacetBucket<MinecraftWorkspaceFacetValue<string>> GetKnown (
        IReadOnlyList<MinecraftWorkspaceFacetBucket<MinecraftWorkspaceFacetValue<string>>> buckets,
        string value) => buckets.Single(bucket => !bucket.Value.IsUnknown && bucket.Value.Value == value);

    private static MinecraftWorkspaceFacetBucket<string> GetKnown (
        IReadOnlyList<MinecraftWorkspaceFacetBucket<string>> buckets,
        string value) => buckets.Single(bucket => bucket.Value == value);

    private static MinecraftWorkspaceFacetBucket<MinecraftWorkspaceFacetValue<LogLevel>> GetKnown (
        IReadOnlyList<MinecraftWorkspaceFacetBucket<MinecraftWorkspaceFacetValue<LogLevel>>> buckets,
        LogLevel value) => buckets.Single(bucket => !bucket.Value.IsUnknown && bucket.Value.Value == value);

    private static string Display (MinecraftWorkspaceFacetBucket<MinecraftWorkspaceFacetValue<string>> bucket) =>
        bucket.Value.IsUnknown ? "Unknown" : bucket.Value.Value;

    private static string Display (MinecraftWorkspaceFacetBucket<MinecraftWorkspaceFacetValue<LogLevel>> bucket) =>
        bucket.Value.IsUnknown ? "Unknown" : bucket.Value.Value.ToString();

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

    private sealed class FixedTimeProvider (DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow () => utcNow;
    }
}
