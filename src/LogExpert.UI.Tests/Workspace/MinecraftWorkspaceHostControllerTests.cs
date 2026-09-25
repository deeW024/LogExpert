#pragma warning disable CA1303 // Synthetic paths, messages, and workspace names are assertion-visible.
#pragma warning disable CA1001 // NUnit TearDown disposes the per-test host.
#pragma warning disable CA1031 // The manual test dispatcher captures failures from worker actions for the test thread.

using System.Diagnostics;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text;

using LogExpert.Core.Classes.MinecraftLogs;
using LogExpert.Core.Config;
using LogExpert.Core.Entities;
using LogExpert.Core.Enums;
using LogExpert.Core.Interfaces;
using LogExpert.UI.Controls.LogTabWindow;
using LogExpert.UI.Interface;
using LogExpert.UI.Workspace;

using Moq;
using NUnit.Framework;
using WeifenLuo.WinFormsUI.Docking;

namespace LogExpert.UI.Tests.Workspace;

[TestFixture]
[Apartment(ApartmentState.STA)]
[NonParallelizable]
[SupportedOSPlatform("windows")]
public sealed class MinecraftWorkspaceHostControllerTests
{
    private string _testDirectory = null!;
    private TestHost? _host;

    [SetUp]
    public void SetUp ()
    {
        _testDirectory = Path.Join(Path.GetTempPath(), "LogExpertWorkspaceHostTests", Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(_testDirectory);
        _ = PluginRegistry.PluginRegistry.Create(_testDirectory, 500);
    }

    [TearDown]
    public void TearDown ()
    {
        _host?.Dispose();
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }

    [Test]
    public void Picker_cancel_leaves_workspace_and_unrelated_document_unchanged ()
    {
        FakeRuntimeFactory runtimeFactory = new();
        _host = CreateHost(runtimeFactory, new FakePicker(null));
        using DockContent unrelatedDocument = new() { Text = "Existing tab" };
        unrelatedDocument.Show(_host.DockPanel, DockState.Document);

        bool opened = _host.Controller.OpenSelectedWorkspaceAsync().GetAwaiter().GetResult();

        Assert.Multiple(() =>
        {
            Assert.That(opened, Is.False);
            Assert.That(runtimeFactory.CreateCount, Is.Zero);
            Assert.That(_host.Controller.ActiveDocument, Is.Null);
            Assert.That(unrelatedDocument.IsDisposed, Is.False);
            Assert.That(unrelatedDocument.DockState, Is.EqualTo(DockState.Document));
            Assert.That(_host.Dispatcher.PendingBackground, Is.Zero);
        });
    }

    [Test]
    public void Invalid_candidate_keeps_current_workspace_and_existing_dock_document ()
    {
        FakeRuntimeFactory runtimeFactory = new();
        FakeRuntime firstRuntime = new("first", _ => Snapshot("First"));
        runtimeFactory.CreateRuntime = path => path == "bad" ? throw new UnauthorizedAccessException("synthetic denied") : firstRuntime;
        _host = CreateHost(runtimeFactory, new FakePicker(null));
        using DockContent unrelatedDocument = new() { Text = "Normal log tab" };
        unrelatedDocument.Show(_host.DockPanel, DockState.Document);
        OpenAndDrainInitialRefresh(_host, "first");
        MinecraftWorkspaceReadOnlyDocument current = _host.Controller.ActiveDocument!;

        Task<bool> failedOpen = _host.Controller.OpenWorkspaceAsync("bad");
        _host.Dispatcher.RunNextBackground();
        _host.Dispatcher.RunNextUi();

        Assert.Multiple(() =>
        {
            Assert.That(failedOpen.GetAwaiter().GetResult(), Is.False);
            Assert.That(_host.Controller.ActiveDocument, Is.SameAs(current));
            Assert.That(current.IsDisposed, Is.False);
            Assert.That(unrelatedDocument.IsDisposed, Is.False);
            Assert.That(unrelatedDocument.DockState, Is.EqualTo(DockState.Document));
            Assert.That(firstRuntime.DisposeCount, Is.Zero);
            Assert.That(_host.Errors, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public void Valid_workspace_is_document_and_replacement_disposes_previous_session_once ()
    {
        FakeRuntimeFactory runtimeFactory = new();
        FakeRuntime firstRuntime = new("first", _ => Snapshot("First"));
        FakeRuntime secondRuntime = new("second", _ => Snapshot("Second"));
        runtimeFactory.CreateRuntime = path => path == "first" ? firstRuntime : secondRuntime;
        _host = CreateHost(runtimeFactory, new FakePicker("second"));
        OpenAndDrainInitialRefresh(_host, "first");
        MinecraftWorkspaceReadOnlyDocument firstDocument = _host.Controller.ActiveDocument!;

        bool openedFromPicker = OpenSelectedAndDrain(_host);
        _host.Dispatcher.DrainBackgroundAndUi();
        MinecraftWorkspaceReadOnlyDocument secondDocument = _host.Controller.ActiveDocument!;

        Assert.Multiple(() =>
        {
            Assert.That(openedFromPicker, Is.True);
            Assert.That(secondDocument, Is.Not.SameAs(firstDocument));
            Assert.That(secondDocument.ShowHint, Is.EqualTo(DockState.Document));
            Assert.That(secondDocument.DockState, Is.EqualTo(DockState.Document));
            Assert.That(firstDocument.IsDisposed, Is.True);
            Assert.That(firstRuntime.DisposeCount, Is.EqualTo(1));
            Assert.That(_host.TriggerFactory.Triggers[0].DisposeCount, Is.EqualTo(1));
            Assert.That(secondRuntime.DisposeCount, Is.Zero);
            Assert.That(runtimeFactory.CreatedPaths, Is.EqualTo(new[] { "first", "second" }));
            Assert.That(_host.Errors, Is.Empty);
        });
    }

    [Test]
    public void Refresh_runs_off_ui_coalesces_ticks_and_applies_only_on_ui_dispatch ()
    {
        FakeRuntimeFactory runtimeFactory = new();
        FakeRuntime runtime = new("workspace", call => Snapshot($"cycle-{call}"));
        runtimeFactory.CreateRuntime = _ => runtime;
        _host = CreateHost(runtimeFactory, new FakePicker(null));
        OpenWorkspace(_host, "root");
        ManualRefreshTrigger trigger = _host.TriggerFactory.Triggers.Single();
        int uiThread = Environment.CurrentManagedThreadId;
        runtime.OnRefresh = call =>
        {
            if (call == 2)
            {
                trigger.Raise();
                trigger.Raise();
                trigger.Raise();
                trigger.Raise();
            }
        };

        _host.Dispatcher.RunNextBackground();
        _host.Dispatcher.DrainUi();

        Assert.Multiple(() =>
        {
            Assert.That(runtime.RefreshThreadIds[1], Is.Not.EqualTo(uiThread));
            Assert.That(runtime.MaximumConcurrentRefreshes, Is.EqualTo(1));
            Assert.That(_host.Dispatcher.PendingBackground, Is.EqualTo(1));
            Assert.That(_host.Controller.ActiveDocument!.WorkspaceControl.Snapshot.WorkspaceDisplayName, Is.EqualTo("cycle-2"));
        });

        _host.Dispatcher.RunNextBackground();
        _host.Dispatcher.DrainUi();

        Assert.Multiple(() =>
        {
            Assert.That(runtime.RefreshCount, Is.EqualTo(3));
            Assert.That(runtime.MaximumConcurrentRefreshes, Is.EqualTo(1));
            Assert.That(_host.Controller.ActiveDocument!.WorkspaceControl.Snapshot.WorkspaceDisplayName, Is.EqualTo("cycle-3"));
        });
    }

    [Test]
    public void Newer_query_revision_discards_a_stale_snapshot_before_UI_application ()
    {
        MinecraftWorkspaceIngressEvent match = CreateHostIngress("query-file", 1, "newer query match");
        MinecraftWorkspaceTimeline timeline = new("host-query-workspace");
        timeline.AppendBatch([match]);
        MinecraftWorkspaceTimelineFilter filter = new();
        FakeRuntime runtime = new("query-workspace", _ => Snapshot("query-workspace"))
        {
            QuerySnapshotFactory = (_, query) => MinecraftWorkspaceReadOnlyViewPresenter.CreateSnapshot(
                query.SearchText ?? "empty-query",
                filter.Evaluate(timeline.GetOrderedSnapshot(), query))
        };
        FakeRuntimeFactory runtimeFactory = new() { CreateRuntime = _ => runtime };
        _host = CreateHost(runtimeFactory, new FakePicker(null));
        OpenWorkspace(_host, "query-root");
        MinecraftWorkspaceReadOnlyControl control = _host.Controller.ActiveDocument!.WorkspaceControl;
        using ManualResetEventSlim staleRefreshStarted = new();
        using ManualResetEventSlim releaseStaleRefresh = new();
        runtime.OnRefresh = call =>
        {
            if (call == 2)
            {
                staleRefreshStarted.Set();
                Assert.That(releaseStaleRefresh.Wait(TimeSpan.FromSeconds(10)), Is.True);
            }
        };

        Task staleRefresh = Task.Run(_host.Dispatcher.RunNextBackground);
        Assert.That(staleRefreshStarted.Wait(TimeSpan.FromSeconds(10)), Is.True);
        control.SearchTextBox.Text = "older";
        control.SearchTextBox.Text = "newer";
        releaseStaleRefresh.Set();
        Assert.That(staleRefresh.Wait(TimeSpan.FromSeconds(10)), Is.True);

        _host.Dispatcher.RunNextUi();
        Assert.Multiple(() =>
        {
            Assert.That(control.Snapshot.QuerySnapshot.SearchText, Is.Null, "The queued old revision must not replace the applied snapshot.");
            Assert.That(control.SearchTextBox.Text, Is.EqualTo("newer"));
            Assert.That(_host.Dispatcher.PendingBackground, Is.EqualTo(1));
        });

        _host.Dispatcher.RunNextBackground();
        _host.Dispatcher.RunNextUi();

        Assert.Multiple(() =>
        {
            Assert.That(runtime.RefreshQueries, Has.Count.EqualTo(3));
            Assert.That(runtime.RefreshQueries[1].SearchText, Is.Null);
            Assert.That(runtime.RefreshQueries[2].SearchText, Is.EqualTo("newer"));
            Assert.That(control.Snapshot.QuerySnapshot.SearchText, Is.EqualTo("newer"));
            Assert.That(control.Snapshot.Rows.Select(row => row.Identity), Is.EqualTo(new[] { match.IngressSequence }));
        });

        _host.TriggerFactory.Triggers.Single().Raise();
        _host.Dispatcher.RunNextBackground();
        _host.Dispatcher.RunNextUi();
        Assert.That(runtime.RefreshQueries[^1], Is.SameAs(control.Snapshot.QuerySnapshot),
            "Periodic refresh must evaluate the currently applied query snapshot.");
    }

    [Test]
    public void Invalid_regex_and_timeout_states_reach_the_workspace_without_UI_exceptions ()
    {
        string timeoutText = new string('a', 50_000) + "!";
        MinecraftWorkspaceTimeline timeline = new("host-query-workspace");
        timeline.AppendBatch([
            CreateHostIngress("regex-normal", 1, "normal event"),
            CreateHostIngress("regex-timeout", 2, timeoutText)]);
        MinecraftWorkspaceTimelineFilter filter = new();
        FakeRuntime runtime = new("regex-status", _ => Snapshot("regex-status"))
        {
            QuerySnapshotFactory = (_, query) => MinecraftWorkspaceReadOnlyViewPresenter.CreateSnapshot(
                "regex-status",
                filter.Evaluate(timeline.GetOrderedSnapshot(), query))
        };
        _host = CreateHost(new FakeRuntimeFactory { CreateRuntime = _ => runtime }, new FakePicker(null));
        OpenAndDrainInitialRefresh(_host, "regex-status-root");
        MinecraftWorkspaceReadOnlyControl control = _host.Controller.ActiveDocument!.WorkspaceControl;

        control.SearchTextBox.Text = "[";
        control.RegexCheckBox.Checked = true;
        _host.Dispatcher.RunNextBackground();
        _host.Dispatcher.DrainUi();
        Assert.Multiple(() =>
        {
            Assert.That(control.Snapshot.FilterStatus, Is.EqualTo(MinecraftWorkspaceFilterStatus.InvalidRegex));
            Assert.That(control.StatusLabel.Text, Does.Contain("InvalidRegex"));
            Assert.That(control.StatusLabel.Text, Does.Contain("Matched: incomplete"));
            Assert.That(control.Snapshot.Rows, Is.Empty);
            Assert.That(_host.Errors, Is.Empty);
        });

        control.SearchTextBox.Text = "^(a+)+$";
        _host.Dispatcher.RunNextBackground();
        _host.Dispatcher.DrainUi();
        Assert.Multiple(() =>
        {
            Assert.That(control.Snapshot.FilterStatus, Is.EqualTo(MinecraftWorkspaceFilterStatus.RegexTimedOut));
            Assert.That(control.StatusLabel.Text, Does.Contain("RegexTimedOut"));
            Assert.That(control.StatusLabel.Text, Does.Contain("Matched: incomplete"));
            Assert.That(control.Snapshot.MatchedCount, Is.Null);
            Assert.That(control.Snapshot.Rows, Is.Empty);
            Assert.That(_host.Errors, Is.Empty);
        });
    }

    [Test]
    public void Failed_refresh_keeps_last_successful_snapshot ()
    {
        FakeRuntimeFactory runtimeFactory = new();
        MinecraftWorkspaceReadOnlyViewSnapshot initial = Snapshot("last-good");
        FakeRuntime runtime = new("workspace", call => call == 2 ? throw new InvalidOperationException("synthetic refresh failure") : initial);
        runtimeFactory.CreateRuntime = _ => runtime;
        _host = CreateHost(runtimeFactory, new FakePicker(null));
        OpenWorkspace(_host, "root");
        MinecraftWorkspaceReadOnlyDocument document = _host.Controller.ActiveDocument!;

        _host.Dispatcher.RunNextBackground();
        _host.Dispatcher.DrainUi();

        Assert.Multiple(() =>
        {
            Assert.That(document.WorkspaceControl.Snapshot, Is.SameAs(initial));
            Assert.That(runtime.DisposeCount, Is.Zero);
            Assert.That(_host.Errors, Is.Empty);
        });
    }

    [Test]
    public void Queued_snapshot_is_ignored_after_replacement_or_document_close ()
    {
        FakeRuntimeFactory runtimeFactory = new();
        FakeRuntime oldRuntime = new("old", call => Snapshot(call == 1 ? "old-initial" : "old-stale"));
        FakeRuntime newRuntime = new("new", _ => Snapshot("new-initial"));
        runtimeFactory.CreateRuntime = path => path == "old" ? oldRuntime : newRuntime;
        _host = CreateHost(runtimeFactory, new FakePicker(null));
        OpenWorkspace(_host, "old");
        MinecraftWorkspaceReadOnlyDocument oldDocument = _host.Controller.ActiveDocument!;

        _host.Dispatcher.RunNextBackground();
        Task<bool> replacement = _host.Controller.OpenWorkspaceAsync("new");
        _host.Dispatcher.RunNextBackground();
        _host.Dispatcher.RunLastUi();
        _host.Dispatcher.RunNextUi();

        Assert.That(replacement.GetAwaiter().GetResult(), Is.True);
        Assert.That(oldDocument.WorkspaceControl.Snapshot.WorkspaceDisplayName, Is.EqualTo("old-initial"));
        Assert.That(_host.Controller.ActiveDocument!.WorkspaceControl.Snapshot.WorkspaceDisplayName, Is.EqualTo("new-initial"));

        MinecraftWorkspaceReadOnlyDocument newDocument = _host.Controller.ActiveDocument!;
        _host.Dispatcher.RunNextBackground();
        newDocument.Close();
        _host.Dispatcher.DrainUi();

        Assert.Multiple(() =>
        {
            Assert.That(newDocument.WorkspaceControl.Snapshot.WorkspaceDisplayName, Is.EqualTo("new-initial"));
            Assert.That(_host.Controller.ActiveDocument, Is.Null);
            Assert.That(newRuntime.DisposeCount, Is.EqualTo(1));
        });
    }

    [Test]
    public void Closing_document_and_host_stops_trigger_and_disposes_runtime_idempotently ()
    {
        FakeRuntimeFactory runtimeFactory = new();
        FakeRuntime runtime = new("workspace", _ => Snapshot("workspace"));
        runtimeFactory.CreateRuntime = _ => runtime;
        _host = CreateHost(runtimeFactory, new FakePicker(null));
        OpenAndDrainInitialRefresh(_host, "root");
        MinecraftWorkspaceReadOnlyDocument document = _host.Controller.ActiveDocument!;
        ManualRefreshTrigger trigger = _host.TriggerFactory.Triggers.Single();

        document.Close();
        trigger.Raise();
        _host.Controller.Dispose();
        _host.Controller.Dispose();

        Assert.Multiple(() =>
        {
            Assert.That(_host.Controller.ActiveDocument, Is.Null);
            Assert.That(trigger.IsStarted, Is.False);
            Assert.That(trigger.StopCount, Is.GreaterThanOrEqualTo(1));
            Assert.That(trigger.DisposeCount, Is.EqualTo(1));
            Assert.That(runtime.DisposeCount, Is.EqualTo(1));
            Assert.That(_host.Dispatcher.PendingBackground, Is.Zero);
        });
    }

    [Test]
    public void File_menu_command_uses_picker_host_path_without_changing_normal_log_tabs ()
    {
        Settings settings = new();
        var fontConverter = TypeDescriptor.GetConverter(typeof(Font));
        settings.Preferences.Font = (Font)fontConverter.ConvertFromInvariantString(settings.Preferences.FontString)!;
        Mock<IConfigManager> configManager = new();
        _ = configManager.Setup(item => item.Settings).Returns(settings);
        FakePicker picker = new(null);
        FakeRuntimeFactory runtimeFactory = new();
        FakeRuntime fakeRuntime = new("menu", _ => Snapshot("workspace"));
        runtimeFactory.CreateRuntime = path => path == "bad" ? throw new IOException("synthetic open failure") : fakeRuntime;
        ManualRefreshTriggerFactory triggerFactory = new();
        QueuedHostDispatcher dispatcher = new();
        List<Exception> errors = [];
        CultureInfo previousUiCulture = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
        LogTabWindow window = new(
            [],
            1,
            false,
            configManager.Object,
            null,
            picker,
            runtimeFactory,
            triggerFactory.Create,
            dispatcher,
            errors.Add);
        _ = _host = new TestHost(window, GetDockPanel(window), window.WorkspaceHostController, dispatcher, triggerFactory, errors);

        try
        {
            window.Show();
            Application.DoEvents();
            ITabController tabController = GetTabController(window);
            LogExpert.UI.Controls.LogWindow.LogWindow[] originalTabs = tabController.GetAllWindows().ToArray();
            Assert.That(originalTabs, Is.Empty);
            ToolStripMenuItem command = GetWorkspaceMenuCommand(window);

            picker.Path = null;
            command.PerformClick();
            Assert.That(tabController.GetAllWindows(), Is.EqualTo(originalTabs));

            picker.Path = "bad";
            command.PerformClick();
            dispatcher.RunNextBackground();
            dispatcher.RunNextUi();
            Application.DoEvents();
            Assert.That(tabController.GetAllWindows(), Is.EqualTo(originalTabs));

            picker.Path = "good";
            command.PerformClick();
            dispatcher.RunNextBackground();
            dispatcher.RunNextUi();
            Application.DoEvents();

            Assert.Multiple(() =>
            {
                Assert.That(command.Text, Is.EqualTo("Open Minecraft Workspace…"));
                Assert.That(picker.CallCount, Is.EqualTo(3));
                Assert.That(window.WorkspaceHostController.ActiveDocument, Is.Not.Null);
                Assert.That(runtimeFactory.CreatedPaths, Is.EqualTo(new[] { "bad", "good" }));
                Assert.That(tabController.GetAllWindows(), Is.EqualTo(originalTabs));
                Assert.That(tabController.GetAllWindows(), Is.Empty);
                Assert.That(errors, Has.Count.EqualTo(1));
            });

            window.Close();
            window.Dispose();
            Assert.Multiple(() =>
            {
                Assert.That(window.WorkspaceHostController.ActiveDocument, Is.Null);
                Assert.That(fakeRuntime.DisposeCount, Is.EqualTo(1));
                Assert.That(triggerFactory.Triggers.Single().DisposeCount, Is.EqualTo(1));
            });
        }
        finally
        {
            if (!window.IsDisposed)
            {
                window.Close();
                window.Dispose();
            }

            CultureInfo.CurrentUICulture = previousUiCulture;
        }
    }

    [Test]
    public void Real_pipeline_refreshes_allowlisted_sources_preserves_identity_and_inserts_late_event ()
    {
        string latestPath = CreateFile("logs/latest.log", "[18:41:03] [Render thread/ERROR] synthetic latest initial\n");
        _ = CreateFile("logs/yeezus.log",
            "2099-01-01T10:02:00Z [INFO] [yeezus-core] [Client thread] synthetic selected event\n" +
            "    at synthetic.WorkspaceSelection.run(Stack.java:42)\n" +
            "2099-01-01T10:04:00Z [INFO] [yeezus-core] [Client thread] synthetic framing boundary\n");
        string arbitraryLog = CreateFile("unrelated/custom.log", "[18:41:03] [Render thread/ERROR] must remain hidden\n");
        TrackingWorkspaceRuntimeFactory runtimeFactory = new(
            new MinecraftWorkspaceHostRuntimeFactory(PluginRegistry.PluginRegistry.Instance, 2048));
        _host = CreateHost(runtimeFactory, new FakePicker(_testDirectory));

        Assert.That(OpenSelectedAndDrain(_host), Is.True);
        _host.Dispatcher.DrainBackgroundAndUi();
        MinecraftWorkspaceReadOnlyDocument document = _host.Controller.ActiveDocument!;
        MinecraftWorkspaceReadOnlyControl control = document.WorkspaceControl;
        PumpUntil(_host, () => control.Snapshot.Rows.Any(row => row.Message.Contains("synthetic selected event", StringComparison.Ordinal)));

        MinecraftWorkspaceReadOnlyRow initialLatest = control.Snapshot.Rows.Single(row => row.Message.Contains("synthetic latest initial", StringComparison.Ordinal));
        MinecraftWorkspaceReadOnlyRow selected = control.Snapshot.Rows.Single(row => row.Message.Contains("synthetic selected event", StringComparison.Ordinal));
        long selectedIdentity = selected.Identity;
        EventRef selectedEventRef = selected.Details.EventRef;
        FileRef selectedFileRef = selected.Details.FileRef;
        long[] initialIdentities = control.Snapshot.Rows.Select(row => row.Identity).ToArray();

        File.AppendAllText(latestPath, "[18:41:04] [Render thread/INFO] synthetic latest append\n", new UTF8Encoding(false));
        PumpUntil(_host, () => control.Snapshot.Rows.Any(row => row.Message.Contains("synthetic latest append", StringComparison.Ordinal)));
        Assert.That(control.Snapshot.Rows.Select(row => row.Identity), Does.Contain(initialLatest.Identity));
        Assert.That(control.Snapshot.Rows.Select(row => row.Identity), Does.Contain(selectedIdentity));

        int selectedRow = control.Snapshot.Rows.ToList().FindIndex(row => row.Identity == selectedIdentity);
        control.EventGrid.CurrentCell = control.EventGrid.Rows[selectedRow].Cells[0];
        control.EventGrid.Rows[selectedRow].Selected = true;
        Application.DoEvents();

        long lateTimestamp = DateTimeOffset.Parse("2099-01-01T10:01:00Z", System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeMilliseconds();
        _ = CreateFile("cactusmonitor/sessions/cfm-synthetic-late.jsonl",
            $"{{\"type\":\"CYCLE\",\"sessionId\":\"synthetic-late-session\",\"sequence\":1,\"timestampEpochMillis\":{lateTimestamp}}}\n");
        PumpUntil(_host, () => control.Snapshot.Rows.Any(row => row.Details.RawText.Contains("synthetic-late-session", StringComparison.Ordinal)));

        MinecraftWorkspaceReadOnlyRow late = control.Snapshot.Rows.Single(row => row.Details.RawText.Contains("synthetic-late-session", StringComparison.Ordinal));
        int lateRow = control.Snapshot.Rows.ToList().FindIndex(row => row.Identity == late.Identity);
        int selectedRowAfterLate = control.Snapshot.Rows.ToList().FindIndex(row => row.Identity == selectedIdentity);
        long[] finalIdentities = control.Snapshot.Rows.Select(row => row.Identity).ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(initialIdentities.All(identity => finalIdentities.Contains(identity)), Is.True);
            Assert.That(finalIdentities.Distinct().Count(), Is.EqualTo(finalIdentities.Length));
            Assert.That(control.Snapshot.Rows.Any(row => row.Details.PhysicalPath == arbitraryLog), Is.False);
            Assert.That(control.Snapshot.Rows.Count(row => row.Details.PhysicalPath == latestPath), Is.GreaterThanOrEqualTo(2));
            Assert.That(control.Snapshot.Rows.Any(row => row.Details.PhysicalPath.EndsWith("yeezus.log", StringComparison.OrdinalIgnoreCase)), Is.True);
            Assert.That(control.Snapshot.Rows.Any(row => row.Details.PhysicalPath.EndsWith("cfm-synthetic-late.jsonl", StringComparison.OrdinalIgnoreCase)), Is.True);
            Assert.That(late.IsLate, Is.True);
            Assert.That(lateRow, Is.LessThan(selectedRowAfterLate));
            Assert.That(control.SelectedIdentity, Is.EqualTo(selectedIdentity));
            Assert.That(control.SelectedDetails!.EventRef, Is.SameAs(selectedEventRef));
            Assert.That(control.SelectedDetails.FileRef, Is.SameAs(selectedFileRef));
            Assert.That(control.SelectedEntry!.Identity, Is.EqualTo(selectedIdentity));
            Assert.That(document.DockState, Is.EqualTo(DockState.Document));
        });

        int visibleCount = control.Snapshot.Rows.Count;
        ManualRefreshTrigger trigger = _host.TriggerFactory.Triggers.Single();
        document.Close();
        File.AppendAllText(latestPath, "[18:41:05] [Render thread/INFO] after document close\n", new UTF8Encoding(false));
        int pendingBeforeTrigger = _host.Dispatcher.PendingBackground;
        trigger.Raise();

        Assert.Multiple(() =>
        {
            Assert.That(_host.Controller.ActiveDocument, Is.Null);
            Assert.That(trigger.DisposeCount, Is.EqualTo(1));
            Assert.That(_host.Dispatcher.PendingBackground, Is.EqualTo(pendingBeforeTrigger));
            Assert.That(control.Snapshot.Rows, Has.Count.EqualTo(visibleCount));
            Assert.Throws<ObjectDisposedException>(() => runtimeFactory.LastRuntime!.Coordinator.Reconcile());
        });

        Assert.That(OpenSelectedAndDrain(_host), Is.True);
        _host.Dispatcher.DrainBackgroundAndUi();
        _host.Controller.Dispose();
        Assert.Throws<ObjectDisposedException>(() => runtimeFactory.LastRuntime!.Coordinator.Reconcile());
    }

    [Test]
    public void Real_pipeline_applies_the_exact_UI_query_to_YEE50_and_keeps_matching_late_rows_selected ()
    {
        string latestPath = CreateFile("logs/latest.log", "[18:41:03] [Render thread/ERROR] synthetic latest initial\n");
        _ = CreateFile("logs/yeezus.log",
            "2099-01-01T10:02:00Z [INFO] [yeezus-core] [Client thread] synthetic yeezus initial\n" +
            "2099-01-01T10:04:00Z [INFO] [yeezus-core] [Client thread] synthetic timeline watermark\n");
        string initialCfmPath = CreateFile(
            "cactusmonitor/sessions/cfm-initial.jsonl.part",
            $"{{\"type\":\"CYCLE\",\"sessionId\":\"synthetic-initial\",\"sequence\":1,\"timestampEpochMillis\":{Utc("2099-01-01T10:03:00Z").ToUnixTimeMilliseconds()}}}\n");
        TrackingWorkspaceRuntimeFactory runtimeFactory = new(
            new MinecraftWorkspaceHostRuntimeFactory(PluginRegistry.PluginRegistry.Instance, 2048));
        _host = CreateHost(runtimeFactory, new FakePicker(_testDirectory));

        Assert.That(OpenSelectedAndDrain(_host), Is.True);
        _host.Dispatcher.DrainBackgroundAndUi();
        MinecraftWorkspaceReadOnlyDocument document = _host.Controller.ActiveDocument!;
        MinecraftWorkspaceReadOnlyControl control = document.WorkspaceControl;
        MinecraftWorkspaceTimelineFilterQuery? lastUiQuery = null;
        List<MinecraftWorkspaceTimelineFilterQuery> emittedQueries = [];
        control.FilterQueryChanged += (_, args) =>
        {
            MinecraftWorkspaceTimelineFilterQuery query = args.Query;
            lastUiQuery = query;
            emittedQueries.Add(query);
        };
        PumpUntil(_host, () => control.Snapshot.Rows.Any(row => row.Details.RawText.Contains("synthetic-initial", StringComparison.Ordinal)));

        int unknownSourceIndex = control.SourceFacetList.Items
            .Cast<MinecraftWorkspaceFacetEditorItem<MinecraftWorkspaceFacetValue<string>>>()
            .ToList()
            .FindIndex(item => item.Value.IsUnknown);
        int yeezusSourceIndex = control.SourceFacetList.Items
            .Cast<MinecraftWorkspaceFacetEditorItem<MinecraftWorkspaceFacetValue<string>>>()
            .ToList()
            .FindIndex(item => !item.Value.IsUnknown && item.Value.Value == "Yeezus");
        int cactusMonitorIndex = control.ComponentFacetList.Items
            .Cast<MinecraftWorkspaceFacetEditorItem<MinecraftWorkspaceFacetValue<string>>>()
            .ToList()
            .FindIndex(item => !item.Value.IsUnknown && item.Value.Value == "CactusMonitor");
        control.SourceFacetList.SetItemChecked(unknownSourceIndex, true);
        control.ComponentFacetList.SetItemChecked(cactusMonitorIndex, true);
        PumpUntil(_host, () => ReferenceEquals(control.Snapshot.QuerySnapshot, lastUiQuery));
        Assert.That(control.Snapshot.Rows, Is.Empty, "Unknown source AND CactusMonitor should not match the latest.log event.");

        control.SourceFacetList.SetItemChecked(yeezusSourceIndex, true);
        PumpUntil(_host, () => ReferenceEquals(control.Snapshot.QuerySnapshot, lastUiQuery));
        MinecraftWorkspaceReadOnlyRow selected = control.Snapshot.Rows.Single(row => row.Details.RawText.Contains("synthetic-initial", StringComparison.Ordinal));
        long selectedIdentity = selected.Identity;
        EventRef selectedEventRef = selected.Details.EventRef;
        FileRef selectedFileRef = selected.Details.FileRef;
        MinecraftWorkspaceTimelineFilterQuery appliedQuery = lastUiQuery!;
        int selectedRowBeforeLiveUpdates = control.Snapshot.Rows.ToList().FindIndex(row => row.Identity == selectedIdentity);
        control.EventGrid.CurrentCell = control.EventGrid.Rows[selectedRowBeforeLiveUpdates].Cells[0];
        control.EventGrid.Rows[selectedRowBeforeLiveUpdates].Selected = true;
        Application.DoEvents();

        long matchingAppendTimestamp = Utc("2099-01-01T10:05:00Z").ToUnixTimeMilliseconds();
        File.AppendAllText(
            initialCfmPath,
            $"{{\"type\":\"CYCLE\",\"sessionId\":\"synthetic-initial\",\"sequence\":2,\"timestampEpochMillis\":{matchingAppendTimestamp}}}\n",
            new UTF8Encoding(false));
        File.AppendAllText(latestPath, "[18:41:04] [Render thread/INFO] synthetic nonmatching live append\n", new UTF8Encoding(false));
        PumpUntil(_host, () => control.Snapshot.Rows.Any(row => row.Details.RawText.Contains("\"sequence\":2", StringComparison.Ordinal)));

        int previousFileFacetCount = control.FileFacetList.Items.Count;
        string latePath = CreateFile(
            "cactusmonitor/sessions/cfm-late.jsonl",
            $"{{\"type\":\"CYCLE\",\"sessionId\":\"synthetic-late\",\"sequence\":1,\"timestampEpochMillis\":{Utc("2099-01-01T10:01:00Z").ToUnixTimeMilliseconds()}}}\n");
        PumpUntil(_host, () => control.Snapshot.Rows.Any(row => row.Details.RawText.Contains("synthetic-late", StringComparison.Ordinal)) &&
            control.FileFacetList.Items.Count > previousFileFacetCount);
        MinecraftWorkspaceReadOnlyRow late = control.Snapshot.Rows.Single(row => row.Details.RawText.Contains("synthetic-late", StringComparison.Ordinal));
        int lateRowIndex = control.Snapshot.Rows.ToList().FindIndex(row => row.Identity == late.Identity);
        int selectedRowIndex = control.Snapshot.Rows.ToList().FindIndex(row => row.Identity == selectedIdentity);
        string lateFileId = runtimeFactory.LastRuntime!.Discovery.Rescan().Files.Single(source => source.FullPath == latePath).FileId;
        MinecraftWorkspaceFacetEditorItem<string> lateFileFacet = control.FileFacetList.Items
            .Cast<MinecraftWorkspaceFacetEditorItem<string>>()
            .Single(item => item.Value == lateFileId);
        int lateFacetIndex = control.FileFacetList.Items.IndexOf(lateFileFacet);

        Assert.Multiple(() =>
        {
            Assert.That(emittedQueries, Has.Count.EqualTo(3));
            Assert.That(appliedQuery.Sources, Has.Count.EqualTo(2));
            Assert.That(appliedQuery.Sources.Any(value => value.IsUnknown), Is.True);
            Assert.That(appliedQuery.Sources.Any(value => !value.IsUnknown && value.Value == "Yeezus"), Is.True);
            Assert.That(appliedQuery.Components, Is.EqualTo(new[] { MinecraftWorkspaceFacetValues.Known("CactusMonitor") }));
            Assert.That(control.Snapshot.QuerySnapshot, Is.SameAs(lastUiQuery));
            Assert.That(control.Snapshot.TotalLoadedCount, Is.GreaterThanOrEqualTo(6), "The matching append and nonmatching latest.log append must both be ingested.");
            Assert.That(control.Snapshot.Rows.Any(row => row.Details.RawText.Contains("synthetic nonmatching live append", StringComparison.Ordinal)), Is.False);
            Assert.That(late.IsLate, Is.True);
            Assert.That(lateRowIndex, Is.LessThan(selectedRowIndex));
            Assert.That(lateFileFacet.Bucket, Is.SameAs(lateFileFacet.DisplayItem.Bucket));
            Assert.That(lateFileFacet.DisplayItem.Bucket.TotalCount, Is.EqualTo(1));
            Assert.That(control.FileFacetList.GetItemChecked(lateFacetIndex), Is.False);
            Assert.That(control.SelectedIdentity, Is.EqualTo(selectedIdentity));
            Assert.That(control.SelectedDetails!.EventRef, Is.SameAs(selectedEventRef));
            Assert.That(control.SelectedDetails.FileRef, Is.SameAs(selectedFileRef));
            Assert.That(control.SelectedEntry!.Identity, Is.EqualTo(selectedIdentity));
            Assert.That(control.Snapshot.Rows.Select(row => row.Identity).Distinct().Count(), Is.EqualTo(control.Snapshot.Rows.Count));
            Assert.That(control.Snapshot.Rows.Any(row => row.Details.PhysicalPath == latestPath), Is.False);
        });

        control.ClearFiltersButton.PerformClick();
        MinecraftWorkspaceTimelineFilterQuery clearQuery = lastUiQuery!;
        PumpUntil(_host, () => ReferenceEquals(control.Snapshot.QuerySnapshot, clearQuery) &&
            control.Snapshot.Rows.Count == control.Snapshot.TotalLoadedCount);
        Assert.Multiple(() =>
        {
            Assert.That(clearQuery.SearchText, Is.Null);
            Assert.That(clearQuery.Sources, Is.Empty);
            Assert.That(clearQuery.Components, Is.Empty);
            Assert.That(control.Snapshot.Rows.Any(row => row.Details.PhysicalPath == latestPath), Is.True);
            Assert.That(control.SelectedIdentity, Is.EqualTo(selectedIdentity));
        });

        document.Close();
        Assert.That(_host.Controller.ActiveDocument, Is.Null);
    }

    private TestHost CreateHost (
        IMinecraftWorkspaceHostRuntimeFactory runtimeFactory,
        FakePicker picker)
    {
        Form form = new()
        {
            ClientSize = new Size(1000, 700),
            StartPosition = FormStartPosition.Manual,
            Location = Point.Empty
        };
        DockPanel dockPanel = new()
        {
            Dock = DockStyle.Fill,
            DocumentStyle = DocumentStyle.DockingWindow,
            Theme = new VS2015LightTheme()
        };
        form.Controls.Add(dockPanel);
        form.Show();
        Application.DoEvents();
        ManualRefreshTriggerFactory triggerFactory = new();
        QueuedHostDispatcher dispatcher = new();
        List<Exception> errors = [];
        MinecraftWorkspaceHostController controller = new(
            form,
            dockPanel,
            picker,
            runtimeFactory,
            triggerFactory.Create,
            dispatcher,
            errors.Add);
        return _host = new TestHost(form, dockPanel, controller, dispatcher, triggerFactory, errors);
    }

    private static void OpenWorkspace (TestHost host, string path)
    {
        Task<bool> opened = host.Controller.OpenWorkspaceAsync(path);
        host.Dispatcher.RunNextBackground();
        host.Dispatcher.RunNextUi();
        Assert.That(opened.GetAwaiter().GetResult(), Is.True);
    }

    private static void OpenAndDrainInitialRefresh (TestHost host, string path)
    {
        OpenWorkspace(host, path);
        host.Dispatcher.DrainBackgroundAndUi();
    }

    private static bool OpenSelectedAndDrain (TestHost host)
    {
        Task<bool> opened = host.Controller.OpenSelectedWorkspaceAsync();
        if (host.Dispatcher.PendingBackground == 0)
        {
            return opened.GetAwaiter().GetResult();
        }

        host.Dispatcher.RunNextBackground();
        host.Dispatcher.RunNextUi();
        return opened.GetAwaiter().GetResult();
    }

    private static void PumpUntil (TestHost host, Func<bool> condition)
    {
        Stopwatch timeout = Stopwatch.StartNew();
        while (!condition() && timeout.Elapsed < TimeSpan.FromSeconds(10))
        {
            host.TriggerFactory.Triggers.LastOrDefault()?.Raise();
            if (host.Dispatcher.PendingBackground > 0)
            {
                host.Dispatcher.RunNextBackground();
            }

            host.Dispatcher.DrainUi();
            Application.DoEvents();
            Thread.Sleep(10);
        }

        Assert.That(condition(), Is.True,
            $"The real reader/session path did not publish the expected synthetic log event. Rows={host.Controller.ActiveDocument?.WorkspaceControl.Snapshot.Rows.Count}, " +
            $"Loaded={host.Controller.ActiveDocument?.WorkspaceControl.Snapshot.TotalLoadedCount}, " +
            $"Query={host.Controller.ActiveDocument?.WorkspaceControl.Snapshot.QuerySnapshot.SearchText}");
    }

    private string CreateFile (string relativePath, string contents)
    {
        string fullPath = Path.Join(_testDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
        _ = Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, contents, new UTF8Encoding(false));
        return fullPath;
    }

    private static DateTimeOffset Utc (string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    private static MinecraftWorkspaceReadOnlyViewSnapshot Snapshot (string displayName)
    {
        MinecraftWorkspaceTimeline timeline = new("test-workspace");
        MinecraftWorkspaceTimelineFilterResult result = new MinecraftWorkspaceTimelineFilter().Evaluate(
            timeline.GetOrderedSnapshot(),
            new MinecraftWorkspaceTimelineFilterQuery());
        return MinecraftWorkspaceReadOnlyViewPresenter.CreateSnapshot(displayName, result);
    }

    private static MinecraftWorkspaceIngressEvent CreateHostIngress (string fileId, long sequence, string message)
    {
        DateTimeOffset timestamp = DateTimeOffset.Parse("2030-01-01T10:00:00Z", CultureInfo.InvariantCulture);
        FileRef file = new(fileId, $"F:/synthetic/{fileId}.log", 1);
        EventRef eventRef = new(file, sequence, sequence * 100, sequence * 100 + 50, sequence, sequence);
        NormalizedLogEvent normalized = new(
            eventRef,
            Attribution.Unknown<string>(),
            Attribution.Unknown<string>(),
            Attribution.Unknown<LogLevel>(),
            null,
            Attribution.Unknown<string>(),
            EventTimestamp.FromSource(timestamp, timestamp.ToString("O", CultureInfo.InvariantCulture)),
            EventParseStatus.Parsed,
            message,
            message);
        return new MinecraftWorkspaceIngressEvent(
            sequence,
            "host-query-workspace",
            fileId,
            fileId,
            MinecraftSourceSegmentRole.Primary,
            normalized,
            timestamp);
    }

    private sealed class TestHost (
        Form form,
        DockPanel dockPanel,
        MinecraftWorkspaceHostController controller,
        QueuedHostDispatcher dispatcher,
        ManualRefreshTriggerFactory triggerFactory,
        List<Exception> errors) : IDisposable
    {
        public Form Form { get; } = form;

        public DockPanel DockPanel { get; } = dockPanel;

        public MinecraftWorkspaceHostController Controller { get; } = controller;

        public QueuedHostDispatcher Dispatcher { get; } = dispatcher;

        public ManualRefreshTriggerFactory TriggerFactory { get; } = triggerFactory;

        public List<Exception> Errors { get; } = errors;

        public void Dispose ()
        {
            Controller.Dispose();
            Form.Close();
            Form.Dispose();
        }
    }

    private sealed class FakePicker (string? path) : IMinecraftWorkspaceFolderPicker
    {
        public string? Path { get; set; } = path;

        public int CallCount { get; private set; }

        public string? PickFolder (IWin32Window owner)
        {
            CallCount++;
            return Path;
        }
    }

    private sealed class FakeRuntimeFactory : IMinecraftWorkspaceHostRuntimeFactory
    {
        public Func<string, FakeRuntime> CreateRuntime { get; set; } = _ => new FakeRuntime("fake", _ => Snapshot("fake"));

        public List<string> CreatedPaths { get; } = [];

        public int CreateCount => CreatedPaths.Count;

        public IMinecraftWorkspaceHostRuntime Create (string rootPath)
        {
            CreatedPaths.Add(rootPath);
            return CreateRuntime(rootPath);
        }
    }

    private sealed class TrackingWorkspaceRuntimeFactory (MinecraftWorkspaceHostRuntimeFactory inner) : IMinecraftWorkspaceHostRuntimeFactory
    {
        public List<MinecraftWorkspaceHostRuntime> CreatedRuntimes { get; } = [];

        public MinecraftWorkspaceHostRuntime? LastRuntime { get; private set; }

        public IMinecraftWorkspaceHostRuntime Create (string rootPath)
        {
            LastRuntime = (MinecraftWorkspaceHostRuntime)inner.Create(rootPath);
            CreatedRuntimes.Add(LastRuntime);
            return LastRuntime;
        }
    }

    private sealed class FakeRuntime (string name, Func<int, MinecraftWorkspaceReadOnlyViewSnapshot> snapshotFactory) : IMinecraftWorkspaceHostRuntime
    {
        private int _activeRefreshes;
        private int _refreshCount;
        private int _disposeCount;
        private int _maximumConcurrentRefreshes;

        public string Name { get; } = name;

        public Action<int>? OnRefresh { get; set; }

        public int RefreshCount => Volatile.Read(ref _refreshCount);

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public int MaximumConcurrentRefreshes => Volatile.Read(ref _maximumConcurrentRefreshes);

        public List<int> RefreshThreadIds { get; } = [];

        public Func<int, MinecraftWorkspaceTimelineFilterQuery, MinecraftWorkspaceReadOnlyViewSnapshot>? QuerySnapshotFactory { get; set; }

        public List<MinecraftWorkspaceTimelineFilterQuery> RefreshQueries { get; } = [];

        public MinecraftWorkspaceReadOnlyViewSnapshot Refresh (MinecraftWorkspaceTimelineFilterQuery query)
        {
            int active = Interlocked.Increment(ref _activeRefreshes);
            int previousMaximum;
            do
            {
                previousMaximum = Volatile.Read(ref _maximumConcurrentRefreshes);
            }
            while (active > previousMaximum && Interlocked.CompareExchange(ref _maximumConcurrentRefreshes, active, previousMaximum) != previousMaximum);

            try
            {
                int call = Interlocked.Increment(ref _refreshCount);
                lock (RefreshThreadIds)
                {
                    RefreshThreadIds.Add(Environment.CurrentManagedThreadId);
                    RefreshQueries.Add(query);
                }

                OnRefresh?.Invoke(call);
                return QuerySnapshotFactory?.Invoke(call, query) ?? snapshotFactory(call);
            }
            finally
            {
                _ = Interlocked.Decrement(ref _activeRefreshes);
            }
        }

        public void Dispose () => _ = Interlocked.Increment(ref _disposeCount);
    }

    private sealed class ManualRefreshTriggerFactory
    {
        public List<ManualRefreshTrigger> Triggers { get; } = [];

        public IMinecraftWorkspaceRefreshTrigger Create ()
        {
            ManualRefreshTrigger trigger = new();
            Triggers.Add(trigger);
            return trigger;
        }
    }

    private sealed class ManualRefreshTrigger : IMinecraftWorkspaceRefreshTrigger
    {
        private EventHandler? _tick;

        public event EventHandler? Tick
        {
            add => _tick += value;
            remove => _tick -= value;
        }

        public bool IsStarted { get; private set; }

        public int StopCount { get; private set; }

        public int DisposeCount { get; private set; }

        public void Start () => IsStarted = true;

        public void StopTrigger ()
        {
            IsStarted = false;
            StopCount++;
        }

        public void Raise ()
        {
            if (IsStarted)
            {
                _tick?.Invoke(this, EventArgs.Empty);
            }
        }

        public void Dispose () => DisposeCount++;
    }

    private sealed class QueuedHostDispatcher : IMinecraftWorkspaceHostDispatcher
    {
        private readonly object _gate = new();
        private readonly Queue<Action> _background = new();
        private readonly List<Action> _ui = [];

        public int PendingBackground
        {
            get
            {
                lock (_gate)
                {
                    return _background.Count;
                }
            }
        }

        public int PendingUi
        {
            get
            {
                lock (_gate)
                {
                    return _ui.Count;
                }
            }
        }

        public void QueueBackgroundWork (Action work)
        {
            lock (_gate)
            {
                _background.Enqueue(work);
            }
        }

        public bool TryPostToUi (Action work)
        {
            lock (_gate)
            {
                _ui.Add(work);
            }

            return true;
        }

        public void RunNextBackground ()
        {
            Action work;
            lock (_gate)
            {
                Assert.That(_background.Count, Is.GreaterThan(0));
                work = _background.Dequeue();
            }

            Exception? failure = null;
            using ManualResetEventSlim completed = new();
            Thread worker = new(() =>
            {
                try
                {
                    work();
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
                finally
                {
                    completed.Set();
                }
            });
            worker.Start();
            Assert.That(completed.Wait(TimeSpan.FromSeconds(20)), Is.True, "Queued host worker did not finish.");
            worker.Join();
            if (failure != null)
            {
                throw failure;
            }
        }

        public void RunNextUi () => RunUiAt(0);

        public void RunLastUi ()
        {
            int index;
            lock (_gate)
            {
                Assert.That(_ui.Count, Is.GreaterThan(0));
                index = _ui.Count - 1;
            }

            RunUiAt(index);
        }

        public void DrainUi ()
        {
            while (PendingUi > 0)
            {
                RunNextUi();
            }
        }

        public void DrainBackgroundAndUi ()
        {
            while (PendingBackground > 0 || PendingUi > 0)
            {
                if (PendingBackground > 0)
                {
                    RunNextBackground();
                }

                DrainUi();
            }
        }

        private void RunUiAt (int index)
        {
            Action work;
            lock (_gate)
            {
                Assert.That(index, Is.InRange(0, _ui.Count - 1));
                work = _ui[index];
                _ui.RemoveAt(index);
            }

            work();
            Application.DoEvents();
        }
    }

    private static ITabController GetTabController (LogTabWindow window)
    {
        FieldInfo field = typeof(LogTabWindow).GetField("_tabController", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (ITabController)field.GetValue(window)!;
    }

    private static ToolStripMenuItem GetWorkspaceMenuCommand (LogTabWindow window)
    {
        FieldInfo field = typeof(LogTabWindow).GetField("openMinecraftWorkspaceToolStripMenuItem", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (ToolStripMenuItem)field.GetValue(window)!;
    }

    private static DockPanel GetDockPanel (LogTabWindow window)
    {
        FieldInfo field = typeof(LogTabWindow).GetField("dockPanel", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (DockPanel)field.GetValue(window)!;
    }
}
