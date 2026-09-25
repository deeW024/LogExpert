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

        TreeNode sourceGroup = control.FacetTree.Nodes["Source"]!;

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Rows.Single().Source, Is.EqualTo("Unknown"));
            Assert.That(sourceGroup.Nodes.Cast<TreeNode>().Any(node =>
                node.Text.Contains("Unknown", StringComparison.Ordinal) &&
                node.Text.Contains("Total: 1", StringComparison.Ordinal) &&
                node.Text.Contains("Matching: 1", StringComparison.Ordinal)), Is.True);
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
