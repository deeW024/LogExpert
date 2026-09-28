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
using LogExpert.UI.Controls.LogWindow;
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
        Settings settings = new();
        Mock<IConfigManager> config = CreateConfig(settings);
        _host = CreateHost(runtimeFactory, new FakePicker(null), configManager: config.Object);
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
            Assert.That(settings.RecentMinecraftWorkspaceRoots, Is.Empty);
            Assert.That(settings.FileHistoryList, Is.Empty);
        });
        config.Verify(manager => manager.Save(SettingsFlags.Settings), Times.Never);
    }

    [Test]
    public void Invalid_candidate_keeps_current_workspace_and_existing_dock_document ()
    {
        string firstRoot = Path.Combine(_testDirectory, "first");
        string badRoot = Path.Combine(_testDirectory, "bad");
        _ = Directory.CreateDirectory(firstRoot);
        _ = Directory.CreateDirectory(badRoot);
        FakeRuntimeFactory runtimeFactory = new();
        Settings settings = new();
        Mock<IConfigManager> config = CreateConfig(settings);
        FakeRuntime firstRuntime = new(firstRoot, _ => Snapshot("First"));
        runtimeFactory.CreateRuntime = path => path == badRoot ? throw new UnauthorizedAccessException("synthetic denied") : firstRuntime;
        _host = CreateHost(runtimeFactory, new FakePicker(null), configManager: config.Object);
        using DockContent unrelatedDocument = new() { Text = "Normal log tab" };
        unrelatedDocument.Show(_host.DockPanel, DockState.Document);
        OpenAndDrainInitialRefresh(_host, firstRoot);
        MinecraftWorkspaceReadOnlyDocument current = _host.Controller.ActiveDocument!;

        Task<bool> failedOpen = _host.Controller.OpenWorkspaceAsync(badRoot);
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
            Assert.That(settings.RecentMinecraftWorkspaceRoots, Is.EqualTo(new[] { MinecraftWorkspaceSettingsStore.NormalizeRoot(firstRoot) }));
            Assert.That(settings.FileHistoryList, Is.Empty);
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
    public void Successful_workspace_activation_records_normalized_recent_root_without_using_file_history ()
    {
        Settings settings = new();
        settings.FileHistoryList.Add("ordinary.log");
        Mock<IConfigManager> config = CreateConfig(settings);
        FakeRuntime runtime = new("history", _ => Snapshot("history"));
        FakeRuntimeFactory runtimeFactory = new() { CreateRuntime = _ => runtime };
        _host = CreateHost(runtimeFactory, new FakePicker(null), configManager: config.Object);

        OpenAndDrainInitialRefresh(_host, _testDirectory);

        Assert.Multiple(() =>
        {
            Assert.That(settings.RecentMinecraftWorkspaceRoots, Is.EqualTo(new[] { MinecraftWorkspaceSettingsStore.NormalizeRoot(_testDirectory) }));
            Assert.That(settings.FileHistoryList, Is.EqualTo(new[] { "ordinary.log" }));
            Assert.That(runtimeFactory.CreatedPaths, Is.EqualTo(new[] { _testDirectory }));
        });
        config.Verify(manager => manager.Save(SettingsFlags.Settings), Times.Once);
    }

    [Test]
    public void Persisted_disabled_source_policy_is_loaded_again_after_settings_reload ()
    {
        _ = CreateFile("logs/yeezus.log", "2026-09-28T10:00:00Z [INFO] [yeezus-core] [Client thread] disabled after restart\n");
        string sourceId = new MinecraftSourceDiscovery(new MinecraftWorkspace(_testDirectory))
            .Rescan().Files.Single().SourceId;
        Settings settings = new();
        MinecraftWorkspaceSettingsStore initialStore = new(CreateConfig(settings).Object);
        initialStore.SetSourceEnabled(_testDirectory, sourceId, enabled: false);

        TrackingWorkspaceRuntimeFactory firstFactory = new(new MinecraftWorkspaceHostRuntimeFactory(PluginRegistry.PluginRegistry.Instance, 2048));
        _host = CreateHost(firstFactory, new FakePicker(null), configManager: CreateConfig(settings).Object);
        OpenAndDrainInitialRefresh(_host, _testDirectory);
        Assert.Multiple(() =>
        {
            Assert.That(firstFactory.InitialDisabledSourceIds.Single(), Is.EqualTo(new[] { sourceId }));
            Assert.That(firstFactory.LastRuntime!.Coordinator.IsSourceEnabled(sourceId), Is.False);
            Assert.That(_host.Controller.ActiveDocument!.WorkspaceControl.LogicalSources.Single().Status, Is.EqualTo(MinecraftWorkspaceSourceStatus.Disabled));
            Assert.That(_host.Controller.ActiveDocument.WorkspaceControl.LogicalSources.Single().HasResumeCheckpoint, Is.False);
        });

        Settings reloadedSettings = Newtonsoft.Json.JsonConvert.DeserializeObject<Settings>(Newtonsoft.Json.JsonConvert.SerializeObject(settings))!;
        _host.Dispose();
        _host = null;

        TrackingWorkspaceRuntimeFactory reopenedFactory = new(new MinecraftWorkspaceHostRuntimeFactory(PluginRegistry.PluginRegistry.Instance, 2048));
        _host = CreateHost(reopenedFactory, new FakePicker(null), configManager: CreateConfig(reloadedSettings).Object);
        OpenAndDrainInitialRefresh(_host, _testDirectory);

        Assert.Multiple(() =>
        {
            Assert.That(reopenedFactory.InitialDisabledSourceIds.Single(), Is.EqualTo(new[] { sourceId }));
            Assert.That(reopenedFactory.LastRuntime!.Coordinator.IsSourceEnabled(sourceId), Is.False);
            Assert.That(_host.Controller.ActiveDocument!.WorkspaceControl.LogicalSources.Single().HasResumeCheckpoint, Is.False,
                "Runtime byte checkpoints are intentionally not persisted across restart.");
            Assert.That(reloadedSettings.RecentMinecraftWorkspaceRoots, Is.EqualTo(new[] { MinecraftWorkspaceSettingsStore.NormalizeRoot(_testDirectory) }));
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
    public void Pause_keeps_only_latest_snapshot_counts_matching_unseen_identities_and_resumes_once ()
    {
        List<MinecraftWorkspaceIngressEvent> events = [
            CreateHostIngress("pause-file", 1, "match initial"),
            CreateHostIngress("pause-file", 2, "skip initial")];
        FakeRuntime runtime = new("pause", _ => Snapshot("pause"))
        {
            QuerySnapshotFactory = (call, query) => Snapshot($"cycle-{call}", events.ToArray(), query)
        };
        FakeRuntimeFactory runtimeFactory = new() { CreateRuntime = _ => runtime };
        _host = CreateHost(runtimeFactory, new FakePicker(null));
        OpenAndDrainInitialRefresh(_host, "pause-root");
        MinecraftWorkspaceReadOnlyControl control = _host.Controller.ActiveDocument!.WorkspaceControl;
        control.SearchTextBox.Text = "match";
        _host.Dispatcher.DrainBackgroundAndUi();
        int refreshesBeforePause = runtime.RefreshCount;

        MinecraftWorkspaceReadOnlyViewSnapshot visibleSnapshot = control.Snapshot;
        long selectedIdentity = visibleSnapshot.Rows.Single().Identity;
        control.EventGrid.CurrentCell = control.EventGrid.Rows[0].Cells[0];
        control.EventGrid.Rows[0].Selected = true;
        Application.DoEvents();
        control.PauseButton.PerformClick();

        events.Add(CreateHostIngress("pause-file", 3, "match append one"));
        events.Add(CreateHostIngress("pause-file", 4, "skip append one"));
        _host.TriggerFactory.Triggers.Single().Raise();
        _host.Dispatcher.RunNextBackground();
        _host.Dispatcher.DrainUi();

        Assert.Multiple(() =>
        {
            Assert.That(control.IsPaused, Is.True);
            Assert.That(control.Snapshot, Is.SameAs(visibleSnapshot));
            Assert.That(control.BacklogCount, Is.EqualTo(1));
            Assert.That(control.LiveStateLabel.Text, Does.Contain("1"));
            Assert.That(runtime.RefreshCount, Is.EqualTo(refreshesBeforePause + 1));
        });

        events.Add(CreateHostIngress("pause-file", 5, "match append two"));
        _host.TriggerFactory.Triggers.Single().Raise();
        _host.Dispatcher.RunNextBackground();
        _host.Dispatcher.DrainUi();
        MinecraftWorkspaceReadOnlyViewSnapshot latestPending = Snapshot("expected-latest", events.ToArray(), control.Snapshot.QuerySnapshot);

        Assert.Multiple(() =>
        {
            Assert.That(control.Snapshot, Is.SameAs(visibleSnapshot));
            Assert.That(control.BacklogCount, Is.EqualTo(2), "Backlog is the count of new matching identities, not row-count arithmetic.");
            Assert.That(control.SelectedIdentity, Is.EqualTo(selectedIdentity));
            Assert.That(control.IsFollowEnabled, Is.True, "Pause must not turn Follow off.");
            Assert.That(runtime.RefreshCount, Is.EqualTo(refreshesBeforePause + 2));
        });

        control.PauseButton.PerformClick();

        Assert.Multiple(() =>
        {
            Assert.That(control.IsPaused, Is.False);
            Assert.That(control.BacklogCount, Is.Zero);
            Assert.That(control.Snapshot.Rows.Select(row => row.Identity), Is.EqualTo(new long[] { 1, 3, 5 }));
            Assert.That(control.SelectedIdentity, Is.EqualTo(selectedIdentity));
            Assert.That(control.Snapshot.Rows.Any(row => row.Identity == 4), Is.False);
            Assert.That(runtimeFactory.CreateCount, Is.EqualTo(1), "Resume must not recreate the runtime or readers.");
            Assert.That(runtime.RefreshCount, Is.EqualTo(refreshesBeforePause + 2), "Applying pending state must not invoke a second refresh synchronously.");
            Assert.That(_host.Dispatcher.PendingBackground, Is.EqualTo(1), "Resume schedules a catch-up refresh.");
        });

        _host.Dispatcher.RunNextBackground();
        _host.Dispatcher.DrainUi();
        Assert.That(control.Snapshot.Rows.Select(row => row.Identity), Is.EqualTo(latestPending.Rows.Select(row => row.Identity)));
        Assert.That(runtime.RefreshCount, Is.EqualTo(refreshesBeforePause + 3), "The queued catch-up refresh runs after Resume.");
    }

    [Test]
    public void Real_periodic_trigger_refreshes_and_updates_backlog_while_paused ()
    {
        List<MinecraftWorkspaceIngressEvent> events = [
            CreateHostIngress("pause-timer-file", 1, "match initial"),
            CreateHostIngress("pause-timer-file", 2, "skip initial")];
        object eventsGate = new();
        FakeRuntime runtime = new("pause-timer", _ => Snapshot("pause-timer"))
        {
            QuerySnapshotFactory = (call, query) =>
            {
                lock (eventsGate)
                {
                    return Snapshot($"pause-timer-{call}", events.ToArray(), query);
                }
            }
        };
        _host = CreateHost(
            new FakeRuntimeFactory { CreateRuntime = _ => runtime },
            new FakePicker(null),
            static () => new WinFormsMinecraftWorkspaceRefreshTrigger());
        OpenAndDrainInitialRefresh(_host, "pause-timer-root");
        MinecraftWorkspaceReadOnlyControl control = _host.Controller.ActiveDocument!.WorkspaceControl;
        control.SearchTextBox.Text = "match";
        PumpUntil(_host, () => control.Snapshot.QuerySnapshot.SearchText == "match");
        control.PauseButton.PerformClick();
        MinecraftWorkspaceReadOnlyViewSnapshot visibleSnapshot = control.Snapshot;
        int refreshesBeforeAppend = runtime.RefreshCount;

        lock (eventsGate)
        {
            events.Add(CreateHostIngress("pause-timer-file", 3, "match appended"));
            events.Add(CreateHostIngress("pause-timer-file", 4, "skip appended"));
        }

        PumpUntil(_host, () => control.BacklogCount == 1);

        Assert.Multiple(() =>
        {
            Assert.That(runtime.RefreshCount, Is.GreaterThan(refreshesBeforeAppend), "The periodic trigger continues refreshing while paused.");
            Assert.That(control.IsPaused, Is.True);
            Assert.That(control.Snapshot, Is.SameAs(visibleSnapshot), "The visible snapshot remains frozen.");
            Assert.That(control.BacklogCount, Is.EqualTo(1), "Only the unseen matching identity contributes to backlog.");
        });
    }

    [Test]
    public void Paused_query_revision_applies_once_discards_stale_results_and_keeps_only_latest_query ()
    {
        List<MinecraftWorkspaceIngressEvent> events = [
            CreateHostIngress("query-pause-file", 1, "alpha event"),
            CreateHostIngress("query-pause-file", 2, "beta event"),
            CreateHostIngress("query-pause-file", 3, "gamma event")];
        FakeRuntime runtime = new("paused-query", _ => Snapshot("paused-query"))
        {
            QuerySnapshotFactory = (call, query) => Snapshot($"cycle-{call}", events.ToArray(), query)
        };
        _host = CreateHost(new FakeRuntimeFactory { CreateRuntime = _ => runtime }, new FakePicker(null));
        OpenAndDrainInitialRefresh(_host, "paused-query-root");
        MinecraftWorkspaceReadOnlyControl control = _host.Controller.ActiveDocument!.WorkspaceControl;
        control.PauseButton.PerformClick();

        control.SearchTextBox.Text = "alpha";
        _host.Dispatcher.RunNextBackground();
        control.SearchTextBox.Text = "beta";
        control.SearchTextBox.Text = "gamma";
        _host.Dispatcher.RunNextUi();
        Assert.That(control.Snapshot.QuerySnapshot.SearchText, Is.Null, "The queued alpha result belongs to a stale revision.");

        _host.Dispatcher.RunNextBackground();
        _host.Dispatcher.RunNextUi();
        MinecraftWorkspaceReadOnlyViewSnapshot appliedQueryBaseline = control.Snapshot;
        Assert.Multiple(() =>
        {
            Assert.That(control.IsPaused, Is.True);
            Assert.That(control.BacklogCount, Is.Zero);
            Assert.That(appliedQueryBaseline.QuerySnapshot.SearchText, Is.EqualTo("gamma"));
            Assert.That(appliedQueryBaseline.Rows.Select(row => row.Entry.IngressEvent.Event.Message), Is.EqualTo(new[] { "gamma event" }));
            Assert.That(runtime.RefreshQueries[^1].SearchText, Is.EqualTo("gamma"));
        });

        events.Add(CreateHostIngress("query-pause-file", 4, "gamma later"));
        _host.TriggerFactory.Triggers.Single().Raise();
        _host.Dispatcher.RunNextBackground();
        _host.Dispatcher.RunNextUi();
        Assert.Multiple(() =>
        {
            Assert.That(control.Snapshot, Is.SameAs(appliedQueryBaseline), "A same-revision live result is withheld after the one allowed paused query apply.");
            Assert.That(control.BacklogCount, Is.EqualTo(1));
            Assert.That(control.IsPaused, Is.True);
        });

        control.PauseButton.PerformClick();
        Assert.Multiple(() =>
        {
            Assert.That(control.IsPaused, Is.False);
            Assert.That(control.Snapshot.Rows.Select(row => row.Entry.IngressEvent.Event.Message), Is.EqualTo(new[] { "gamma event", "gamma later" }));
            Assert.That(control.BacklogCount, Is.Zero);
            Assert.That(runtime.RefreshQueries.Any(query => query.SearchText == "beta"), Is.False, "The coalesced rapid edit must run the latest query.");
        });
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
    public void Real_coordinator_timeline_filter_and_workspace_session_keep_pause_backlog_and_follow_anchor_canonical ()
    {
        string latestContents = string.Join(
            Environment.NewLine,
            Enumerable.Range(1, 20).Select(index => $"[18:41:{index:00}] [Render thread/INFO] synthetic latest {index}")) + Environment.NewLine;
        string latestPath = CreateFile("logs/latest.log", latestContents);
        DateTimeOffset firstYeezusTimestamp = Utc("2099-01-01T10:00:00Z");
        StringBuilder yeezusContents = new();
        for (int index = 1; index <= 25; index++)
        {
            string timestamp = firstYeezusTimestamp.AddMinutes(index).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
            yeezusContents.Append(timestamp).Append(" [INFO] [yeezus-core] [Client thread] synthetic initial ").Append(index).AppendLine();
        }

        yeezusContents.Append(firstYeezusTimestamp.AddMinutes(26).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture))
            .AppendLine(" [INFO] [yeezus-core] [Client thread] outside query framing boundary");
        string yeezusPath = CreateFile("logs/yeezus.log", yeezusContents.ToString());
        TrackingWorkspaceRuntimeFactory runtimeFactory = new(
            new MinecraftWorkspaceHostRuntimeFactory(PluginRegistry.PluginRegistry.Instance, 2048));
        _host = CreateHost(runtimeFactory, new FakePicker(_testDirectory));

        OpenAndDrainInitialRefresh(_host, _testDirectory);
        MinecraftWorkspaceReadOnlyControl control = _host.Controller.ActiveDocument!.WorkspaceControl;
        PumpUntil(_host, () => control.Snapshot.Rows.Count >= 45);
        control.SearchTextBox.Text = "synthetic";
        control.TextScopeComboBox.SelectedIndex = (int)MinecraftWorkspaceTextScope.MessageOrRawText;
        PumpUntil(_host, () => control.Snapshot.QuerySnapshot.SearchText == "synthetic" && control.Snapshot.Rows.Count >= 45);
        MinecraftWorkspaceReadOnlyRow initialSelected = control.Snapshot.Rows.Single(row =>
            row.Message.Contains("synthetic initial 10", StringComparison.Ordinal));
        int selectedRow = control.Snapshot.Rows.ToList().FindIndex(row => row.Identity == initialSelected.Identity);
        control.EventGrid.CurrentCell = control.EventGrid.Rows[selectedRow].Cells[0];
        control.EventGrid.Rows[selectedRow].Selected = true;
        Application.DoEvents();
        control.FollowCheckBox.Checked = true;
        Application.DoEvents();
        long selectedIdentity = initialSelected.Identity;
        EventRef selectedEventRef = initialSelected.Details.EventRef;
        FileRef selectedFileRef = initialSelected.Details.FileRef;
        MinecraftWorkspaceReadOnlyViewSnapshot visibleBeforePause = control.Snapshot;
        control.PauseButton.PerformClick();

        string newYeezusRecords = firstYeezusTimestamp.AddMinutes(40).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) +
            " [INFO] [yeezus-core] [Client thread] synthetic pause-match appended\n" +
            firstYeezusTimestamp.AddMinutes(41).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) +
            " [INFO] [yeezus-core] [Client thread] outside query framing boundary\n";
        File.AppendAllText(yeezusPath, newYeezusRecords, new UTF8Encoding(false));
        File.AppendAllText(latestPath, "[18:42:00] [Render thread/INFO] nonmatching live noise" + Environment.NewLine, new UTF8Encoding(false));
        long lateTimestamp = firstYeezusTimestamp.AddMinutes(1).ToUnixTimeMilliseconds();
        _ = CreateFile(
            "cactusmonitor/sessions/cfm-synthetic-pause-late.jsonl",
            $"{{\"type\":\"CYCLE\",\"sessionId\":\"synthetic-pause-late\",\"sequence\":1,\"timestampEpochMillis\":{lateTimestamp}}}" + Environment.NewLine);

        PumpUntil(_host, () => control.BacklogCount == 2);
        Assert.Multiple(() =>
        {
            Assert.That(control.IsPaused, Is.True);
            Assert.That(control.Snapshot, Is.SameAs(visibleBeforePause), "The visible grid snapshot stays frozen while the real runtime refreshes.");
            Assert.That(control.Snapshot.Rows.Select(row => row.Identity), Does.Contain(selectedIdentity));
            Assert.That(control.BacklogCount, Is.EqualTo(2), "The nonmatching live append is excluded from the active-query backlog.");
            Assert.That(control.LiveStateLabel.Text, Does.Contain("2"));
            Assert.That(control.Snapshot.QuerySnapshot.SearchText, Is.EqualTo("synthetic"));
        });

        control.PauseButton.PerformClick();
        Application.DoEvents();
        MinecraftWorkspaceReadOnlyRow[] resumedRows = control.Snapshot.Rows.ToArray();
        MinecraftWorkspaceReadOnlyRow lateRow = resumedRows.Single(row => row.Details.RawText.Contains("synthetic-pause-late", StringComparison.Ordinal));

        Assert.Multiple(() =>
        {
            Assert.That(control.IsPaused, Is.False);
            Assert.That(control.BacklogCount, Is.Zero);
            Assert.That(resumedRows, Has.Length.EqualTo(47));
            Assert.That(lateRow.IsLate, Is.True);
            Assert.That(Array.FindIndex(resumedRows, row => row.Identity == lateRow.Identity), Is.LessThan(Array.FindIndex(resumedRows, row => row.Identity == selectedIdentity)));
            Assert.That(control.SelectedIdentity, Is.EqualTo(selectedIdentity));
            Assert.That(control.SelectedDetails!.EventRef, Is.SameAs(selectedEventRef));
            Assert.That(control.SelectedDetails.FileRef, Is.SameAs(selectedFileRef));
            Assert.That(control.Snapshot.Rows.Select(row => row.Identity).Distinct().Count(), Is.EqualTo(control.Snapshot.Rows.Count));
            Assert.That(control.IsFollowEnabled, Is.True);
            Assert.That(IsAtTail(control.EventGrid), Is.True);
            Assert.That(control.Snapshot.QuerySnapshot.SearchText, Is.EqualTo("synthetic"));
        });

        control.FollowCheckBox.Checked = false;
        control.EventGrid.FirstDisplayedScrollingRowIndex = 10;
        Application.DoEvents();
        long anchorIdentity = control.Snapshot.Rows[control.EventGrid.FirstDisplayedScrollingRowIndex].Identity;
        control.PauseButton.PerformClick();
        long secondLateTimestamp = firstYeezusTimestamp.AddSeconds(30).ToUnixTimeMilliseconds();
        _ = CreateFile(
            "cactusmonitor/sessions/cfm-synthetic-pause-late-earlier.jsonl",
            $"{{\"type\":\"CYCLE\",\"sessionId\":\"synthetic-pause-earlier\",\"sequence\":1,\"timestampEpochMillis\":{secondLateTimestamp}}}" + Environment.NewLine);
        PumpUntil(_host, () => control.BacklogCount == 1);
        control.PauseButton.PerformClick();
        Application.DoEvents();

        Assert.Multiple(() =>
        {
            Assert.That(control.IsFollowEnabled, Is.False);
            Assert.That(control.Snapshot.Rows[control.EventGrid.FirstDisplayedScrollingRowIndex].Identity, Is.EqualTo(anchorIdentity));
            Assert.That(control.SelectedIdentity, Is.EqualTo(selectedIdentity));
            Assert.That(control.Snapshot.Rows.Any(row => row.Details.RawText.Contains("synthetic-pause-earlier", StringComparison.Ordinal)), Is.True);
            Assert.That(control.Snapshot.QuerySnapshot.SearchText, Is.EqualTo("synthetic"));
            Assert.That(runtimeFactory.CreatedRuntimes, Has.Count.EqualTo(1), "Pause and Resume retain the original live runtime.");
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
    public void Paused_pending_snapshot_is_discarded_when_its_workspace_session_is_replaced ()
    {
        FakeRuntime oldRuntime = new("old", call => Snapshot(call <= 2 ? "old-initial" : "old-pending"));
        FakeRuntime newRuntime = new("new", _ => Snapshot("new-initial"));
        FakeRuntimeFactory runtimeFactory = new()
        {
            CreateRuntime = path => path == "old" ? oldRuntime : newRuntime
        };
        _host = CreateHost(runtimeFactory, new FakePicker(null));
        OpenAndDrainInitialRefresh(_host, "old");
        MinecraftWorkspaceReadOnlyDocument oldDocument = _host.Controller.ActiveDocument!;
        MinecraftWorkspaceReadOnlyControl oldControl = oldDocument.WorkspaceControl;
        MinecraftWorkspaceReadOnlyViewSnapshot visibleSnapshot = oldControl.Snapshot;
        oldControl.PauseButton.PerformClick();
        _host.TriggerFactory.Triggers[0].Raise();
        _host.Dispatcher.RunNextBackground();
        _host.Dispatcher.RunNextUi();
        Assert.Multiple(() =>
        {
            Assert.That(oldControl.IsPaused, Is.True);
            Assert.That(oldRuntime.RefreshCount, Is.GreaterThan(2), "The runtime refreshes while presentation is paused.");
            Assert.That(oldControl.Snapshot, Is.SameAs(visibleSnapshot));
        });

        Task<bool> replacement = _host.Controller.OpenWorkspaceAsync("new");
        _host.Dispatcher.RunNextBackground();
        _host.Dispatcher.RunNextUi();
        Assert.That(replacement.GetAwaiter().GetResult(), Is.True);
        _host.Dispatcher.DrainBackgroundAndUi();

        Assert.Multiple(() =>
        {
            Assert.That(oldDocument.IsDisposed, Is.True);
            Assert.That(oldControl.Snapshot, Is.SameAs(visibleSnapshot));
            Assert.That(_host.Controller.ActiveDocument!.WorkspaceControl.Snapshot.WorkspaceDisplayName, Is.EqualTo("new-initial"));
            Assert.That(oldRuntime.DisposeCount, Is.EqualTo(1));
            Assert.That(newRuntime.DisposeCount, Is.Zero);
            Assert.That(_host.Errors, Is.Empty);
        });
    }

    [Test]
    public void Closing_document_and_host_stops_trigger_and_disposes_runtime_idempotently ()
    {
        FakeRuntimeFactory runtimeFactory = new();
        FakeRuntime runtime = new("workspace", call => Snapshot(call <= 2 ? "workspace-initial" : "workspace-pending"));
        runtimeFactory.CreateRuntime = _ => runtime;
        _host = CreateHost(runtimeFactory, new FakePicker(null));
        OpenAndDrainInitialRefresh(_host, "root");
        MinecraftWorkspaceReadOnlyDocument document = _host.Controller.ActiveDocument!;
        MinecraftWorkspaceReadOnlyViewSnapshot visibleSnapshot = document.WorkspaceControl.Snapshot;
        ManualRefreshTrigger trigger = _host.TriggerFactory.Triggers.Single();
        document.WorkspaceControl.PauseButton.PerformClick();
        trigger.Raise();
        _host.Dispatcher.RunNextBackground();
        _host.Dispatcher.RunNextUi();
        Assert.Multiple(() =>
        {
            Assert.That(document.WorkspaceControl.IsPaused, Is.True);
            Assert.That(document.WorkspaceControl.Snapshot, Is.SameAs(visibleSnapshot));
        });

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
            Assert.That(document.WorkspaceControl.Snapshot, Is.SameAs(visibleSnapshot));
            Assert.That(_host.Dispatcher.PendingBackground, Is.Zero);
        });
    }

    [Test]
    public void File_menu_command_uses_picker_host_path_without_changing_normal_log_tabs ()
    {
        string badRoot = Path.Combine(_testDirectory, "bad");
        string goodRoot = Path.Combine(_testDirectory, "good");
        _ = Directory.CreateDirectory(badRoot);
        _ = Directory.CreateDirectory(goodRoot);
        Settings settings = new();
        var fontConverter = TypeDescriptor.GetConverter(typeof(Font));
        settings.Preferences.Font = (Font)fontConverter.ConvertFromInvariantString(settings.Preferences.FontString)!;
        Mock<IConfigManager> configManager = new();
        _ = configManager.Setup(item => item.Settings).Returns(settings);
        FakePicker picker = new(null);
        FakeRuntimeFactory runtimeFactory = new();
        FakeRuntime fakeRuntime = new("menu", _ => Snapshot("workspace"));
        runtimeFactory.CreateRuntime = path => path == badRoot ? throw new IOException("synthetic open failure") : fakeRuntime;
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

            picker.Path = badRoot;
            command.PerformClick();
            dispatcher.RunNextBackground();
            dispatcher.RunNextUi();
            Application.DoEvents();
            Assert.That(tabController.GetAllWindows(), Is.EqualTo(originalTabs));

            picker.Path = goodRoot;
            command.PerformClick();
            dispatcher.RunNextBackground();
            dispatcher.RunNextUi();
            Application.DoEvents();

            Assert.Multiple(() =>
            {
                Assert.That(command.Text, Is.EqualTo("Open Minecraft Workspace…"));
                Assert.That(picker.CallCount, Is.EqualTo(3));
                Assert.That(window.WorkspaceHostController.ActiveDocument, Is.Not.Null);
                Assert.That(runtimeFactory.CreatedPaths, Is.EqualTo(new[] { badRoot, goodRoot }));
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
    public void Recent_workspace_menu_uses_host_open_path_and_remove_leaves_directory_intact ()
    {
        Settings settings = new();
        settings.RecentMinecraftWorkspaceRoots.Add(_testDirectory);
        string missingRoot = Path.Combine(_testDirectory, "missing-workspace");
        settings.RecentMinecraftWorkspaceRoots.Add(missingRoot);
        var fontConverter = TypeDescriptor.GetConverter(typeof(Font));
        settings.Preferences.Font = (Font)fontConverter.ConvertFromInvariantString(settings.Preferences.FontString)!;
        Mock<IConfigManager> config = CreateConfig(settings);
        FakeRuntime runtime = new("recent", _ => Snapshot("recent"));
        string normalizedMissingRoot = MinecraftWorkspaceSettingsStore.NormalizeRoot(missingRoot);
        FakeRuntimeFactory runtimeFactory = new()
        {
            CreateRuntime = path => string.Equals(path, normalizedMissingRoot, StringComparison.OrdinalIgnoreCase)
                ? throw new DirectoryNotFoundException("synthetic missing recent workspace")
                : runtime
        };
        ManualRefreshTriggerFactory triggerFactory = new();
        QueuedHostDispatcher dispatcher = new();
        List<Exception> errors = [];
        LogTabWindow window = new(
            [],
            1,
            false,
            config.Object,
            null,
            new FakePicker(null),
            runtimeFactory,
            triggerFactory.Create,
            dispatcher,
            errors.Add);
        _ = _host = new TestHost(window, GetDockPanel(window), window.WorkspaceHostController, dispatcher, triggerFactory, errors);

        try
        {
            window.Show();
            Application.DoEvents();
            ToolStripMenuItem recentMenu = GetRecentWorkspaceMenu(window);
            ToolStripMenuItem recentItem = recentMenu.DropDownItems
                .Cast<ToolStripMenuItem>()
                .First(item => item.Name == "RecentMinecraftWorkspaceItem");

            recentItem.PerformClick();
            dispatcher.RunNextBackground();
            dispatcher.RunNextUi();
            Application.DoEvents();

            Assert.That(window.WorkspaceHostController.ActiveDocument, Is.Not.Null);
            Assert.That(runtimeFactory.CreatedPaths, Is.EqualTo(new[] { _testDirectory }));
            Assert.That(settings.RecentMinecraftWorkspaceRoots, Is.EqualTo(new[]
            {
                MinecraftWorkspaceSettingsStore.NormalizeRoot(_testDirectory), normalizedMissingRoot
            }));
            Assert.That(errors, Is.Empty);

            MinecraftWorkspaceReadOnlyDocument openedDocument = window.WorkspaceHostController.ActiveDocument!;
            ToolStripMenuItem missingItem = GetRecentWorkspaceMenu(window).DropDownItems
                .Cast<ToolStripMenuItem>()
                .Single(item => item.Name == "RecentMinecraftWorkspaceItem" && item.Tag as string == normalizedMissingRoot);
            missingItem.PerformClick();
            dispatcher.DrainBackgroundAndUi();
            Application.DoEvents();

            Assert.Multiple(() =>
            {
                Assert.That(window.WorkspaceHostController.ActiveDocument, Is.SameAs(openedDocument));
                Assert.That(settings.RecentMinecraftWorkspaceRoots, Does.Contain(normalizedMissingRoot));
                Assert.That(errors, Has.Count.EqualTo(1));
            });

            ToolStripMenuItem removeItem = missingItem.DropDownItems.Cast<ToolStripMenuItem>().Single();
            removeItem.PerformClick();

            Assert.Multiple(() =>
            {
                Assert.That(settings.RecentMinecraftWorkspaceRoots, Is.EqualTo(new[] { MinecraftWorkspaceSettingsStore.NormalizeRoot(_testDirectory) }));
                Assert.That(Directory.Exists(_testDirectory), Is.True);
                Assert.That(Directory.Exists(missingRoot), Is.False);
                Assert.That(settings.FileHistoryList, Is.Empty);
            });
        }
        finally
        {
            if (!window.IsDisposed)
            {
                window.Close();
                window.Dispose();
            }
        }
    }

    [Test]
    public async Task Real_source_toggle_suspends_exactly_resumes_and_rescan_keeps_the_view_state ()
    {
        const string initial = "2026-09-28T10:00:00Z [INFO] [yeezus-core] [Client thread] Y80_MATCH initial";
        const string pending = "2026-09-28T10:00:01Z [ERROR] [yeezus-core] [Client thread] Y80_MATCH pending at disable";
        string yeezusPath = CreateFile("logs/yeezus.log", initial + "\n" + pending + "\n");
        Settings settings = new();
        Mock<IConfigManager> config = CreateConfig(settings);
        TrackingWorkspaceRuntimeFactory runtimeFactory = new(new MinecraftWorkspaceHostRuntimeFactory(PluginRegistry.PluginRegistry.Instance, 2048));
        _host = CreateHost(runtimeFactory, new FakePicker(_testDirectory), configManager: config.Object);

        Assert.That(OpenSelectedAndDrain(_host), Is.True);
        _host.Dispatcher.DrainBackgroundAndUi();
        MinecraftWorkspaceReadOnlyControl control = _host.Controller.ActiveDocument!.WorkspaceControl;
        PumpUntil(_host, () => control.Snapshot.Rows.Any(row => row.Message.Contains("Y80_MATCH initial", StringComparison.Ordinal)));
        control.SearchTextBox.Text = "Y80_MATCH";
        _host.Dispatcher.DrainBackgroundAndUi();
        PumpUntil(_host, () => control.Snapshot.QuerySnapshot.SearchText == "Y80_MATCH");
        MinecraftWorkspaceTimelineFilterQuery activeQuery = control.Snapshot.QuerySnapshot;
        int queryChanges = 0;
        control.FilterQueryChanged += (_, _) => queryChanges++;

        MinecraftWorkspaceReadOnlyRow initialRow = control.Snapshot.Rows.Single(row => row.Message.Contains("Y80_MATCH initial", StringComparison.Ordinal));
        long selectedIdentity = initialRow.Identity;
        EventRef selectedEventRef = initialRow.Details.EventRef;
        FileRef selectedFileRef = initialRow.Details.FileRef;
        int selectedRow = control.Snapshot.Rows.ToList().FindIndex(row => row.Identity == selectedIdentity);
        control.EventGrid.CurrentCell = control.EventGrid.Rows[selectedRow].Cells[0];
        control.EventGrid.Rows[selectedRow].Selected = true;
        Application.DoEvents();
        control.FollowCheckBox.Checked = false;
        control.PauseButton.PerformClick();
        Application.DoEvents();

        MinecraftWorkspaceLogicalSourceSnapshot yeezusSource = control.LogicalSources.Single(source => source.Family == MinecraftSourceFamily.Yeezus);
        int sourceIndex = control.LiveSourceList.Items
            .Cast<MinecraftWorkspaceLogicalSourceEditorItem>()
            .ToList()
            .FindIndex(item => item.Source.SourceId == yeezusSource.SourceId);
        Assert.That(sourceIndex, Is.GreaterThanOrEqualTo(0));
        control.FacetTabs.SelectedTab = control.FacetTabs.TabPages.Cast<TabPage>().Single(page => page.Name == "WorkspaceLiveSourcesTab");
        long sourceGenerationBeforeRapidToggle = runtimeFactory.LastRuntime!.Coordinator.GetRuntimeSources()
            .Single(state => state.Source.SourceId == yeezusSource.SourceId).Generation;
        control.LiveSourceList.SetItemChecked(sourceIndex, false);
        control.LiveSourceList.SetItemChecked(sourceIndex, true);
        _host.Dispatcher.DrainBackgroundAndUi();
        Assert.Multiple(() =>
        {
            Assert.That(runtimeFactory.LastRuntime.Coordinator.IsSourceEnabled(yeezusSource.SourceId), Is.True);
            Assert.That(settings.MinecraftWorkspaceSourcePolicies, Is.Empty);
            Assert.That(runtimeFactory.LastRuntime.Coordinator.GetRuntimeSources()
                .Single(state => state.Source.SourceId == yeezusSource.SourceId).Generation, Is.EqualTo(sourceGenerationBeforeRapidToggle));
            Assert.That(control.Snapshot.QuerySnapshot, Is.SameAs(activeQuery));
            Assert.That(control.SelectedIdentity, Is.EqualTo(selectedIdentity));
            Assert.That(control.IsPaused, Is.True);
        });
        sourceIndex = control.LiveSourceList.Items
            .Cast<MinecraftWorkspaceLogicalSourceEditorItem>()
            .ToList()
            .FindIndex(item => item.Source.SourceId == yeezusSource.SourceId);
        control.LiveSourceList.SetItemChecked(sourceIndex, false);
        _host.Dispatcher.DrainBackgroundAndUi();
        int sourceFacetCount = control.SourceFacetList.Items.Count;

        Assert.Multiple(() =>
        {
            Assert.That(control.LogicalSources.Single(source => source.SourceId == yeezusSource.SourceId).Status, Is.EqualTo(MinecraftWorkspaceSourceStatus.Disabled));
            Assert.That(control.IsPaused, Is.True);
            Assert.That(control.BacklogCount, Is.Zero);
            Assert.That(control.IsFollowEnabled, Is.False);
            Assert.That(control.Snapshot.Rows, Has.Count.EqualTo(1));
            Assert.That(control.Snapshot.QuerySnapshot, Is.SameAs(activeQuery));
            Assert.That(control.SelectedIdentity, Is.EqualTo(selectedIdentity));
            Assert.That(control.SourceFacetList.Items, Has.Count.EqualTo(sourceFacetCount));
            Assert.That(settings.MinecraftWorkspaceSourcePolicies.Single().DisabledSourceIds, Does.Contain(yeezusSource.SourceId));
            Assert.That(runtimeFactory.LastRuntime!.Coordinator.IsSourceEnabled(yeezusSource.SourceId), Is.False);
            Assert.That(queryChanges, Is.Zero);
        });

        await File.AppendAllTextAsync(yeezusPath,
            "2026-09-28T10:00:02Z [INFO] [yeezus-core] [Client thread] Y80_MATCH append-one\n" +
            "2026-09-28T10:00:03Z [INFO] [yeezus-core] [Client thread] Y80_MATCH append-two\n" +
            "2026-09-28T10:00:04Z [INFO] [yeezus-core] [Client thread] Y80_MATCH append-boundary\n",
            new UTF8Encoding(false)).ConfigureAwait(true);
        control.RescanSourcesButton.PerformClick();
        _host.Dispatcher.DrainBackgroundAndUi();

        Assert.Multiple(() =>
        {
            Assert.That(control.Snapshot.Rows, Has.Count.EqualTo(1), "Disabled source append must not enter the timeline before resume.");
            Assert.That(control.BacklogCount, Is.Zero);
            Assert.That(control.Snapshot.QuerySnapshot, Is.SameAs(activeQuery));
            Assert.That(control.LogicalSources.Single(source => source.SourceId == yeezusSource.SourceId).Status, Is.EqualTo(MinecraftWorkspaceSourceStatus.Disabled));
            Assert.That(queryChanges, Is.Zero);
        });

        sourceIndex = control.LiveSourceList.Items
            .Cast<MinecraftWorkspaceLogicalSourceEditorItem>()
            .ToList()
            .FindIndex(item => item.Source.SourceId == yeezusSource.SourceId);
        control.LiveSourceList.SetItemChecked(sourceIndex, true);
        _host.Dispatcher.DrainBackgroundAndUi();
        PumpUntil(_host, () => control.IsPaused && control.BacklogCount == 3);

        Assert.Multiple(() =>
        {
            Assert.That(control.Snapshot.Rows, Has.Count.EqualTo(1), "Pause keeps the presented rows frozen while the source catches up.");
            Assert.That(control.Snapshot.QuerySnapshot.SearchText, Is.EqualTo("Y80_MATCH"));
            Assert.That(control.SelectedIdentity, Is.EqualTo(selectedIdentity));
            Assert.That(control.IsFollowEnabled, Is.False);
            Assert.That(control.IsPaused, Is.True);
            Assert.That(control.BacklogCount, Is.EqualTo(3));
            Assert.That(settings.MinecraftWorkspaceSourcePolicies, Is.Empty);
        });

        control.PauseButton.PerformClick();
        PumpUntil(_host, () => control.Snapshot.Rows.Count == 4);
        control.FacetTabs.SelectedTab = control.FacetTabs.TabPages.Cast<TabPage>().Single(page => page.Name == "WorkspaceLiveSourcesTab");
        long yeezusCreatedGeneration = runtimeFactory.LastRuntime!.Coordinator.GetRuntimeSources()
            .Single(state => state.Source.SourceId == yeezusSource.SourceId).Generation;
        string newCfmPath = CreateFile(
            "cactusmonitor/sessions/cfm-y80-rescan.jsonl",
            $"{{\"type\":\"CYCLE\",\"sessionId\":\"Y80-RESCAN\",\"sequence\":1,\"timestampEpochMillis\":{Utc("2099-01-01T10:05:00Z").ToUnixTimeMilliseconds()}}}\n");
        control.RescanSourcesButton.PerformClick();
        PumpUntil(_host, () => control.LogicalSources.Any(source => source.Family == MinecraftSourceFamily.CactusMonitor) &&
            control.Snapshot.TotalLoadedCount == 5);
        _host.Dispatcher.DrainBackgroundAndUi();

        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(newCfmPath), Is.True);
            Assert.That(control.LogicalSources, Has.Count.EqualTo(2));
            Assert.That(control.Snapshot.QuerySnapshot, Is.SameAs(activeQuery));
            Assert.That(control.Snapshot.TotalLoadedCount, Is.EqualTo(5));
            Assert.That(control.Snapshot.Rows.Count(row => row.Message.Contains("Y80_MATCH", StringComparison.Ordinal)), Is.EqualTo(4));
            Assert.That(control.Snapshot.Rows.Count(row => row.Message.Contains("append-one", StringComparison.Ordinal)), Is.EqualTo(1));
            Assert.That(control.Snapshot.Rows.Count(row => row.Message.Contains("append-two", StringComparison.Ordinal)), Is.EqualTo(1));
            Assert.That(control.Snapshot.Rows.Count(row => row.Message.Contains("pending at disable", StringComparison.Ordinal)), Is.EqualTo(1));
            Assert.That(control.SelectedIdentity, Is.EqualTo(selectedIdentity));
            Assert.That(control.SelectedDetails!.EventRef, Is.SameAs(selectedEventRef));
            Assert.That(control.SelectedDetails.FileRef, Is.SameAs(selectedFileRef));
            Assert.That(control.IsFollowEnabled, Is.False);
            Assert.That(control.IsPaused, Is.False);
            Assert.That(runtimeFactory.LastRuntime.Coordinator.GetRuntimeSources()
                .Single(state => state.Source.SourceId == yeezusSource.SourceId).Generation, Is.EqualTo(yeezusCreatedGeneration));
            Assert.That(queryChanges, Is.Zero);
            Assert.That(_host.Errors, Is.Empty);
        });
    }

    [Test]
    public void Rapid_toggles_for_distinct_logical_sources_are_applied_independently ()
    {
        _ = CreateFile("logs/latest.log", "[18:41:03] [Render thread/ERROR] latest source\n");
        _ = CreateFile(
            "cactusmonitor/sessions/cfm-y80-independent-toggles.jsonl",
            $"{{\"type\":\"CYCLE\",\"sessionId\":\"Y80-TOGGLE\",\"sequence\":1,\"timestampEpochMillis\":{Utc("2099-01-01T10:05:00Z").ToUnixTimeMilliseconds()}}}\n");
        Settings settings = new();
        Mock<IConfigManager> config = CreateConfig(settings);
        TrackingWorkspaceRuntimeFactory runtimeFactory = new(new MinecraftWorkspaceHostRuntimeFactory(PluginRegistry.PluginRegistry.Instance, 2048));
        _host = CreateHost(runtimeFactory, new FakePicker(_testDirectory), configManager: config.Object);

        Assert.That(OpenSelectedAndDrain(_host), Is.True);
        _host.Dispatcher.DrainBackgroundAndUi();
        MinecraftWorkspaceReadOnlyControl control = _host.Controller.ActiveDocument!.WorkspaceControl;
        PumpUntil(_host, () => control.Snapshot.TotalLoadedCount == 2);
        IReadOnlyList<MinecraftWorkspaceLogicalSourceSnapshot> sources = control.LogicalSources;
        string[] sourceIds = sources.Select(source => source.SourceId).ToArray();
        Assert.That(sourceIds, Has.Length.EqualTo(2));
        MinecraftWorkspaceReadOnlyViewSnapshot initialSnapshot = control.Snapshot;
        int sourceFacetCount = control.SourceFacetList.Items.Count;

        control.FacetTabs.SelectedTab = control.FacetTabs.TabPages.Cast<TabPage>()
            .Single(page => page.Name == "WorkspaceLiveSourcesTab");
        int[] sourceIndexes = control.LiveSourceList.Items
            .Cast<MinecraftWorkspaceLogicalSourceEditorItem>()
            .Select((item, index) => (item, index))
            .Where(pair => sourceIds.Contains(pair.item.Source.SourceId, StringComparer.Ordinal))
            .Select(pair => pair.index)
            .ToArray();
        Assert.That(sourceIndexes, Has.Length.EqualTo(2));

        control.LiveSourceList.SetItemChecked(sourceIndexes[0], false);
        control.LiveSourceList.SetItemChecked(sourceIndexes[1], false);
        _host.Dispatcher.DrainBackgroundAndUi();

        Assert.Multiple(() =>
        {
            Assert.That(sourceIds.All(sourceId => !runtimeFactory.LastRuntime!.Coordinator.IsSourceEnabled(sourceId)), Is.True);
            Assert.That(settings.MinecraftWorkspaceSourcePolicies.Single().DisabledSourceIds, Is.EquivalentTo(sourceIds));
            Assert.That(control.LogicalSources.Where(source => sourceIds.Contains(source.SourceId, StringComparer.Ordinal))
                .Select(source => source.Status), Is.All.EqualTo(MinecraftWorkspaceSourceStatus.Disabled));
            Assert.That(control.Snapshot.QuerySnapshot, Is.SameAs(initialSnapshot.QuerySnapshot));
            Assert.That(control.Snapshot.Rows.Select(row => row.Identity), Is.EqualTo(initialSnapshot.Rows.Select(row => row.Identity)));
            Assert.That(control.SourceFacetList.Items, Has.Count.EqualTo(sourceFacetCount));
            Assert.That(control.IsFollowEnabled, Is.True);
            Assert.That(control.IsPaused, Is.False);
            Assert.That(control.BacklogCount, Is.Zero);
        });
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

    [Test]
    public void Show_in_source_keeps_workspace_selection_query_follow_and_pause_unchanged ()
    {
        MinecraftWorkspaceIngressEvent ingress = CreateHostIngress("show-source-file", 1, "selected source record");
        MinecraftWorkspaceReadOnlyViewSnapshot snapshot = Snapshot("source-navigation", [ingress], new MinecraftWorkspaceTimelineFilterQuery());
        FakeRuntime runtime = new("source-navigation", _ => snapshot);
        FakeSourceNavigator navigator = new();
        _host = CreateHost(new FakeRuntimeFactory { CreateRuntime = _ => runtime }, new FakePicker(null), sourceNavigator: navigator);
        OpenAndDrainInitialRefresh(_host, "source-navigation-root");
        MinecraftWorkspaceReadOnlyControl control = _host.Controller.ActiveDocument!.WorkspaceControl;
        control.EventGrid.CurrentCell = control.EventGrid.Rows[0].Cells[0];
        control.EventGrid.Rows[0].Selected = true;
        Application.DoEvents();
        control.SearchTextBox.Text = "selected";
        _host.Dispatcher.DrainBackgroundAndUi();
        control.FollowCheckBox.Checked = false;
        control.PauseButton.PerformClick();
        Application.DoEvents();

        MinecraftWorkspaceTimelineEntry selectedEntry = control.SelectedEntry!;
        EventRef selectedEventRef = selectedEntry.IngressEvent.Event.Ref;
        FileRef selectedFileRef = selectedEventRef.File;
        MinecraftWorkspaceTimelineFilterQuery appliedQuery = control.Snapshot.QuerySnapshot;
        long selectedIdentity = control.SelectedIdentity!.Value;
        control.ShowInSourceButton.PerformClick();
        _host.Dispatcher.RunNextBackground();
        _host.Dispatcher.RunNextUi();

        Assert.Multiple(() =>
        {
            Assert.That(navigator.ValidatedEntries, Has.Count.EqualTo(1));
            Assert.That(navigator.ValidatedEntries[0], Is.SameAs(selectedEntry));
            Assert.That(navigator.OpenedEntries, Has.Count.EqualTo(1));
            Assert.That(navigator.OpenedEntries[0], Is.SameAs(selectedEntry));
            Assert.That(control.SourceNavigationStatusLabel.Text, Does.Contain("Source opened:"));
            Assert.That(control.Snapshot.QuerySnapshot, Is.SameAs(appliedQuery));
            Assert.That(control.SelectedIdentity, Is.EqualTo(selectedIdentity));
            Assert.That(control.SelectedDetails!.EventRef, Is.SameAs(selectedEventRef));
            Assert.That(control.SelectedDetails.FileRef, Is.SameAs(selectedFileRef));
            Assert.That(control.IsFollowEnabled, Is.False);
            Assert.That(control.IsPaused, Is.True);
            Assert.That(control.BacklogCount, Is.Zero);
            Assert.That(_host.Errors, Is.Empty);
        });
    }

    [Test]
    public void Source_navigation_completion_from_closed_workspace_is_discarded ()
    {
        MinecraftWorkspaceIngressEvent ingress = CreateHostIngress("stale-source-file", 1, "selected source record");
        MinecraftWorkspaceReadOnlyViewSnapshot snapshot = Snapshot("stale-source", [ingress], new MinecraftWorkspaceTimelineFilterQuery());
        FakeRuntime runtime = new("stale-source", _ => snapshot);
        FakeSourceNavigator navigator = new();
        _host = CreateHost(new FakeRuntimeFactory { CreateRuntime = _ => runtime }, new FakePicker(null), sourceNavigator: navigator);
        OpenAndDrainInitialRefresh(_host, "stale-source-root");
        MinecraftWorkspaceReadOnlyDocument document = _host.Controller.ActiveDocument!;
        MinecraftWorkspaceReadOnlyControl control = document.WorkspaceControl;
        control.EventGrid.CurrentCell = control.EventGrid.Rows[0].Cells[0];
        control.EventGrid.Rows[0].Selected = true;
        Application.DoEvents();
        control.ShowInSourceButton.PerformClick();
        _host.Dispatcher.RunNextBackground();

        document.Close();
        _host.Dispatcher.DrainUi();

        Assert.Multiple(() =>
        {
            Assert.That(navigator.ValidatedEntries, Has.Count.EqualTo(1));
            Assert.That(navigator.OpenedEntries, Is.Empty);
            Assert.That(_host.Controller.ActiveDocument, Is.Null);
            Assert.That(document.IsDisposed, Is.True);
        });
    }

    [Test]
    public void Real_show_in_source_opens_and_reuses_the_normal_LogWindow_at_exact_physical_lines ()
    {
        string latestPath = CreateFile(
            "logs/latest.log",
            "[18:41:01] [Render thread/INFO] synthetic first source line\n" +
            "[18:41:02] [Render thread/ERROR] synthetic second source line\n");
        string yeezusPath = CreateFile(
            "logs/yeezus.log",
            "2099-01-01T10:00:00Z [INFO] [yeezus-core] [Client thread] synthetic multiline source header\n" +
            "    at synthetic.Stack.run(Stack.java:42)\n" +
            "2099-01-01T10:01:00Z [INFO] [yeezus-core] [Client thread] synthetic following header\n");
        string cfmPartPath = CreateFile(
            "cactusmonitor/sessions/cfm-source-nav.jsonl.part",
            $"{{\"type\":\"CYCLE\",\"sessionId\":\"source-nav\",\"sequence\":1,\"timestampEpochMillis\":{Utc("2099-01-01T10:02:00Z").ToUnixTimeMilliseconds()}}}\n");
        Settings settings = new();
        var fontConverter = TypeDescriptor.GetConverter(typeof(Font));
        settings.Preferences.Font = (Font)fontConverter.ConvertFromInvariantString(settings.Preferences.FontString)!;
        settings.Preferences.AskForClose = false;
        settings.Preferences.SaveSessions = false;
        Mock<IConfigManager> configManager = new();
        _ = configManager.Setup(item => item.Settings).Returns(settings);
        _ = configManager.Setup(item => item.ActiveConfigDir).Returns(_testDirectory);
        _ = configManager.Setup(item => item.ActiveSessionDir).Returns(_testDirectory);
        TrackingWorkspaceRuntimeFactory runtimeFactory = new(
            new MinecraftWorkspaceHostRuntimeFactory(PluginRegistry.PluginRegistry.Instance, 2048));
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
            new FakePicker(_testDirectory),
            runtimeFactory,
            triggerFactory.Create,
            dispatcher,
            errors.Add);
        _ = _host = new TestHost(window, GetDockPanel(window), window.WorkspaceHostController, dispatcher, triggerFactory, errors);

        try
        {
            window.Show();
            Application.DoEvents();
            Task<bool> openWorkspace = window.WorkspaceHostController.OpenWorkspaceAsync(_testDirectory);
            dispatcher.RunNextBackground();
            dispatcher.RunNextUi();
            Assert.That(openWorkspace.GetAwaiter().GetResult(), Is.True);
            MinecraftWorkspaceReadOnlyDocument workspaceDocument = window.WorkspaceHostController.ActiveDocument!;
            MinecraftWorkspaceReadOnlyControl control = workspaceDocument.WorkspaceControl;
            PumpUntil(_host, () => control.Snapshot.Rows.Any(row =>
                row.Details.PhysicalPath == latestPath && row.Details.RawText.Contains("synthetic second source line", StringComparison.Ordinal)));

            MinecraftWorkspaceReadOnlyRow secondRow = control.Snapshot.Rows.Single(row =>
                row.Details.PhysicalPath == latestPath && row.Details.RawText.Contains("synthetic second source line", StringComparison.Ordinal));
            int secondRowIndex = control.Snapshot.Rows.ToList().IndexOf(secondRow);
            control.EventGrid.CurrentCell = control.EventGrid.Rows[secondRowIndex].Cells[0];
            control.EventGrid.Rows[secondRowIndex].Selected = true;
            Application.DoEvents();
            long selectedIdentity = control.SelectedIdentity!.Value;
            control.ShowInSourceButton.PerformClick();
            dispatcher.RunNextBackground();
            dispatcher.RunNextUi();

            ITabController tabController = GetTabController(window);
            LogWindow sourceWindow = tabController.FindWindowByFileName(latestPath)!;
            WaitForSourceLoad(_host, sourceWindow);
            Assert.That(
                sourceWindow.CurrentLineNum,
                Is.EqualTo(checked((int)secondRow.Details.StartLineNumber - 1)),
                $"Current source row={sourceWindow.CurrentLineNum}; event physical line={secondRow.Details.StartLineNumber}; navigation status={control.SourceNavigationStatusLabel.Text}");

            int firstRowIndex = control.Snapshot.Rows.ToList().FindIndex(row =>
                row.Details.PhysicalPath == latestPath && row.Details.RawText.Contains("synthetic first source line", StringComparison.Ordinal));
            Assert.That(firstRowIndex, Is.GreaterThanOrEqualTo(0));
            workspaceDocument.Activate();
            Application.DoEvents();
            control.EventGrid.CurrentCell = control.EventGrid.Rows[firstRowIndex].Cells[0];
            control.EventGrid.Rows[firstRowIndex].Selected = true;
            Application.DoEvents();
            long secondSelectedIdentity = control.SelectedIdentity!.Value;
            control.ShowInSourceButton.PerformClick();
            dispatcher.RunNextBackground();
            dispatcher.RunNextUi();

            Assert.Multiple(() =>
            {
                Assert.That(tabController.GetAllWindows(), Has.Count.EqualTo(1));
                Assert.That(tabController.FindWindowByFileName(latestPath), Is.SameAs(sourceWindow));
                Assert.That(sourceWindow.CurrentLineNum, Is.Zero);
                Assert.That(control.SelectedIdentity, Is.EqualTo(secondSelectedIdentity));
                Assert.That(control.SelectedIdentity, Is.Not.EqualTo(selectedIdentity));
                Assert.That(control.SourceNavigationStatusLabel.Text, Does.Contain("Source opened:"));
                Assert.That(control.IsPaused, Is.False);
                Assert.That(control.Snapshot.QuerySnapshot.SearchText, Is.Null);
                Assert.That(errors, Is.Empty);
            });

        File.WriteAllText(
            latestPath,
            "[18:41:01] [Render thread/INFO] synthetic first source gone\n" +
            "[18:41:02] [Render thread/ERROR] synthetic second source line\n",
            new UTF8Encoding(false));
        workspaceDocument.Activate();
        Application.DoEvents();
        control.ShowInSourceButton.PerformClick();
        dispatcher.RunNextBackground();
        dispatcher.RunNextUi();
        Assert.Multiple(() =>
        {
            Assert.That(control.SourceNavigationStatusLabel.Text, Does.Contain("Source changed since this event was captured"));
            Assert.That(tabController.GetAllWindows(), Has.Count.EqualTo(1));
            Assert.That(tabController.FindWindowByFileName(latestPath), Is.SameAs(sourceWindow));
            Assert.That(sourceWindow.CurrentLineNum, Is.Zero);
        });

        workspaceDocument.Activate();
        PumpUntil(_host, () => control.Snapshot.Rows.Any(row =>
            row.Details.PhysicalPath == cfmPartPath && row.Details.RawText.Contains("source-nav", StringComparison.Ordinal)));
        MinecraftWorkspaceReadOnlyRow cfmRow = control.Snapshot.Rows.Single(row =>
            row.Details.PhysicalPath == cfmPartPath && row.Details.RawText.Contains("source-nav", StringComparison.Ordinal));
        int cfmRowIndex = control.Snapshot.Rows.ToList().IndexOf(cfmRow);
        string cfmFinalPath = cfmPartPath[..^".part".Length];
        File.Move(cfmPartPath, cfmFinalPath);
        control.EventGrid.CurrentCell = control.EventGrid.Rows[cfmRowIndex].Cells[0];
        control.EventGrid.Rows[cfmRowIndex].Selected = true;
        Application.DoEvents();
        control.ShowInSourceButton.PerformClick();
        dispatcher.RunNextBackground();
        dispatcher.RunNextUi();
        LogWindow cfmWindow = tabController.FindWindowByFileName(cfmFinalPath)!;
        WaitForSourceLoad(_host, cfmWindow);
        Assert.Multiple(() =>
        {
            Assert.That(cfmWindow.CurrentLineNum, Is.Zero);
            Assert.That(control.SourceNavigationStatusLabel.Text, Does.Contain("Source moved:"));
            Assert.That(control.SourceNavigationStatusLabel.Text, Does.Contain(Path.GetFileName(cfmFinalPath)));
            Assert.That(tabController.GetAllWindows(), Has.Count.EqualTo(2));
        });

        workspaceDocument.Activate();
        PumpUntil(_host, () => control.Snapshot.Rows.Any(row =>
            row.Details.PhysicalPath == yeezusPath && row.Details.RawText.Contains("synthetic.Stack.run", StringComparison.Ordinal)));
        int yeezusRowIndex = control.Snapshot.Rows.ToList().FindIndex(row =>
            row.Details.PhysicalPath == yeezusPath && row.Details.RawText.Contains("synthetic.Stack.run", StringComparison.Ordinal));
        Assert.That(yeezusRowIndex, Is.GreaterThanOrEqualTo(0));
        control.EventGrid.CurrentCell = control.EventGrid.Rows[yeezusRowIndex].Cells[0];
        control.EventGrid.Rows[yeezusRowIndex].Selected = true;
        Application.DoEvents();
        MinecraftWorkspaceTimelineEntry yeezusEntry = control.SelectedEntry!;
        control.ShowInSourceButton.PerformClick();
        dispatcher.RunNextBackground();
        dispatcher.RunNextUi();
        LogWindow yeezusWindow = tabController.FindWindowByFileName(yeezusPath)!;
        WaitForSourceLoad(_host, yeezusWindow);

        Assert.Multiple(() =>
        {
            Assert.That(yeezusEntry.IngressEvent.Event.Ref.StartLineNumber, Is.EqualTo(1));
            Assert.That(yeezusWindow.CurrentLineNum, Is.Zero, "The multiline event navigates to its physical header line.");
            Assert.That(tabController.GetAllWindows(), Has.Count.EqualTo(3));
            Assert.That(tabController.FindWindowByFileName(yeezusPath), Is.SameAs(yeezusWindow));
            Assert.That(control.SelectedEntry, Is.SameAs(yeezusEntry));
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

    private TestHost CreateHost (
        IMinecraftWorkspaceHostRuntimeFactory runtimeFactory,
        FakePicker picker,
        Func<IMinecraftWorkspaceRefreshTrigger>? refreshTriggerFactory = null,
        IMinecraftWorkspaceSourceNavigator? sourceNavigator = null,
        IConfigManager? configManager = null)
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
        refreshTriggerFactory ??= triggerFactory.Create;
        QueuedHostDispatcher dispatcher = new();
        List<Exception> errors = [];
        MinecraftWorkspaceHostController controller = new(
            form,
            dockPanel,
            picker,
            runtimeFactory,
            refreshTriggerFactory,
            dispatcher,
            errors.Add,
            sourceNavigator,
            configManager);
        return _host = new TestHost(form, dockPanel, controller, dispatcher, triggerFactory, errors);
    }

    private static Mock<IConfigManager> CreateConfig (Settings settings)
    {
        Mock<IConfigManager> config = new();
        _ = config.Setup(manager => manager.Settings).Returns(settings);
        _ = config.Setup(manager => manager.Save(It.IsAny<SettingsFlags>()));
        return config;
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
            $"Query={host.Controller.ActiveDocument?.WorkspaceControl.Snapshot.QuerySnapshot.SearchText}, " +
            $"Scope={host.Controller.ActiveDocument?.WorkspaceControl.Snapshot.QuerySnapshot.TextScope}, " +
            $"Live={host.Controller.ActiveDocument?.WorkspaceControl.LiveStateLabel.Text}, " +
            $"VisibleRows={host.Controller.ActiveDocument?.WorkspaceControl.Snapshot.Rows.Count}");
    }

    private static void WaitForSourceLoad (TestHost host, LogWindow sourceWindow)
    {
        Task finished = Task.Run(sourceWindow.WaitForLoadingFinished);
        PumpUntil(host, () => finished.IsCompleted);
        finished.GetAwaiter().GetResult();
        Application.DoEvents();
    }

    private static bool IsAtTail (DataGridView grid) => grid.RowCount == 0 ||
        grid.FirstDisplayedScrollingRowIndex + grid.DisplayedRowCount(includePartialRow: false) >= grid.RowCount;

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

    private static MinecraftWorkspaceReadOnlyViewSnapshot Snapshot (
        string displayName,
        IReadOnlyList<MinecraftWorkspaceIngressEvent> events,
        MinecraftWorkspaceTimelineFilterQuery query)
    {
        MinecraftWorkspaceTimeline timeline = new("host-query-workspace");
        timeline.AppendBatch(events);
        MinecraftWorkspaceTimelineFilterResult result = new MinecraftWorkspaceTimelineFilter().Evaluate(
            timeline.GetOrderedSnapshot(),
            query);
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

    private sealed class FakeSourceNavigator : IMinecraftWorkspaceSourceNavigator
    {
        public List<MinecraftWorkspaceTimelineEntry> ValidatedEntries { get; } = [];

        public List<MinecraftWorkspaceTimelineEntry> OpenedEntries { get; } = [];

        public MinecraftWorkspaceSourceNavigationResult Validate (MinecraftWorkspaceTimelineEntry entry)
        {
            ValidatedEntries.Add(entry);
            NormalizedLogEvent logEvent = entry.IngressEvent.Event;
            return new MinecraftWorkspaceSourceNavigationResult(
                MinecraftWorkspaceSourceNavigationStatus.Success,
                logEvent.Ref.File.Path,
                logEvent.Ref.File.Path,
                logEvent.Ref.File.FileId,
                logEvent.Ref.File.Generation,
                checked((int)logEvent.Ref.StartLineNumber),
                false);
        }

        public MinecraftWorkspaceSourceNavigationResult Open (
            MinecraftWorkspaceTimelineEntry entry,
            MinecraftWorkspaceSourceNavigationResult validatedTarget)
        {
            OpenedEntries.Add(entry);
            return validatedTarget;
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

    private sealed class TrackingWorkspaceRuntimeFactory (MinecraftWorkspaceHostRuntimeFactory inner) :
        IMinecraftWorkspaceHostRuntimeFactory,
        IMinecraftWorkspaceHostRuntimeFactoryWithSourcePolicy
    {
        public List<MinecraftWorkspaceHostRuntime> CreatedRuntimes { get; } = [];

        public List<IReadOnlyList<string>> InitialDisabledSourceIds { get; } = [];

        public MinecraftWorkspaceHostRuntime? LastRuntime { get; private set; }

        public IMinecraftWorkspaceHostRuntime Create (string rootPath)
            => Create(rootPath, []);

        public IMinecraftWorkspaceHostRuntime Create (string rootPath, IReadOnlyList<string> initiallyDisabledSourceIds)
        {
            InitialDisabledSourceIds.Add(Array.AsReadOnly(initiallyDisabledSourceIds.ToArray()));
            LastRuntime = (MinecraftWorkspaceHostRuntime)inner.Create(rootPath, initiallyDisabledSourceIds);
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

    private static ToolStripMenuItem GetRecentWorkspaceMenu (LogTabWindow window)
    {
        FieldInfo field = typeof(LogTabWindow).GetField("recentMinecraftWorkspacesToolStripMenuItem", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (ToolStripMenuItem)field.GetValue(window)!;
    }

    private static DockPanel GetDockPanel (LogTabWindow window)
    {
        FieldInfo field = typeof(LogTabWindow).GetField("dockPanel", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (DockPanel)field.GetValue(window)!;
    }
}
