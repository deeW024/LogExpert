#pragma warning disable CA1303 // Test fixtures intentionally use synthetic, assertion-visible strings.

using System.Runtime.Versioning;
using System.Text;

using LogExpert.Core.Classes.MinecraftLogs;
using LogExpert.Core.Config;
using LogExpert.Core.Entities;
using LogExpert.UI.Workspace;

using NUnit.Framework;

using WeifenLuo.WinFormsUI.Docking;

namespace LogExpert.UI.Tests.Workspace;

[TestFixture]
[Apartment(ApartmentState.STA)]
[NonParallelizable]
[SupportedOSPlatform("windows")]
public sealed class MinecraftWorkspaceReadOnlyViewTests
{
    private static readonly DateTimeOffset IngestedAt = DateTimeOffset.Parse(
        "2026-09-25T00:00:00Z",
        System.Globalization.CultureInfo.InvariantCulture);

    private string _testDirectory = null!;

    [SetUp]
    public void SetUp ()
    {
        _testDirectory = Path.Join(Path.GetTempPath(), "LogExpertUiWorkspaceTests", Guid.NewGuid().ToString("N"));
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
    public void Presenter_empty_result_preserves_status_and_counts_with_no_rows ()
    {
        MinecraftWorkspaceTimelineFilterResult result = FilterResult([]);

        MinecraftWorkspaceReadOnlyViewSnapshot snapshot = MinecraftWorkspaceReadOnlyViewPresenter.CreateSnapshot("Synthetic workspace", result);

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.WorkspaceDisplayName, Is.EqualTo("Synthetic workspace"));
            Assert.That(snapshot.QuerySnapshot, Is.SameAs(result.QuerySnapshot));
            Assert.That(snapshot.FilterStatus, Is.EqualTo(MinecraftWorkspaceFilterStatus.Success));
            Assert.That(snapshot.TotalLoadedCount, Is.Zero);
            Assert.That(snapshot.MatchedCount, Is.Zero);
            Assert.That(snapshot.Rows, Is.Empty);
            Assert.That(snapshot.Rows, Is.InstanceOf<System.Collections.ObjectModel.ReadOnlyCollection<MinecraftWorkspaceReadOnlyRow>>());
        });
    }

    [Test]
    public void Presenter_rows_keep_exact_entry_references_identity_and_filter_order ()
    {
        MinecraftWorkspaceIngressEvent later = CreateIngress(1, "late-file", 1, "later", Utc("2030-01-01T10:03:00Z"));
        MinecraftWorkspaceIngressEvent earlier = CreateIngress(2, "early-file", 1, "earlier", Utc("2030-01-01T10:02:00Z"));
        MinecraftWorkspaceTimelineFilterResult result = FilterResult([later, earlier]);
        MinecraftWorkspaceReadOnlyViewSnapshot snapshot = MinecraftWorkspaceReadOnlyViewPresenter.CreateSnapshot("Synthetic", result);

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Rows, Has.Count.EqualTo(result.MatchedEntries.Count));
            for (int index = 0; index < result.MatchedEntries.Count; index++)
            {
                Assert.That(snapshot.Rows[index].Entry, Is.SameAs(result.MatchedEntries[index]));
                Assert.That(snapshot.Rows[index].Identity, Is.EqualTo(result.MatchedEntries[index].Identity));
            }

            Assert.That(snapshot.Rows.Select(row => row.Message), Is.EqualTo(new[] { "earlier", "later" }));
        });
    }

    [Test]
    public void Presenter_displays_known_and_unknown_attribution_as_text ()
    {
        MinecraftWorkspaceIngressEvent known = CreateIngress(
            1,
            "known-file",
            1,
            "known message",
            Utc("2030-01-01T10:00:00Z"),
            source: Attribution.From("Yeezus", AttributionProvenance.ExplicitMetadata, AttributionConfidence.Exact),
            component: Attribution.From("Core", AttributionProvenance.Parsed, AttributionConfidence.High),
            level: Attribution.From(LogLevel.Warn, AttributionProvenance.Parsed, AttributionConfidence.High),
            thread: Attribution.From("Render thread", AttributionProvenance.Adapter, AttributionConfidence.Exact));
        MinecraftWorkspaceIngressEvent unknown = CreateIngress(
            2,
            "unknown-file",
            1,
            "unknown message",
            Utc("2030-01-01T10:01:00Z"));

        MinecraftWorkspaceReadOnlyViewSnapshot snapshot = MinecraftWorkspaceReadOnlyViewPresenter.CreateSnapshot(
            "Synthetic",
            FilterResult([known, unknown]));
        MinecraftWorkspaceReadOnlyRow knownRow = snapshot.Rows.Single(row => row.Identity == 1);
        MinecraftWorkspaceReadOnlyRow unknownRow = snapshot.Rows.Single(row => row.Identity == 2);

        Assert.Multiple(() =>
        {
            Assert.That(knownRow.Source, Is.EqualTo("Yeezus"));
            Assert.That(knownRow.Component, Is.EqualTo("Core"));
            Assert.That(knownRow.Level, Is.EqualTo("WARN"));
            Assert.That(knownRow.Thread, Is.EqualTo("Render thread"));
            Assert.That(unknownRow.Source, Is.EqualTo("Unknown"));
            Assert.That(unknownRow.Component, Is.EqualTo("Unknown"));
            Assert.That(unknownRow.Level, Is.EqualTo("Unknown"));
            Assert.That(unknownRow.Thread, Is.EqualTo("Unknown"));
        });
    }

    [TestCase(LogLevel.Trace, "TRACE")]
    [TestCase(LogLevel.Debug, "DEBUG")]
    [TestCase(LogLevel.Info, "INFO")]
    [TestCase(LogLevel.Warn, "WARN")]
    [TestCase(LogLevel.Error, "ERROR")]
    [TestCase(LogLevel.Fatal, "FATAL")]
    public void Presenter_severity_display_is_deterministic (LogLevel level, string expected)
    {
        MinecraftWorkspaceIngressEvent ingress = CreateIngress(
            1,
            "level-file",
            1,
            "severity",
            Utc("2030-01-01T10:00:00Z"),
            level: Attribution.From(level, AttributionProvenance.Parsed, AttributionConfidence.Exact));

        MinecraftWorkspaceReadOnlyViewSnapshot snapshot = MinecraftWorkspaceReadOnlyViewPresenter.CreateSnapshot("Synthetic", FilterResult([ingress]));

        Assert.That(snapshot.Rows.Single().Level, Is.EqualTo(expected));
    }

    [Test]
    public void Presenter_time_uses_candidate_timestamp_as_invariant_utc_text ()
    {
        MinecraftWorkspaceIngressEvent ingress = CreateIngress(
            1,
            "offset-file",
            1,
            "offset time",
            DateTimeOffset.Parse("2030-01-01T12:00:00+02:00", System.Globalization.CultureInfo.InvariantCulture));

        MinecraftWorkspaceReadOnlyViewSnapshot snapshot = MinecraftWorkspaceReadOnlyViewPresenter.CreateSnapshot("Synthetic", FilterResult([ingress]));

        Assert.That(snapshot.Rows.Single().Time, Is.EqualTo("2030-01-01T10:00:00.0000000Z"));
    }

    [Test]
    public void Presenter_carries_late_and_source_order_adjustment_metadata ()
    {
        var timeline = new MinecraftWorkspaceTimeline(WorkspaceId);
        timeline.AppendBatch([CreateIngress(1, "same-source", 1, "newer", Utc("2030-01-01T10:03:00Z"))]);
        timeline.AppendBatch([CreateIngress(2, "same-source", 2, "late", Utc("2030-01-01T10:01:00Z"))]);
        MinecraftWorkspaceReadOnlyViewSnapshot snapshot = MinecraftWorkspaceReadOnlyViewPresenter.CreateSnapshot(
            "Synthetic",
            new MinecraftWorkspaceTimelineFilter().Evaluate(timeline.GetOrderedSnapshot(), new MinecraftWorkspaceTimelineFilterQuery()));
        MinecraftWorkspaceReadOnlyRow late = snapshot.Rows.Single(row => row.Identity == 2);

        Assert.Multiple(() =>
        {
            Assert.That(late.IsLate, Is.True);
            Assert.That(late.WasTimestampAdjustedForSourceOrder, Is.True);
            Assert.That(late.Details.IsLate, Is.True);
            Assert.That(late.Details.WasTimestampAdjustedForSourceOrder, Is.True);
        });
    }

    [Test]
    public void Presenter_facet_display_keeps_bucket_order_original_values_and_typed_unknown ()
    {
        MinecraftWorkspaceIngressEvent known = CreateIngress(
            1,
            "known-file",
            1,
            "known",
            Utc("2030-01-01T10:00:00Z"),
            source: Attribution.From("Yeezus", AttributionProvenance.ExplicitMetadata, AttributionConfidence.Exact));
        MinecraftWorkspaceIngressEvent unknown = CreateIngress(2, "unknown-file", 1, "unknown", Utc("2030-01-01T10:01:00Z"));
        MinecraftWorkspaceTimelineFilterResult result = FilterResult([known, unknown]);

        MinecraftWorkspaceReadOnlyFacetSnapshot facets = MinecraftWorkspaceReadOnlyViewPresenter
            .CreateSnapshot("Synthetic", result)
            .Facets;
        var unknownSourceFacet = facets.Sources[^1];

        Assert.Multiple(() =>
        {
            AssertBucketsPreserved(facets.FileIds, result.FacetBuckets.FileIds);
            AssertBucketsPreserved(facets.Sources, result.FacetBuckets.Sources);
            AssertBucketsPreserved(facets.Components, result.FacetBuckets.Components);
            AssertBucketsPreserved(facets.Levels, result.FacetBuckets.Levels);
            AssertBucketsPreserved(facets.Threads, result.FacetBuckets.Threads);
            Assert.That(unknownSourceFacet.DisplayLabel, Is.EqualTo("Unknown"));
            Assert.That(unknownSourceFacet.Value.IsUnknown, Is.True);
            Assert.That(unknownSourceFacet.TotalCount, Is.EqualTo(1));
            Assert.That(unknownSourceFacet.MatchingCount, Is.EqualTo(1));
        });
    }

    [Test]
    public void Presenter_filter_errors_preserve_incomplete_status_without_success_count ()
    {
        MinecraftWorkspaceIngressEvent ingress = CreateIngress(1, "regex-file", 1, "sample", Utc("2030-01-01T10:00:00Z"));
        MinecraftWorkspaceTimeline timeline = Timeline([ingress]);
        MinecraftWorkspaceTimelineFilter filter = new();
        MinecraftWorkspaceTimelineFilterResult invalid = filter.Evaluate(
            timeline.GetOrderedSnapshot(),
            new MinecraftWorkspaceTimelineFilterQuery(searchText: "[", isRegex: true));
        MinecraftWorkspaceReadOnlyViewSnapshot invalidSnapshot = MinecraftWorkspaceReadOnlyViewPresenter.CreateSnapshot("Synthetic", invalid);

        Assert.Multiple(() =>
        {
            Assert.That(invalidSnapshot.FilterStatus, Is.EqualTo(MinecraftWorkspaceFilterStatus.InvalidRegex));
            Assert.That(invalidSnapshot.MatchedCount, Is.Null);
            Assert.That(invalidSnapshot.Rows, Is.Empty);
            Assert.That(invalidSnapshot.ErrorText, Is.Not.Null.And.Not.Empty);
        });
    }

    [Test]
    public void Presenter_timeout_result_does_not_claim_complete_matched_count ()
    {
        string catastrophicInput = new string('a', 50_000) + "!";
        MinecraftWorkspaceIngressEvent ingress = CreateIngress(1, "timeout-file", 1, catastrophicInput, Utc("2030-01-01T10:00:00Z"));
        MinecraftWorkspaceTimeline timeline = Timeline([ingress]);
        MinecraftWorkspaceTimelineFilterResult timeout = new MinecraftWorkspaceTimelineFilter().Evaluate(
            timeline.GetOrderedSnapshot(),
            new MinecraftWorkspaceTimelineFilterQuery(searchText: "^(a+)+$", isRegex: true));
        MinecraftWorkspaceReadOnlyViewSnapshot snapshot = MinecraftWorkspaceReadOnlyViewPresenter.CreateSnapshot("Synthetic", timeout);

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.FilterStatus, Is.EqualTo(MinecraftWorkspaceFilterStatus.RegexTimedOut));
            Assert.That(snapshot.MatchedCount, Is.Null);
            Assert.That(snapshot.Rows, Is.Empty);
            Assert.That(snapshot.ErrorText, Is.Not.Null.And.Not.Empty);
        });
    }

    [Test]
    public void Presenter_details_retain_exact_location_timestamp_and_attribution_references ()
    {
        MinecraftWorkspaceIngressEvent ingress = CreateIngress(
            4,
            "detail-file",
            11,
            "full message\nstack continuation",
            DateTimeOffset.Parse("2030-01-01T12:00:00+02:00", System.Globalization.CultureInfo.InvariantCulture),
            rawText: "raw record\nraw stack",
            path: "F:/synthetic/logs/yeezus.log",
            generation: 3,
            source: Attribution.From("Yeezus", AttributionProvenance.ExplicitMetadata, AttributionConfidence.Exact),
            component: Attribution.From("Core", AttributionProvenance.Parsed, AttributionConfidence.High),
            level: Attribution.From(LogLevel.Error, AttributionProvenance.Adapter, AttributionConfidence.Medium),
            thread: Attribution.From("Client thread", AttributionProvenance.Heuristic, AttributionConfidence.Low),
            producerSequence: 99);
        MinecraftWorkspaceReadOnlyRow row = MinecraftWorkspaceReadOnlyViewPresenter
            .CreateSnapshot("Synthetic", FilterResult([ingress]))
            .Rows
            .Single();
        MinecraftWorkspaceReadOnlyEventDetails details = row.Details;
        NormalizedLogEvent originalEvent = ingress.Event;

        Assert.Multiple(() =>
        {
            Assert.That(row.Message, Is.EqualTo("full message ⏎ stack continuation"));
            Assert.That(details.Message, Is.SameAs(originalEvent.Message));
            Assert.That(details.RawText, Is.SameAs(originalEvent.RawText));
            Assert.That(details.Event, Is.SameAs(originalEvent));
            Assert.That(details.EventRef, Is.SameAs(originalEvent.Ref));
            Assert.That(details.FileRef, Is.SameAs(originalEvent.Ref.File));
            Assert.That(details.PhysicalPath, Is.EqualTo("F:/synthetic/logs/yeezus.log"));
            Assert.That(details.FileId, Is.EqualTo("detail-file"));
            Assert.That(details.Generation, Is.EqualTo(3));
            Assert.That(details.StartByteOffset, Is.EqualTo(originalEvent.Ref.StartByteOffset));
            Assert.That(details.EndByteOffset, Is.EqualTo(originalEvent.Ref.EndByteOffset));
            Assert.That(details.StartLineNumber, Is.EqualTo(originalEvent.Ref.StartLineNumber));
            Assert.That(details.EndLineNumber, Is.EqualTo(originalEvent.Ref.EndLineNumber));
            Assert.That(details.SourceLocalSequence, Is.EqualTo(11));
            Assert.That(details.ProducerSequence, Is.EqualTo(99));
            Assert.That(details.Timestamp, Is.SameAs(originalEvent.Timestamp));
            Assert.That(details.Timestamp.RawValue, Is.EqualTo("2030-01-01T12:00:00.0000000+02:00"));
            Assert.That(details.TimestampBasis, Is.EqualTo(MinecraftWorkspaceTimelineTimestampBasis.Source));
            Assert.That(details.CandidateTimestampUtc, Is.EqualTo(Utc("2030-01-01T10:00:00Z")));
            Assert.That(details.SourceAttribution, Is.SameAs(originalEvent.Source));
            Assert.That(details.ComponentAttribution, Is.SameAs(originalEvent.Component));
            Assert.That(details.LevelAttribution, Is.SameAs(originalEvent.Level));
            Assert.That(details.ThreadAttribution, Is.SameAs(originalEvent.Thread));
            Assert.That(details.SourceAttribution.Provenance, Is.EqualTo(AttributionProvenance.ExplicitMetadata));
            Assert.That(details.ComponentAttribution.Confidence, Is.EqualTo(AttributionConfidence.High));
        });
    }

    [Test]
    public void Control_has_exact_six_ordered_columns_and_read_only_virtual_grid_settings ()
    {
        MinecraftWorkspaceReadOnlyViewSnapshot snapshot = ViewSnapshot(
            CreateIngress(1, "grid-file", 1, "grid row", Utc("2030-01-01T10:00:00Z")));
        using var control = new MinecraftWorkspaceReadOnlyControl(snapshot);

        Assert.Multiple(() =>
        {
            Assert.That(control.EventGrid.Columns.Cast<DataGridViewColumn>().Select(column => column.HeaderText),
                Is.EqualTo(new[] { "Time", "Source", "Component", "Level", "Thread", "Message" }));
            Assert.That(control.EventGrid.Columns.Cast<DataGridViewColumn>().All(column =>
                column.SortMode == DataGridViewColumnSortMode.NotSortable), Is.True);
            Assert.That(control.EventGrid.VirtualMode, Is.True);
            Assert.That(control.EventGrid.ReadOnly, Is.True);
            Assert.That(control.EventGrid.AllowUserToAddRows, Is.False);
            Assert.That(control.EventGrid.AllowUserToDeleteRows, Is.False);
            Assert.That(control.EventGrid.SelectionMode, Is.EqualTo(DataGridViewSelectionMode.FullRowSelect));
            Assert.That(control.EventGrid.MultiSelect, Is.False);
            Assert.That(control.EventGrid.AllowUserToOrderColumns, Is.False);
            Assert.That(control.EventGrid.DataSource, Is.Null);
            Assert.That(control.EventGrid.RowCount, Is.EqualTo(1));
        });
    }

    [Test]
    public void Control_virtual_binding_and_selection_show_exact_retained_entry_details ()
    {
        MinecraftWorkspaceIngressEvent first = CreateIngress(1, "first-file", 1, "first", Utc("2030-01-01T10:00:00Z"));
        MinecraftWorkspaceIngressEvent second = CreateIngress(2, "second-file", 1, "selected", Utc("2030-01-01T10:01:00Z"), rawText: "selected raw detail");
        MinecraftWorkspaceReadOnlyViewSnapshot snapshot = ViewSnapshot(first, second);
        using var form = CreateForm();
        using var control = new MinecraftWorkspaceReadOnlyControl(snapshot);
        form.Controls.Add(control);
        form.Show();
        Application.DoEvents();

        SelectRow(control, 1);

        Assert.Multiple(() =>
        {
            Assert.That(control.EventGrid.RowCount, Is.EqualTo(2));
            Assert.That(control.SelectedEntry, Is.SameAs(snapshot.Rows[1].Entry));
            Assert.That(control.SelectedIdentity, Is.EqualTo(snapshot.Rows[1].Identity));
            Assert.That(control.SelectedDetails!.EventRef, Is.SameAs(snapshot.Rows[1].Entry.IngressEvent.Event.Ref));
            Assert.That(control.SelectedDetails.FileRef, Is.SameAs(snapshot.Rows[1].Entry.IngressEvent.Event.Ref.File));
            Assert.That(control.DetailsTextBox.Text, Does.Contain("selected raw detail"));
        });
    }

    [Test]
    public void Control_rebind_restores_selection_by_identity_after_late_insertion_moves_row ()
    {
        var timeline = new MinecraftWorkspaceTimeline(WorkspaceId);
        timeline.AppendBatch([
            CreateIngress(1, "primary-file", 1, "before selected", Utc("2030-01-01T10:02:00Z")),
            CreateIngress(2, "primary-file", 2, "selected", Utc("2030-01-01T10:03:00Z"), rawText: "selected\n    at stack.Continuation.run(Stack.java:42)")]);
        MinecraftWorkspaceTimelineFilter filter = new();
        MinecraftWorkspaceReadOnlyViewSnapshot before = ViewSnapshot(filter.Evaluate(timeline.GetOrderedSnapshot(), new MinecraftWorkspaceTimelineFilterQuery()));
        long selectedIdentity = before.Rows.Single(row => row.Message.StartsWith("selected", StringComparison.Ordinal)).Identity;
        using var form = CreateForm();
        using var control = new MinecraftWorkspaceReadOnlyControl(before);
        form.Controls.Add(control);
        form.Show();
        Application.DoEvents();
        int oldRowIndex = before.Rows.ToList().FindIndex(row => row.Identity == selectedIdentity);
        SelectRow(control, oldRowIndex);
        EventRef originalEventRef = control.SelectedEntry!.IngressEvent.Event.Ref;
        FileRef originalFileRef = control.SelectedEntry.IngressEvent.Event.Ref.File;

        timeline.AppendBatch([CreateIngress(
            3,
            "late-cfm-file",
            1,
            "inserted late",
            Utc("2030-01-01T10:01:00Z"),
            sourceId: "late-cfm-source")]);
        IReadOnlyList<MinecraftWorkspaceTimelineEntry> afterTimeline = timeline.GetOrderedSnapshot();
        MinecraftWorkspaceReadOnlyViewSnapshot after = ViewSnapshot(filter.Evaluate(afterTimeline, new MinecraftWorkspaceTimelineFilterQuery()));
        int newRowIndex = after.Rows.ToList().FindIndex(row => row.Identity == selectedIdentity);

        control.ApplySnapshot(after);
        Application.DoEvents();

        Assert.Multiple(() =>
        {
            Assert.That(afterTimeline.Single(entry => entry.Identity == 3).IsLate, Is.True);
            Assert.That(newRowIndex, Is.EqualTo(oldRowIndex + 1));
            Assert.That(control.SelectedIdentity, Is.EqualTo(selectedIdentity));
            Assert.That(control.SelectedEntry!.Identity, Is.EqualTo(selectedIdentity));
            Assert.That(control.EventGrid.SelectedRows.Count, Is.EqualTo(1));
            Assert.That(control.EventGrid.SelectedRows[0].Index, Is.EqualTo(newRowIndex));
            Assert.That(control.SelectedEntry.IngressEvent.Event.Ref, Is.SameAs(originalEventRef));
            Assert.That(control.SelectedEntry.IngressEvent.Event.Ref.File, Is.SameAs(originalFileRef));
            Assert.That(control.DetailsTextBox.Text, Does.Contain("stack.Continuation.run"));
        });
    }

    [Test]
    public void Follow_defaults_on_tracks_user_scroll_and_tails_without_changing_selection_or_query ()
    {
        MinecraftWorkspaceIngressEvent[] events = Enumerable.Range(1, 60)
            .Select(index => CreateIngress(
                index,
                "follow-file",
                index,
                $"follow event {index}",
                Utc("2030-01-01T10:00:00Z").AddMinutes(index)))
            .ToArray();
        MinecraftWorkspaceReadOnlyViewSnapshot before = ViewSnapshot(events);
        using var form = CreateForm();
        using var control = new MinecraftWorkspaceReadOnlyControl(before);
        form.Controls.Add(control);
        form.Show();
        Application.DoEvents();

        Assert.Multiple(() =>
        {
            Assert.That(control.IsFollowEnabled, Is.True);
            Assert.That(control.IsPaused, Is.False);
            Assert.That(control.BacklogCount, Is.Zero);
            Assert.That(IsAtTail(control.EventGrid), Is.True);
        });

        int selectedRow = Math.Min(12, control.EventGrid.RowCount - 1);
        SelectRow(control, selectedRow);
        long selectedIdentity = control.SelectedIdentity!.Value;
        control.EventGrid.FirstDisplayedScrollingRowIndex = 0;
        Application.DoEvents();
        Assert.That(control.IsFollowEnabled, Is.False, "Manual scrolling away from the tail disables Follow.");

        control.EventGrid.FirstDisplayedScrollingRowIndex = Math.Max(
            0,
            control.EventGrid.RowCount - control.EventGrid.DisplayedRowCount(includePartialRow: false));
        Application.DoEvents();
        Assert.That(control.IsFollowEnabled, Is.True, "Manual scrolling back to the tail re-enables Follow.");

        control.EventGrid.FirstDisplayedScrollingRowIndex = 0;
        Application.DoEvents();
        control.FollowCheckBox.Checked = true;
        Application.DoEvents();

        Assert.Multiple(() =>
        {
            Assert.That(IsAtTail(control.EventGrid), Is.True, "Manually enabling Follow scrolls to the tail immediately.");
            Assert.That(control.SelectedIdentity, Is.EqualTo(selectedIdentity));
            Assert.That(control.Snapshot.QuerySnapshot.SearchText, Is.Null);
        });

        MinecraftWorkspaceIngressEvent appended = CreateIngress(
            61,
            "follow-file",
            61,
            "follow appended",
            Utc("2030-01-01T12:00:00Z"));
        MinecraftWorkspaceTimeline timeline = Timeline(events);
        timeline.AppendBatch([appended]);
        MinecraftWorkspaceReadOnlyViewSnapshot after = ViewSnapshot(new MinecraftWorkspaceTimelineFilter().Evaluate(
            timeline.GetOrderedSnapshot(),
            control.Snapshot.QuerySnapshot));
        control.ApplySnapshot(after);
        Application.DoEvents();

        Assert.Multiple(() =>
        {
            Assert.That(IsAtTail(control.EventGrid), Is.True, "Follow keeps newly applied live rows at the tail.");
            Assert.That(control.SelectedIdentity, Is.EqualTo(selectedIdentity));
        });
    }

    [Test]
    public void Follow_off_preserves_top_identity_and_selection_after_late_insertion_then_clamps_filtered_anchor ()
    {
        List<MinecraftWorkspaceIngressEvent> events = Enumerable.Range(1, 40)
            .Select(index => CreateIngress(
                index,
                "anchor-file",
                index,
                $"anchor event {index}",
                Utc("2030-01-01T10:00:00Z").AddMinutes(index)))
            .ToList();
        MinecraftWorkspaceTimeline timeline = Timeline(events);
        MinecraftWorkspaceReadOnlyViewSnapshot before = ViewSnapshot(new MinecraftWorkspaceTimelineFilter().Evaluate(
            timeline.GetOrderedSnapshot(),
            new MinecraftWorkspaceTimelineFilterQuery()));
        using var form = CreateForm();
        using var control = new MinecraftWorkspaceReadOnlyControl(before);
        form.Controls.Add(control);
        form.Show();
        Application.DoEvents();
        control.FollowCheckBox.Checked = false;
        control.EventGrid.FirstDisplayedScrollingRowIndex = 8;
        Application.DoEvents();
        int firstDisplayed = control.EventGrid.FirstDisplayedScrollingRowIndex;
        long anchorIdentity = control.Snapshot.Rows[firstDisplayed].Identity;
        int selectionIndex = Math.Min(firstDisplayed + 3, control.Snapshot.Rows.Count - 1);
        SelectRow(control, selectionIndex);
        long selectedIdentity = control.SelectedIdentity!.Value;
        control.EventGrid.HorizontalScrollingOffset = 160;
        int horizontalOffset = control.EventGrid.HorizontalScrollingOffset;

        MinecraftWorkspaceIngressEvent late = CreateIngress(
            100,
            "late-anchor-file",
            1,
            "late anchor insertion",
            Utc("2030-01-01T09:00:00Z"),
            sourceId: "late-anchor-source");
        events.Add(late);
        timeline.AppendBatch([late]);
        MinecraftWorkspaceReadOnlyViewSnapshot after = ViewSnapshot(new MinecraftWorkspaceTimelineFilter().Evaluate(
            timeline.GetOrderedSnapshot(),
            new MinecraftWorkspaceTimelineFilterQuery()));
        int expectedAnchorRow = after.Rows.ToList().FindIndex(row => row.Identity == anchorIdentity);

        control.ApplySnapshot(after);
        Application.DoEvents();

        Assert.Multiple(() =>
        {
            Assert.That(control.IsFollowEnabled, Is.False);
            Assert.That(control.Snapshot.Rows[control.EventGrid.FirstDisplayedScrollingRowIndex].Identity, Is.EqualTo(anchorIdentity));
            Assert.That(control.EventGrid.FirstDisplayedScrollingRowIndex, Is.EqualTo(expectedAnchorRow));
            Assert.That(control.SelectedIdentity, Is.EqualTo(selectedIdentity), "Selection identity is independent from the viewport anchor.");
            Assert.That(control.SelectedDetails!.EventRef, Is.SameAs(after.Rows.Single(row => row.Identity == selectedIdentity).Details.EventRef));
            Assert.That(control.EventGrid.HorizontalScrollingOffset, Is.EqualTo(horizontalOffset));
        });

        MinecraftWorkspaceIngressEvent[] fallbackEvents = Enumerable.Range(101, 8)
            .Select(index => CreateIngress(
                index,
                "fallback-file",
                index,
                $"fallback row {index}",
                Utc("2030-01-01T13:00:00Z").AddMinutes(index)))
            .ToArray();
        events.AddRange(fallbackEvents);
        timeline.AppendBatch(fallbackEvents);
        MinecraftWorkspaceReadOnlyViewSnapshot filtered = ViewSnapshot(new MinecraftWorkspaceTimelineFilter().Evaluate(
            timeline.GetOrderedSnapshot(),
            new MinecraftWorkspaceTimelineFilterQuery(searchText: "fallback")));
        control.ApplySnapshot(filtered);
        Application.DoEvents();

        Assert.Multiple(() =>
        {
            Assert.That(control.EventGrid.RowCount, Is.EqualTo(fallbackEvents.Length));
            Assert.That(control.EventGrid.FirstDisplayedScrollingRowIndex, Is.EqualTo(0), "A filtered-out anchor falls back to a valid clamped prior index.");
            Assert.That(control.IsFollowEnabled, Is.False);
            Assert.That(control.SelectedIdentity, Is.Null);
            Assert.That(control.SelectedDetails, Is.Null);
        });
    }

    [Test]
    public void Control_clears_selection_and_details_when_identity_disappears_from_filtered_view ()
    {
        MinecraftWorkspaceReadOnlyViewSnapshot all = ViewSnapshot(
            CreateIngress(1, "keep-file", 1, "keep this row", Utc("2030-01-01T10:00:00Z")),
            CreateIngress(2, "drop-file", 1, "drop this row", Utc("2030-01-01T10:01:00Z")));
        using var form = CreateForm();
        using var control = new MinecraftWorkspaceReadOnlyControl(all);
        form.Controls.Add(control);
        form.Show();
        Application.DoEvents();
        SelectRow(control, 1);

        MinecraftWorkspaceTimeline timeline = Timeline(
            all.Rows.Select(row => row.Entry.IngressEvent).ToArray());
        MinecraftWorkspaceTimelineFilterResult filtered = new MinecraftWorkspaceTimelineFilter().Evaluate(
            timeline.GetOrderedSnapshot(),
            new MinecraftWorkspaceTimelineFilterQuery(searchText: "keep"));
        control.ApplySnapshot(ViewSnapshot(filtered));
        Application.DoEvents();

        Assert.Multiple(() =>
        {
            Assert.That(control.EventGrid.RowCount, Is.EqualTo(1));
            Assert.That(control.SelectedIdentity, Is.Null);
            Assert.That(control.SelectedEntry, Is.Null);
            Assert.That(control.SelectedDetails, Is.Null);
            Assert.That(control.DetailsTextBox.Text, Is.Empty);
            Assert.That(control.EventGrid.SelectedRows.Count, Is.Zero);
        });
    }

    [Test]
    public void Control_shows_unknown_rows_and_facet_bucket_labels_with_counts ()
    {
        MinecraftWorkspaceReadOnlyViewSnapshot snapshot = ViewSnapshot(
            CreateIngress(1, "unknown-file", 1, "unknown row", Utc("2030-01-01T10:00:00Z")));
        using var control = new MinecraftWorkspaceReadOnlyControl(snapshot);

        MinecraftWorkspaceFacetEditorItem<MinecraftWorkspaceFacetValue<string>> sourceUnknown =
            control.SourceFacetList.Items.Cast<MinecraftWorkspaceFacetEditorItem<MinecraftWorkspaceFacetValue<string>>>()
                .Single(item => item.Value.IsUnknown);

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Rows.Single().Source, Is.EqualTo("Unknown"));
            Assert.That(sourceUnknown.DisplayText, Does.Contain("Unknown"));
            Assert.That(sourceUnknown.DisplayText, Does.Contain("Total: 1"));
            Assert.That(sourceUnknown.DisplayText, Does.Contain("Matching: 1"));
        });
    }

    [Test]
    public void Control_shows_regex_timeout_as_an_incomplete_filter_result ()
    {
        string catastrophicInput = new string('a', 50_000) + "!";
        MinecraftWorkspaceTimeline timeline = Timeline([
            CreateIngress(1, "timeout-control-file", 1, catastrophicInput, Utc("2030-01-01T10:00:00Z"))]);
        MinecraftWorkspaceTimelineFilterResult timeout = new MinecraftWorkspaceTimelineFilter().Evaluate(
            timeline.GetOrderedSnapshot(),
            new MinecraftWorkspaceTimelineFilterQuery(searchText: "^(a+)+$", isRegex: true));
        using MinecraftWorkspaceReadOnlyControl control = new(ViewSnapshot(timeout));

        Assert.Multiple(() =>
        {
            Assert.That(control.StatusLabel.Text, Does.Contain("RegexTimedOut"));
            Assert.That(control.StatusLabel.Text, Does.Contain("Matched: incomplete"));
            Assert.That(control.StatusLabel.Text, Does.Contain(timeout.ErrorDetail));
            Assert.That(control.EventGrid.RowCount, Is.Zero);
        });
    }

    [Test]
    public void Filter_editor_emits_complete_query_and_YEE50_keeps_typed_unknown_distinct_from_known_literal ()
    {
        MinecraftWorkspaceIngressEvent unknownSource = CreateIngress(
            1,
            "unknown-source-file",
            1,
            "unknown source event",
            Utc("2030-01-01T10:00:00Z"),
            component: Attribution.From("Core", AttributionProvenance.Parsed, AttributionConfidence.High));
        MinecraftWorkspaceIngressEvent knownUnknownSource = CreateIngress(
            2,
            "known-unknown-file",
            1,
            "known literal event",
            Utc("2030-01-01T10:01:00Z"),
            source: Attribution.From("Unknown", AttributionProvenance.ExplicitMetadata, AttributionConfidence.Exact),
            component: Attribution.From("Core", AttributionProvenance.Parsed, AttributionConfidence.High));
        MinecraftWorkspaceIngressEvent otherSource = CreateIngress(
            3,
            "other-source-file",
            1,
            "different component event",
            Utc("2030-01-01T10:02:00Z"),
            source: Attribution.From("Yeezus", AttributionProvenance.ExplicitMetadata, AttributionConfidence.Exact),
            component: Attribution.From("Network", AttributionProvenance.Parsed, AttributionConfidence.High));
        MinecraftWorkspaceTimeline timeline = Timeline([unknownSource, knownUnknownSource, otherSource]);
        using Form form = CreateForm();
        using MinecraftWorkspaceReadOnlyControl control = new(ViewSnapshot(FilterResult([unknownSource, knownUnknownSource, otherSource])));
        form.Controls.Add(control);
        form.Show();
        Application.DoEvents();
        List<MinecraftWorkspaceTimelineFilterQuery> emittedQueries = [];
        control.FilterQueryChanged += (_, args) => emittedQueries.Add(args.Query);

        MinecraftWorkspaceFacetEditorItem<MinecraftWorkspaceFacetValue<string>>[] sourceItems = control.SourceFacetList.Items
            .Cast<MinecraftWorkspaceFacetEditorItem<MinecraftWorkspaceFacetValue<string>>>()
            .ToArray();
        int unknownIndex = Array.FindIndex(sourceItems, item => item.Value.IsUnknown);
        int knownLiteralIndex = Array.FindIndex(sourceItems, item => !item.Value.IsUnknown && item.Value.Value == "Unknown");
        int coreIndex = control.ComponentFacetList.Items
            .Cast<MinecraftWorkspaceFacetEditorItem<MinecraftWorkspaceFacetValue<string>>>()
            .ToList()
            .FindIndex(item => !item.Value.IsUnknown && item.Value.Value == "Core");

        control.SourceFacetList.SetItemChecked(unknownIndex, true);
        control.SourceFacetList.SetItemChecked(knownLiteralIndex, true);
        control.ComponentFacetList.SetItemChecked(coreIndex, true);
        MinecraftWorkspaceTimelineFilterQuery query = emittedQueries[^1];
        MinecraftWorkspaceTimelineFilterResult result = new MinecraftWorkspaceTimelineFilter().Evaluate(timeline.GetOrderedSnapshot(), query);

        Assert.Multiple(() =>
        {
            Assert.That(unknownIndex, Is.GreaterThanOrEqualTo(0));
            Assert.That(knownLiteralIndex, Is.GreaterThanOrEqualTo(0));
            Assert.That(coreIndex, Is.GreaterThanOrEqualTo(0));
            Assert.That(query.Sources, Has.Count.EqualTo(2));
            Assert.That(query.Sources.Any(value => value.IsUnknown), Is.True);
            Assert.That(query.Sources.Any(value => !value.IsUnknown && value.Value == "Unknown"), Is.True);
            Assert.That(query.Components, Has.Count.EqualTo(1));
            Assert.That(result.QuerySnapshot, Is.SameAs(query));
            Assert.That(result.MatchedEntries.Select(entry => entry.Identity), Is.EqualTo(new long[] { 1, 2 }));
            Assert.That(sourceItems.Count(item => item.Value.IsUnknown || (!item.Value.IsUnknown && item.Value.Value == "Unknown")), Is.EqualTo(2));
            Assert.That(sourceItems[knownLiteralIndex].DisplayItem.DisplayLabel, Is.EqualTo("\"Unknown\" (literal)"));
        });

        control.SourceFacetList.SetItemChecked(knownLiteralIndex, false);
        MinecraftWorkspaceTimelineFilterQuery unknownOnlyQuery = emittedQueries[^1];
        MinecraftWorkspaceTimelineFilterResult unknownOnly = new MinecraftWorkspaceTimelineFilter().Evaluate(
            timeline.GetOrderedSnapshot(),
            unknownOnlyQuery);
        int eventCountBeforeApply = emittedQueries.Count;
        control.ApplySnapshot(ViewSnapshot(unknownOnly));
        MinecraftWorkspaceFacetEditorItem<MinecraftWorkspaceFacetValue<string>>[] reboundSources = control.SourceFacetList.Items
            .Cast<MinecraftWorkspaceFacetEditorItem<MinecraftWorkspaceFacetValue<string>>>()
            .ToArray();
        int reboundUnknown = Array.FindIndex(reboundSources, item => item.Value.IsUnknown);
        int reboundKnownLiteral = Array.FindIndex(reboundSources, item => !item.Value.IsUnknown && item.Value.Value == "Unknown");

        Assert.Multiple(() =>
        {
            Assert.That(control.SourceFacetList.GetItemChecked(reboundUnknown), Is.True);
            Assert.That(control.SourceFacetList.GetItemChecked(reboundKnownLiteral), Is.False);
            Assert.That(emittedQueries, Has.Count.EqualTo(eventCountBeforeApply));
            Assert.That(control.Snapshot.QuerySnapshot, Is.SameAs(unknownOnlyQuery));
        });
    }

    [Test]
    public void Filter_editor_maps_text_options_and_clear_all_to_one_complete_query ()
    {
        MinecraftWorkspaceReadOnlyViewSnapshot initial = ViewSnapshot(
            CreateIngress(1, "text-options", 1, "message value", Utc("2030-01-01T10:00:00Z"), rawText: "raw value"));
        using Form form = CreateForm();
        using MinecraftWorkspaceReadOnlyControl control = new(initial);
        form.Controls.Add(control);
        form.Show();
        Application.DoEvents();
        List<MinecraftWorkspaceTimelineFilterQuery> emitted = [];
        control.FilterQueryChanged += (_, args) => emitted.Add(args.Query);

        control.SearchTextBox.Text = "message phrase";
        Assert.Multiple(() =>
        {
            Assert.That(emitted[^1].SearchText, Is.EqualTo("message phrase"));
            Assert.That(emitted[^1].TextScope, Is.EqualTo(MinecraftWorkspaceTextScope.Message));
        });
        control.SearchTextBox.Text = "^raw\\s+value$";
        control.TextScopeComboBox.SelectedIndex = (int)MinecraftWorkspaceTextScope.RawText;
        control.RegexCheckBox.Checked = true;
        control.CaseSensitiveCheckBox.Checked = true;
        control.InvertCheckBox.Checked = true;
        MinecraftWorkspaceTimelineFilterQuery complete = emitted[^1];

        Assert.Multiple(() =>
        {
            Assert.That(complete.SearchText, Is.EqualTo("^raw\\s+value$"));
            Assert.That(complete.TextScope, Is.EqualTo(MinecraftWorkspaceTextScope.RawText));
            Assert.That(complete.IsRegex, Is.True);
            Assert.That(complete.IsCaseSensitive, Is.True);
            Assert.That(complete.IsInvert, Is.True);
            Assert.That(complete.FileIds, Is.Empty);
            Assert.That(complete.Sources, Is.Empty);
            Assert.That(complete.Components, Is.Empty);
            Assert.That(complete.Levels, Is.Empty);
            Assert.That(complete.Threads, Is.Empty);
        });

        foreach (MinecraftWorkspaceTextScope scope in Enum.GetValues<MinecraftWorkspaceTextScope>())
        {
            control.TextScopeComboBox.SelectedIndex = (int)scope;
            Assert.That(emitted[^1].TextScope, Is.EqualTo(scope));
        }

        int beforeApply = emitted.Count;
        control.ApplySnapshot(ViewSnapshot(new MinecraftWorkspaceTimelineFilter().Evaluate(
            Timeline([CreateIngress(1, "text-options", 1, "message value", Utc("2030-01-01T10:00:00Z"), rawText: "raw value")]).GetOrderedSnapshot(),
            complete)));
        Assert.That(emitted, Has.Count.EqualTo(beforeApply), "Applying the exact YEE-50 snapshot query must not emit a recursive query change.");

        control.ClearFiltersButton.PerformClick();
        MinecraftWorkspaceTimelineFilterQuery cleared = emitted[^1];
        Assert.Multiple(() =>
        {
            Assert.That(cleared.SearchText, Is.Null);
            Assert.That(cleared.TextScope, Is.EqualTo(MinecraftWorkspaceTextScope.Message));
            Assert.That(cleared.IsRegex, Is.False);
            Assert.That(cleared.IsCaseSensitive, Is.False);
            Assert.That(cleared.IsInvert, Is.False);
            Assert.That(cleared.FileIds, Is.Empty);
            Assert.That(cleared.Sources, Is.Empty);
            Assert.That(cleared.Components, Is.Empty);
            Assert.That(cleared.Levels, Is.Empty);
            Assert.That(cleared.Threads, Is.Empty);
        });
    }

    [Test]
    public void Filter_editor_maps_file_id_and_typed_unknown_component_level_and_thread_values ()
    {
        MinecraftWorkspaceIngressEvent unknown = CreateIngress(
            1, "exact-file-id", 1, "unknown event", Utc("2030-01-01T10:00:00Z"));
        MinecraftWorkspaceIngressEvent known = CreateIngress(
            2,
            "known-values-file",
            1,
            "known event",
            Utc("2030-01-01T10:01:00Z"),
            source: Attribution.From("Yeezus", AttributionProvenance.ExplicitMetadata, AttributionConfidence.Exact),
            component: Attribution.From("Core", AttributionProvenance.Parsed, AttributionConfidence.High),
            level: Attribution.From(LogLevel.Error, AttributionProvenance.Parsed, AttributionConfidence.High),
            thread: Attribution.From("Client thread", AttributionProvenance.Parsed, AttributionConfidence.High));
        using Form form = CreateForm();
        using MinecraftWorkspaceReadOnlyControl control = new(ViewSnapshot(unknown, known));
        form.Controls.Add(control);
        form.Show();
        Application.DoEvents();
        List<MinecraftWorkspaceTimelineFilterQuery> emitted = [];
        control.FilterQueryChanged += (_, args) => emitted.Add(args.Query);

        int fileIndex = control.FileFacetList.Items
            .Cast<MinecraftWorkspaceFacetEditorItem<string>>()
            .ToList()
            .FindIndex(item => item.Value == unknown.FileId);
        int unknownComponentIndex = control.ComponentFacetList.Items
            .Cast<MinecraftWorkspaceFacetEditorItem<MinecraftWorkspaceFacetValue<string>>>()
            .ToList()
            .FindIndex(item => item.Value.IsUnknown);
        int unknownLevelIndex = control.LevelFacetList.Items
            .Cast<MinecraftWorkspaceFacetEditorItem<MinecraftWorkspaceFacetValue<LogLevel>>>()
            .ToList()
            .FindIndex(item => item.Value.IsUnknown);
        int unknownThreadIndex = control.ThreadFacetList.Items
            .Cast<MinecraftWorkspaceFacetEditorItem<MinecraftWorkspaceFacetValue<string>>>()
            .ToList()
            .FindIndex(item => item.Value.IsUnknown);
        control.FileFacetList.SetItemChecked(fileIndex, true);
        control.ComponentFacetList.SetItemChecked(unknownComponentIndex, true);
        control.LevelFacetList.SetItemChecked(unknownLevelIndex, true);
        control.ThreadFacetList.SetItemChecked(unknownThreadIndex, true);
        MinecraftWorkspaceTimelineFilterQuery query = emitted[^1];
        MinecraftWorkspaceTimelineFilterResult result = new MinecraftWorkspaceTimelineFilter().Evaluate(
            Timeline([unknown, known]).GetOrderedSnapshot(),
            query);

        Assert.Multiple(() =>
        {
            Assert.That(query.FileIds, Is.EqualTo(new[] { unknown.FileId }));
            Assert.That(query.Components.Single().IsUnknown, Is.True);
            Assert.That(query.Levels.Single().IsUnknown, Is.True);
            Assert.That(query.Threads.Single().IsUnknown, Is.True);
            Assert.That(result.QuerySnapshot, Is.SameAs(query));
            Assert.That(result.MatchedEntries.Select(entry => entry.Identity), Is.EqualTo(new long[] { unknown.IngressSequence }));
            AssertEditorBucketsPreserved(control.FileFacetList, control.Snapshot.Facets.FileIds);
            AssertEditorBucketsPreserved(control.SourceFacetList, control.Snapshot.Facets.Sources);
            AssertEditorBucketsPreserved(control.ComponentFacetList, control.Snapshot.Facets.Components);
            AssertEditorBucketsPreserved(control.LevelFacetList, control.Snapshot.Facets.Levels);
            AssertEditorBucketsPreserved(control.ThreadFacetList, control.Snapshot.Facets.Threads);
        });
    }

    [Test]
    public void Filter_editor_combines_source_and_level_facets_with_AND_semantics ()
    {
        MinecraftWorkspaceIngressEvent matching = CreateIngress(
            1,
            "yee-error",
            1,
            "matching event",
            Utc("2030-01-01T10:00:00Z"),
            source: Attribution.From("Yeezus", AttributionProvenance.ExplicitMetadata, AttributionConfidence.Exact),
            level: Attribution.From(LogLevel.Error, AttributionProvenance.Parsed, AttributionConfidence.High));
        MinecraftWorkspaceIngressEvent wrongLevel = CreateIngress(
            2,
            "yee-info",
            1,
            "wrong level",
            Utc("2030-01-01T10:01:00Z"),
            source: Attribution.From("Yeezus", AttributionProvenance.ExplicitMetadata, AttributionConfidence.Exact),
            level: Attribution.From(LogLevel.Info, AttributionProvenance.Parsed, AttributionConfidence.High));
        MinecraftWorkspaceIngressEvent wrongSource = CreateIngress(
            3,
            "other-error",
            1,
            "wrong source",
            Utc("2030-01-01T10:02:00Z"),
            source: Attribution.From("ReCactus", AttributionProvenance.ExplicitMetadata, AttributionConfidence.Exact),
            level: Attribution.From(LogLevel.Error, AttributionProvenance.Parsed, AttributionConfidence.High));
        MinecraftWorkspaceTimeline timeline = Timeline([matching, wrongLevel, wrongSource]);
        using Form form = CreateForm();
        using MinecraftWorkspaceReadOnlyControl control = new(ViewSnapshot(matching, wrongLevel, wrongSource));
        form.Controls.Add(control);
        form.Show();
        Application.DoEvents();
        MinecraftWorkspaceTimelineFilterQuery? emittedQuery = null;
        control.FilterQueryChanged += (_, args) => emittedQuery = args.Query;
        int yeezus = control.SourceFacetList.Items
            .Cast<MinecraftWorkspaceFacetEditorItem<MinecraftWorkspaceFacetValue<string>>>()
            .ToList()
            .FindIndex(item => !item.Value.IsUnknown && item.Value.Value == "Yeezus");
        int error = control.LevelFacetList.Items
            .Cast<MinecraftWorkspaceFacetEditorItem<MinecraftWorkspaceFacetValue<LogLevel>>>()
            .ToList()
            .FindIndex(item => !item.Value.IsUnknown && item.Value.Value == LogLevel.Error);
        control.SourceFacetList.SetItemChecked(yeezus, true);
        control.LevelFacetList.SetItemChecked(error, true);
        MinecraftWorkspaceTimelineFilterResult filtered = new MinecraftWorkspaceTimelineFilter().Evaluate(
            timeline.GetOrderedSnapshot(),
            emittedQuery!);

        Assert.That(filtered.MatchedEntries.Select(entry => entry.Identity), Is.EqualTo(new[] { matching.IngressSequence }));
    }

    [Test]
    public void Filter_editor_uses_YEE50_for_message_raw_and_message_or_raw_text_scopes ()
    {
        MinecraftWorkspaceIngressEvent ingress = CreateIngress(
            1,
            "raw-scope-file",
            1,
            "visible headline",
            Utc("2030-01-01T10:00:00Z"),
            rawText: "visible headline\n    at synthetic.RawOnly.run(Stack.java:42)");
        MinecraftWorkspaceTimeline timeline = Timeline([ingress]);
        using Form form = CreateForm();
        using MinecraftWorkspaceReadOnlyControl control = new(ViewSnapshot(ingress));
        form.Controls.Add(control);
        form.Show();
        Application.DoEvents();
        MinecraftWorkspaceTimelineFilterQuery? emittedQuery = null;
        control.FilterQueryChanged += (_, args) => emittedQuery = args.Query;
        control.SearchTextBox.Text = "RawOnly\\.run";
        control.RegexCheckBox.Checked = true;

        MinecraftWorkspaceTimelineFilterResult messageOnly = new MinecraftWorkspaceTimelineFilter().Evaluate(
            timeline.GetOrderedSnapshot(), emittedQuery!);
        control.TextScopeComboBox.SelectedIndex = (int)MinecraftWorkspaceTextScope.RawText;
        MinecraftWorkspaceTimelineFilterResult rawOnly = new MinecraftWorkspaceTimelineFilter().Evaluate(
            timeline.GetOrderedSnapshot(), emittedQuery!);
        control.TextScopeComboBox.SelectedIndex = (int)MinecraftWorkspaceTextScope.MessageOrRawText;
        MinecraftWorkspaceTimelineFilterResult either = new MinecraftWorkspaceTimelineFilter().Evaluate(
            timeline.GetOrderedSnapshot(), emittedQuery!);

        Assert.Multiple(() =>
        {
            Assert.That(messageOnly.MatchedEntries, Is.Empty);
            Assert.That(rawOnly.MatchedEntries.Select(entry => entry.Identity), Is.EqualTo(new[] { ingress.IngressSequence }));
            Assert.That(either.MatchedEntries.Select(entry => entry.Identity), Is.EqualTo(new[] { ingress.IngressSequence }));
        });
    }

    [Test]
    public void Filter_editor_preserves_exact_checked_values_and_leaves_new_buckets_unchecked_on_live_snapshot ()
    {
        MinecraftWorkspaceIngressEvent first = CreateIngress(
            1,
            "first-facet-file",
            1,
            "first source event",
            Utc("2030-01-01T10:00:00Z"),
            source: Attribution.From("First", AttributionProvenance.ExplicitMetadata, AttributionConfidence.Exact));
        MinecraftWorkspaceIngressEvent second = CreateIngress(
            2,
            "second-facet-file",
            1,
            "new source event",
            Utc("2030-01-01T10:01:00Z"),
            source: Attribution.From("Second", AttributionProvenance.ExplicitMetadata, AttributionConfidence.Exact));
        using Form form = CreateForm();
        using MinecraftWorkspaceReadOnlyControl control = new(ViewSnapshot(first));
        form.Controls.Add(control);
        form.Show();
        Application.DoEvents();
        List<MinecraftWorkspaceTimelineFilterQuery> emitted = [];
        control.FilterQueryChanged += (_, args) => emitted.Add(args.Query);
        int firstSourceIndex = control.SourceFacetList.Items
            .Cast<MinecraftWorkspaceFacetEditorItem<MinecraftWorkspaceFacetValue<string>>>()
            .ToList()
            .FindIndex(item => !item.Value.IsUnknown && item.Value.Value == "First");
        control.SourceFacetList.SetItemChecked(firstSourceIndex, true);
        MinecraftWorkspaceTimelineFilterQuery query = emitted[^1];
        MinecraftWorkspaceTimelineFilterResult result = new MinecraftWorkspaceTimelineFilter().Evaluate(
            Timeline([first, second]).GetOrderedSnapshot(),
            query);
        int emittedBeforeApply = emitted.Count;

        control.ApplySnapshot(ViewSnapshot(result));

        MinecraftWorkspaceFacetEditorItem<MinecraftWorkspaceFacetValue<string>>[] sources = control.SourceFacetList.Items
            .Cast<MinecraftWorkspaceFacetEditorItem<MinecraftWorkspaceFacetValue<string>>>()
            .ToArray();
        int appliedFirstIndex = Array.FindIndex(sources, item => !item.Value.IsUnknown && item.Value.Value == "First");
        int newSourceIndex = Array.FindIndex(sources, item => !item.Value.IsUnknown && item.Value.Value == "Second");
        control.SearchTextBox.Text = "source";
        MinecraftWorkspaceTimelineFilterQuery afterTextEdit = emitted[^1];

        Assert.Multiple(() =>
        {
            Assert.That(control.SourceFacetList.GetItemChecked(appliedFirstIndex), Is.True);
            Assert.That(control.SourceFacetList.GetItemChecked(newSourceIndex), Is.False);
            Assert.That(afterTextEdit.Sources, Is.EqualTo(query.Sources));
            Assert.That(afterTextEdit.SearchText, Is.EqualTo("source"));
            Assert.That(emittedBeforeApply, Is.EqualTo(1));
        });
    }

    [Test]
    public void Control_shows_filter_error_as_incomplete_without_fake_success_rows ()
    {
        MinecraftWorkspaceTimeline timeline = Timeline([CreateIngress(1, "error-file", 1, "not matched", Utc("2030-01-01T10:00:00Z"))]);
        MinecraftWorkspaceTimelineFilterResult result = new MinecraftWorkspaceTimelineFilter().Evaluate(
            timeline.GetOrderedSnapshot(),
            new MinecraftWorkspaceTimelineFilterQuery(searchText: "[", isRegex: true));
        using var control = new MinecraftWorkspaceReadOnlyControl(ViewSnapshot(result));

        Assert.Multiple(() =>
        {
            Assert.That(control.StatusLabel.Text, Does.Contain("InvalidRegex"));
            Assert.That(control.StatusLabel.Text, Does.Contain("Matched: incomplete"));
            Assert.That(control.EventGrid.RowCount, Is.Zero);
            Assert.That(control.SelectedEntry, Is.Null);
        });
    }

    [Test]
    public void Control_starts_with_the_canonical_empty_query_and_all_facets_unchecked ()
    {
        MinecraftWorkspaceTimelineFilterResult result = FilterResult([
            CreateIngress(1, "empty-query-file", 1, "initial event", Utc("2030-01-01T10:00:00Z"))]);
        using MinecraftWorkspaceReadOnlyControl control = new(ViewSnapshot(result));
        MinecraftWorkspaceTimelineFilterQuery query = control.Snapshot.QuerySnapshot;

        Assert.Multiple(() =>
        {
            Assert.That(query, Is.SameAs(result.QuerySnapshot));
            Assert.That(query.SearchText, Is.Null);
            Assert.That(query.IsRegex, Is.False);
            Assert.That(query.IsCaseSensitive, Is.False);
            Assert.That(query.IsInvert, Is.False);
            Assert.That(query.TextScope, Is.EqualTo(MinecraftWorkspaceTextScope.Message));
            Assert.That(query.FileIds, Is.Empty);
            Assert.That(query.Sources, Is.Empty);
            Assert.That(query.Components, Is.Empty);
            Assert.That(query.Levels, Is.Empty);
            Assert.That(query.Threads, Is.Empty);
            Assert.That(control.FileFacetList.CheckedItems, Is.Empty);
            Assert.That(control.SourceFacetList.CheckedItems, Is.Empty);
            Assert.That(control.ComponentFacetList.CheckedItems, Is.Empty);
            Assert.That(control.LevelFacetList.CheckedItems, Is.Empty);
            Assert.That(control.ThreadFacetList.CheckedItems, Is.Empty);
        });
    }

    [Test]
    public void Document_is_document_style_and_disposal_does_not_mutate_core_result ()
    {
        MinecraftWorkspaceTimeline timeline = Timeline([CreateIngress(1, "document-file", 1, "document event", Utc("2030-01-01T10:00:00Z"))]);
        MinecraftWorkspaceTimelineFilterResult result = new MinecraftWorkspaceTimelineFilter().Evaluate(
            timeline.GetOrderedSnapshot(), new MinecraftWorkspaceTimelineFilterQuery());
        MinecraftWorkspaceTimelineEntry originalEntry = result.MatchedEntries.Single();
        NormalizedLogEvent originalEvent = originalEntry.IngressEvent.Event;
        int originalTimelineCount = timeline.Count;
        MinecraftWorkspaceReadOnlyViewSnapshot snapshot = ViewSnapshot(result);

        using var form = CreateForm();
        using var dockPanel = CreateDockPanel();
        form.Controls.Add(dockPanel);
        form.Show();
        using var document = new MinecraftWorkspaceReadOnlyDocument(snapshot);
        document.Show(dockPanel, DockState.Document);
        Application.DoEvents();

        Assert.Multiple(() =>
        {
            Assert.That(document.Text, Is.EqualTo("Synthetic workspace"));
            Assert.That(document.TabText, Is.EqualTo("Synthetic workspace"));
            Assert.That(document.ShowHint, Is.EqualTo(DockState.Document));
            Assert.That(document.DockState, Is.EqualTo(DockState.Document));
            Assert.That(document.WorkspaceControl.Parent, Is.Not.Null);
        });

        document.Close();
        Application.DoEvents();

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(MinecraftWorkspaceFilterStatus.Success));
            Assert.That(result.MatchedEntries.Single(), Is.SameAs(originalEntry));
            Assert.That(result.MatchedEntries.Single().IngressEvent.Event, Is.SameAs(originalEvent));
            Assert.That(timeline.Count, Is.EqualTo(originalTimelineCount));
        });
    }

    [Test]
    public void Real_coordinator_timeline_filter_and_dock_document_keep_selection_after_late_insert ()
    {
        string latestPath = CreateFile("logs/latest.log",
            "[18:41:03] [Render thread/ERROR] synthetic UI latest event\n");
        string yeezusPath = CreateFile("logs/yeezus.log",
            "2099-01-01T10:02:00Z [INFO] [yeezus-core] [Client thread] synthetic UI earlier event\n" +
            "2099-01-01T10:03:00Z [INFO] [yeezus-core] [Client thread] synthetic UI selected event\n" +
            "    at synthetic.WorkspaceSelection.run(Stack.java:42)\n" +
            "2099-01-01T10:04:00Z [INFO] [yeezus-core] [Client thread] synthetic UI framing boundary\n");
        var workspace = new MinecraftWorkspace(_testDirectory);
        var discovery = new MinecraftSourceDiscovery(workspace);
        var factory = new MinecraftWorkspaceLiveSourceSessionFactory(
            PluginRegistry.PluginRegistry.Instance,
            new EncodingOptions { Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false) },
            maximumLineLength: 2048);
        using var coordinator = new MinecraftWorkspaceLiveCoordinator(discovery, factory);
        coordinator.Reconcile();

        IReadOnlyList<MinecraftWorkspaceIngressEvent> initialEvents = WaitForIngress(coordinator, events =>
            events.Any(item => item.Event.RawText.Contains("synthetic UI selected event", StringComparison.Ordinal)) &&
            events.Any(item => item.Event.RawText.Contains("synthetic UI latest event", StringComparison.Ordinal)));
        var timeline = new MinecraftWorkspaceTimeline(workspace.WorkspaceId);
        timeline.AppendBatch(initialEvents);
        MinecraftWorkspaceTimelineFilter filter = new();
        MinecraftWorkspaceTimelineFilterResult initialFilter = filter.Evaluate(
            timeline.GetOrderedSnapshot(), new MinecraftWorkspaceTimelineFilterQuery());
        MinecraftWorkspaceReadOnlyViewSnapshot initialView = ViewSnapshot(initialFilter);
        MinecraftWorkspaceTimelineEntry initialSelectedEntry = initialFilter.MatchedEntries.Single(entry =>
            entry.IngressEvent.Event.RawText.Contains("synthetic UI selected event", StringComparison.Ordinal));
        long selectedIdentity = initialSelectedEntry.Identity;
        EventRef selectedEventRef = initialSelectedEntry.IngressEvent.Event.Ref;
        FileRef selectedFileRef = selectedEventRef.File;

        using var form = CreateForm();
        using var dockPanel = CreateDockPanel();
        form.Controls.Add(dockPanel);
        form.Show();
        using var document = new MinecraftWorkspaceReadOnlyDocument(initialView);
        document.Show(dockPanel, DockState.Document);
        Application.DoEvents();
        MinecraftWorkspaceReadOnlyControl control = document.WorkspaceControl;
        int oldRowIndex = initialView.Rows.ToList().FindIndex(row => row.Identity == selectedIdentity);
        SelectRow(control, oldRowIndex);

        string latePath = CreateFile(
            "cactusmonitor/sessions/cfm-synthetic-late.jsonl",
            $"{{\"type\":\"CYCLE\",\"sessionId\":\"synthetic-late-session\",\"sequence\":1,\"timestampEpochMillis\":{Utc("2099-01-01T10:01:00Z").ToUnixTimeMilliseconds()}}}\n");
        Assert.That(File.Exists(latePath), Is.True);
        coordinator.Reconcile();
        IReadOnlyList<MinecraftWorkspaceIngressEvent> lateIngress = WaitForIngress(coordinator, events =>
            events.Any(item => item.Event.RawText.Contains("synthetic-late-session", StringComparison.Ordinal)));
        timeline.AppendBatch(lateIngress);
        IReadOnlyList<MinecraftWorkspaceTimelineEntry> updatedTimeline = timeline.GetOrderedSnapshot();
        MinecraftWorkspaceTimelineFilterResult updatedFilter = filter.Evaluate(
            updatedTimeline, new MinecraftWorkspaceTimelineFilterQuery());
        MinecraftWorkspaceReadOnlyViewSnapshot updatedView = ViewSnapshot(updatedFilter);
        int newRowIndex = updatedView.Rows.ToList().FindIndex(row => row.Identity == selectedIdentity);

        control.ApplySnapshot(updatedView);
        Application.DoEvents();

        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(latestPath), Is.True);
            Assert.That(File.Exists(yeezusPath), Is.True);
            Assert.That(initialView.Rows.Select(row => row.Entry.Identity), Is.EqualTo(initialFilter.MatchedEntries.Select(entry => entry.Identity)));
            Assert.That(oldRowIndex, Is.GreaterThanOrEqualTo(1));
            Assert.That(newRowIndex, Is.EqualTo(oldRowIndex + 1));
            Assert.That(updatedTimeline.Single(entry => entry.Identity == lateIngress.Single().IngressSequence).IsLate, Is.True);
            Assert.That(control.SelectedIdentity, Is.EqualTo(selectedIdentity));
            Assert.That(control.SelectedEntry!.Identity, Is.EqualTo(selectedIdentity));
            Assert.That(control.EventGrid.SelectedRows.Count, Is.EqualTo(1));
            Assert.That(control.EventGrid.SelectedRows[0].Index, Is.EqualTo(newRowIndex));
            Assert.That(control.DetailsTextBox.Text, Does.Contain("synthetic.WorkspaceSelection.run"));
            Assert.That(control.SelectedEntry.IngressEvent.Event.Ref, Is.SameAs(selectedEventRef));
            Assert.That(control.SelectedEntry.IngressEvent.Event.Ref.File, Is.SameAs(selectedFileRef));
            Assert.That(control.SelectedDetails!.Entry, Is.SameAs(control.SelectedEntry));
            Assert.That(document.DockState, Is.EqualTo(DockState.Document));
        });

        document.Close();
        Application.DoEvents();
    }

    private static Form CreateForm () => new()
    {
        ClientSize = new Size(1100, 760),
        StartPosition = FormStartPosition.Manual,
        Location = new Point(0, 0)
    };

    private static DockPanel CreateDockPanel () => new()
    {
        Dock = DockStyle.Fill,
        DocumentStyle = DocumentStyle.DockingWindow,
        Theme = new VS2015LightTheme()
    };

    private static void SelectRow (MinecraftWorkspaceReadOnlyControl control, int rowIndex)
    {
        control.EventGrid.CurrentCell = control.EventGrid.Rows[rowIndex].Cells[0];
        control.EventGrid.Rows[rowIndex].Selected = true;
        Application.DoEvents();
    }

    private static bool IsAtTail (DataGridView grid) => grid.RowCount == 0 ||
        grid.FirstDisplayedScrollingRowIndex + grid.DisplayedRowCount(includePartialRow: false) >= grid.RowCount;

    private static void AssertBucketsPreserved<TValue> (
        IReadOnlyList<MinecraftWorkspaceFacetDisplayItem<TValue>> displayItems,
        IReadOnlyList<MinecraftWorkspaceFacetBucket<TValue>> originalBuckets)
    {
        Assert.That(displayItems.Count, Is.EqualTo(originalBuckets.Count));
        for (int index = 0; index < originalBuckets.Count; index++)
        {
            Assert.That(displayItems[index].Bucket, Is.SameAs(originalBuckets[index]));
        }
    }

    private static void AssertEditorBucketsPreserved<TValue> (
        CheckedListBox list,
        IReadOnlyList<MinecraftWorkspaceFacetDisplayItem<TValue>> originalItems)
    {
        Assert.That(list.Items.Count, Is.EqualTo(originalItems.Count));
        for (int index = 0; index < originalItems.Count; index++)
        {
            MinecraftWorkspaceFacetEditorItem<TValue> item = (MinecraftWorkspaceFacetEditorItem<TValue>)list.Items[index]!;
            Assert.That(item.DisplayItem, Is.SameAs(originalItems[index]));
        }
    }

    private static MinecraftWorkspaceReadOnlyViewSnapshot ViewSnapshot (params MinecraftWorkspaceIngressEvent[] events) =>
        ViewSnapshot(FilterResult(events));

    private static MinecraftWorkspaceReadOnlyViewSnapshot ViewSnapshot (MinecraftWorkspaceTimelineFilterResult result) =>
        MinecraftWorkspaceReadOnlyViewPresenter.CreateSnapshot("Synthetic workspace", result);

    private static MinecraftWorkspaceTimelineFilterResult FilterResult (IReadOnlyList<MinecraftWorkspaceIngressEvent> events) =>
        new MinecraftWorkspaceTimelineFilter().Evaluate(Timeline(events).GetOrderedSnapshot(), new MinecraftWorkspaceTimelineFilterQuery());

    private static MinecraftWorkspaceTimeline Timeline (IReadOnlyList<MinecraftWorkspaceIngressEvent> events)
    {
        var timeline = new MinecraftWorkspaceTimeline(WorkspaceId);
        timeline.AppendBatch(events);
        return timeline;
    }

    private static MinecraftWorkspaceIngressEvent CreateIngress (
        long ingressSequence,
        string fileId,
        long sourceLocalSequence,
        string message,
        DateTimeOffset timestamp,
        string? rawText = null,
        string? path = null,
        long generation = 1,
        string? sourceId = null,
        AttributedValue<string>? source = null,
        AttributedValue<string>? component = null,
        AttributedValue<LogLevel>? level = null,
        AttributedValue<string>? thread = null,
        long? producerSequence = null)
    {
        var fileRef = new FileRef(fileId, path ?? $"F:/synthetic/{fileId}.log", generation);
        long startByteOffset = ingressSequence * 100;
        var eventRef = new EventRef(
            fileRef,
            sourceLocalSequence,
            startByteOffset,
            startByteOffset + 50,
            sourceLocalSequence,
            sourceLocalSequence + (message.Contains('\n', StringComparison.Ordinal) ? 1 : 0));
        string rawTimestamp = timestamp.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        var normalizedEvent = new NormalizedLogEvent(
            eventRef,
            source ?? Attribution.Unknown<string>(),
            component ?? Attribution.Unknown<string>(),
            level ?? Attribution.Unknown<LogLevel>(),
            null,
            thread ?? Attribution.Unknown<string>(),
            EventTimestamp.FromSource(timestamp, rawTimestamp),
            EventParseStatus.Parsed,
            message,
            rawText ?? message,
            producerSequence);

        return new MinecraftWorkspaceIngressEvent(
            ingressSequence,
            WorkspaceId,
            sourceId ?? fileId,
            fileId,
            MinecraftSourceSegmentRole.Primary,
            normalizedEvent,
            IngestedAt);
    }

    private static DateTimeOffset Utc (string value) =>
        DateTimeOffset.Parse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal);

    private string CreateFile (string relativePath, string content)
    {
        string path = Path.Join(_testDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
        _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private static IReadOnlyList<MinecraftWorkspaceIngressEvent> WaitForIngress (
        MinecraftWorkspaceLiveCoordinator coordinator,
        Predicate<IReadOnlyList<MinecraftWorkspaceIngressEvent>> isReady)
    {
        var drained = new List<MinecraftWorkspaceIngressEvent>();
        DateTime deadlineUtc = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadlineUtc)
        {
            drained.AddRange(coordinator.DrainPendingEvents());
            if (isReady(drained))
            {
                return Array.AsReadOnly(drained.ToArray());
            }

            Application.DoEvents();
            Thread.Sleep(10);
        }

        Assert.Fail("Timed out waiting for synthetic workspace events from the real reader/coordinator path.");
        return Array.Empty<MinecraftWorkspaceIngressEvent>();
    }

    private const string WorkspaceId = "synthetic-workspace";
}

#pragma warning restore CA1303
