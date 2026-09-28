using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Text;

using LogExpert.Core.Classes.MinecraftLogs;
using LogExpert.Core.Config;
using LogExpert.Core.Entities;
using LogExpert.Core.Enums;
using LogExpert.Core.Interfaces;
using LogExpert.UI.Controls.LogTabWindow;
using LogExpert.UI.Workspace;
using NLog;
using WeifenLuo.WinFormsUI.Docking;

namespace LogExpert.UI.Workspace;

#pragma warning disable CA1031 // UI and background boundaries log unexpected failures instead of surfacing them on the UI thread.

[SupportedOSPlatform("windows")]
internal interface IMinecraftWorkspaceFolderPicker
{
    string? PickFolder (IWin32Window owner);
}

[SupportedOSPlatform("windows")]
internal interface IMinecraftWorkspaceRefreshTrigger : IDisposable
{
    event EventHandler? Tick;

    void Start ();

    void StopTrigger ();
}

[SupportedOSPlatform("windows")]
internal interface IMinecraftWorkspaceHostRuntime : IDisposable
{
    MinecraftWorkspaceReadOnlyViewSnapshot Refresh (MinecraftWorkspaceTimelineFilterQuery query);
}

[SupportedOSPlatform("windows")]
internal interface IMinecraftWorkspaceHostRuntimeFactory
{
    IMinecraftWorkspaceHostRuntime Create (string rootPath);
}

[SupportedOSPlatform("windows")]
internal interface IMinecraftWorkspaceHostRuntimeFactoryWithSourcePolicy
{
    IMinecraftWorkspaceHostRuntime Create (string rootPath, IReadOnlyList<string> initiallyDisabledSourceIds);
}

[SupportedOSPlatform("windows")]
internal interface IMinecraftWorkspaceHostSourceManagement
{
    string WorkspaceId { get; }

    IReadOnlyList<MinecraftWorkspaceLogicalSourceSnapshot> GetLogicalSourceSnapshot ();

    void SetSourceEnabled (string sourceId, bool enabled);
}

[SupportedOSPlatform("windows")]
internal interface IMinecraftWorkspaceHostDispatcher
{
    void QueueBackgroundWork (Action work);

    bool TryPostToUi (Action work);
}

[SupportedOSPlatform("windows")]
internal sealed class FolderBrowserMinecraftWorkspacePicker : IMinecraftWorkspaceFolderPicker
{
    public string? PickFolder (IWin32Window owner)
    {
        using FolderBrowserDialog dialog = new()
        {
            ShowNewFolderButton = false
        };

        return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.SelectedPath : null;
    }
}

[SupportedOSPlatform("windows")]
internal sealed class WinFormsMinecraftWorkspaceRefreshTrigger : IMinecraftWorkspaceRefreshTrigger
{
    /// <summary>Triggers a coalesced workspace refresh every second.</summary>
    private readonly System.Threading.Timer _timer;

    public WinFormsMinecraftWorkspaceRefreshTrigger ()
    {
        _timer = new System.Threading.Timer(OnTimerTick, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public event EventHandler? Tick;

    public void Start () => _ = _timer.Change(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

    public void StopTrigger () => _ = _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

    public void Dispose ()
    {
        _timer.Dispose();
    }

    private void OnTimerTick (object? state) => Tick?.Invoke(this, EventArgs.Empty);
}

[SupportedOSPlatform("windows")]
internal sealed class WinFormsMinecraftWorkspaceHostDispatcher (Control owner) : IMinecraftWorkspaceHostDispatcher
{
    public void QueueBackgroundWork (Action work)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (!ThreadPool.QueueUserWorkItem(static state => ((Action)state!).Invoke(), work))
        {
            throw new InvalidOperationException();
        }
    }

    public bool TryPostToUi (Action work)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (owner.IsDisposed || !owner.IsHandleCreated)
        {
            return false;
        }

        try
        {
            _ = owner.BeginInvoke(work);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}

[SupportedOSPlatform("windows")]
internal sealed class MinecraftWorkspaceHostRuntimeFactory :
    IMinecraftWorkspaceHostRuntimeFactory,
    IMinecraftWorkspaceHostRuntimeFactoryWithSourcePolicy
{
    private readonly IPluginRegistry _pluginRegistry;
    private readonly int _maximumLineLength;

    public MinecraftWorkspaceHostRuntimeFactory (IPluginRegistry pluginRegistry, int maximumLineLength)
    {
        ArgumentNullException.ThrowIfNull(pluginRegistry);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumLineLength);
        _pluginRegistry = pluginRegistry;
        _maximumLineLength = maximumLineLength;
    }

    public IMinecraftWorkspaceHostRuntime Create (string rootPath)
        => Create(rootPath, []);

    public IMinecraftWorkspaceHostRuntime Create (string rootPath, IReadOnlyList<string> initiallyDisabledSourceIds)
    {
        MinecraftWorkspace workspace = new(rootPath);
        MinecraftSourceDiscovery discovery = new(workspace);
        MinecraftWorkspaceLiveSourceSessionFactory sessionFactory = new(
            _pluginRegistry,
            new EncodingOptions { Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false) },
            _maximumLineLength);
        MinecraftWorkspaceLiveCoordinator coordinator = new(
            discovery,
            sessionFactory,
            initiallyDisabledSourceIds: initiallyDisabledSourceIds);
        return new MinecraftWorkspaceHostRuntime(workspace, discovery, sessionFactory, coordinator);
    }
}

[SupportedOSPlatform("windows")]
internal sealed class MinecraftWorkspaceHostRuntime : IMinecraftWorkspaceHostRuntime, IMinecraftWorkspaceHostSourceManagement
{
    private readonly MinecraftWorkspaceTimeline _timeline;
    private readonly MinecraftWorkspaceTimelineFilter _filter = new();
    private int _disposed;

    public MinecraftWorkspaceHostRuntime (
        MinecraftWorkspace workspace,
        MinecraftSourceDiscovery discovery,
        MinecraftWorkspaceLiveSourceSessionFactory sessionFactory,
        MinecraftWorkspaceLiveCoordinator coordinator)
    {
        Workspace = workspace;
        Discovery = discovery;
        SessionFactory = sessionFactory;
        Coordinator = coordinator;
        _timeline = new MinecraftWorkspaceTimeline(workspace.WorkspaceId);
        DisplayName = Path.GetFileName(workspace.RootPath);
        if (string.IsNullOrWhiteSpace(DisplayName))
        {
            DisplayName = workspace.RootPath;
        }
    }

    public MinecraftWorkspace Workspace { get; }

    public MinecraftSourceDiscovery Discovery { get; }

    public MinecraftWorkspaceLiveSourceSessionFactory SessionFactory { get; }

    public MinecraftWorkspaceLiveCoordinator Coordinator { get; }

    public string DisplayName { get; }

    public string WorkspaceId => Workspace.WorkspaceId;

    public IReadOnlyList<MinecraftWorkspaceLogicalSourceSnapshot> GetLogicalSourceSnapshot () => Coordinator.GetLogicalSourceSnapshot();

    public void SetSourceEnabled (string sourceId, bool enabled) => Coordinator.SetSourceEnabled(sourceId, enabled);

    public MinecraftWorkspaceReadOnlyViewSnapshot Refresh (MinecraftWorkspaceTimelineFilterQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        Coordinator.Reconcile();
        IReadOnlyList<MinecraftWorkspaceIngressEvent> events = Coordinator.DrainPendingEvents();
        if (events.Count > 0)
        {
            _timeline.AppendBatch(events);
        }

        MinecraftWorkspaceTimelineFilterResult result = _filter.Evaluate(
            _timeline.GetOrderedSnapshot(),
            query);
        return MinecraftWorkspaceReadOnlyViewPresenter.CreateSnapshot(DisplayName, result);
    }

    public void Dispose ()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            Coordinator.Dispose();
        }
    }
}

[SupportedOSPlatform("windows")]
internal sealed class MinecraftWorkspaceHostController : IDisposable
{
    private readonly IWin32Window _owner;
    private readonly DockPanel _dockPanel;
    private readonly IMinecraftWorkspaceFolderPicker _folderPicker;
    private readonly IMinecraftWorkspaceHostRuntimeFactory _runtimeFactory;
    private readonly Func<IMinecraftWorkspaceRefreshTrigger> _triggerFactory;
    private readonly IMinecraftWorkspaceHostDispatcher _dispatcher;
    private readonly Action<Exception> _showError;
    private readonly IMinecraftWorkspaceSourceNavigator? _sourceNavigator;
    private readonly MinecraftWorkspaceSettingsStore? _settingsStore;
    private readonly object _gate = new();
    private MinecraftWorkspaceHostSession? _activeSession;
    private int _disposed;

    public MinecraftWorkspaceHostController (
        IWin32Window owner,
        DockPanel dockPanel,
        IMinecraftWorkspaceFolderPicker folderPicker,
        IMinecraftWorkspaceHostRuntimeFactory runtimeFactory,
        Func<IMinecraftWorkspaceRefreshTrigger> triggerFactory,
        IMinecraftWorkspaceHostDispatcher dispatcher,
        Action<Exception> showError,
        IMinecraftWorkspaceSourceNavigator? sourceNavigator = null,
        IConfigManager? configManager = null)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _dockPanel = dockPanel ?? throw new ArgumentNullException(nameof(dockPanel));
        _folderPicker = folderPicker ?? throw new ArgumentNullException(nameof(folderPicker));
        _runtimeFactory = runtimeFactory ?? throw new ArgumentNullException(nameof(runtimeFactory));
        _triggerFactory = triggerFactory ?? throw new ArgumentNullException(nameof(triggerFactory));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _showError = showError ?? throw new ArgumentNullException(nameof(showError));
        _sourceNavigator = sourceNavigator;
        _settingsStore = configManager is null ? null : new MinecraftWorkspaceSettingsStore(configManager);
    }

    public event EventHandler? RecentWorkspacesChanged;

    public IReadOnlyList<string> GetRecentWorkspaceRoots () => _settingsStore?.GetRecentWorkspaceRoots() ?? Array.Empty<string>();

    public void RemoveRecentWorkspace (string rootPath)
    {
        _settingsStore?.RemoveRecentWorkspace(rootPath);
        RecentWorkspacesChanged?.Invoke(this, EventArgs.Empty);
    }

    public MinecraftWorkspaceReadOnlyDocument? ActiveDocument
    {
        get
        {
            lock (_gate)
            {
                return _activeSession?.Document;
            }
        }
    }

    public Task<bool> OpenSelectedWorkspaceAsync ()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return Task.FromResult(false);
        }

        try
        {
            string? path = _folderPicker.PickFolder(_owner);
            return string.IsNullOrWhiteSpace(path)
                ? Task.FromResult(false)
                : OpenWorkspaceAsync(path);
        }
        catch (Exception exception)
        {
            _showError(exception);
            return Task.FromResult(false);
        }
    }

    public Task<bool> OpenWorkspaceAsync (string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return Task.FromResult(false);
            }
        }

        TaskCompletionSource<bool> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _dispatcher.QueueBackgroundWork(() => PrepareCandidate(rootPath, completion));
        }
        catch (Exception exception)
        {
            _showError(exception);
            completion.TrySetResult(false);
        }

        return completion.Task;
    }

    public void Dispose ()
    {
        MinecraftWorkspaceHostSession? session;
        lock (_gate)
        {
            if (_disposed != 0)
            {
                return;
            }

            _disposed = 1;
            session = _activeSession;
            _activeSession = null;
        }

        session?.Dispose(closeDocument: true);
    }

    private void PrepareCandidate (string rootPath, TaskCompletionSource<bool> completion)
    {
        IMinecraftWorkspaceHostRuntime? runtime = null;
        MinecraftWorkspaceReadOnlyViewSnapshot? snapshot = null;
        Exception? failure = null;
        try
        {
            IReadOnlyList<string> disabledSources = _settingsStore?.GetDisabledSourceIds(rootPath) ?? Array.Empty<string>();
            runtime = _runtimeFactory is IMinecraftWorkspaceHostRuntimeFactoryWithSourcePolicy sourcePolicyFactory
                ? sourcePolicyFactory.Create(rootPath, disabledSources)
                : _runtimeFactory.Create(rootPath);
            snapshot = runtime.Refresh(new MinecraftWorkspaceTimelineFilterQuery());
        }
        catch (Exception exception)
        {
            runtime?.Dispose();
            runtime = null;
            failure = exception;
        }

        IMinecraftWorkspaceHostRuntime? preparedRuntime = runtime;
        MinecraftWorkspaceReadOnlyViewSnapshot? initialSnapshot = snapshot;
        Exception? openFailure = failure;
        if (!_dispatcher.TryPostToUi(() => CompleteCandidate(rootPath, preparedRuntime, initialSnapshot, openFailure, completion)))
        {
            preparedRuntime?.Dispose();
            completion.TrySetResult(false);
        }
    }

    private void CompleteCandidate (
        string rootPath,
        IMinecraftWorkspaceHostRuntime? runtime,
        MinecraftWorkspaceReadOnlyViewSnapshot? snapshot,
        Exception? failure,
        TaskCompletionSource<bool> completion)
    {
        if (failure != null)
        {
            _showError(failure);
            completion.TrySetResult(false);
            return;
        }

        if (runtime == null || snapshot == null || Volatile.Read(ref _disposed) != 0)
        {
            runtime?.Dispose();
            completion.TrySetResult(false);
            return;
        }

        MinecraftWorkspaceReadOnlyDocument? document = null;
        MinecraftWorkspaceHostSession? candidate = null;
        try
        {
            document = new MinecraftWorkspaceReadOnlyDocument(snapshot);
            candidate = new MinecraftWorkspaceHostSession(
                runtime,
                document,
                _triggerFactory(),
                _dispatcher,
                session => IsCurrentSession(session),
                OnDocumentClosed,
                _sourceNavigator,
                _settingsStore,
                _showError);
            if (runtime is IMinecraftWorkspaceHostSourceManagement sourceManagement)
            {
                document.WorkspaceControl.ApplyLogicalSourceSnapshot(sourceManagement.GetLogicalSourceSnapshot());
            }
            document.Show(_dockPanel, DockState.Document);

            MinecraftWorkspaceHostSession? previous;
            lock (_gate)
            {
                if (_disposed != 0)
                {
                    candidate.Dispose(closeDocument: true);
                    completion.TrySetResult(false);
                    return;
                }

                previous = _activeSession;
                _activeSession = candidate;
            }

            previous?.Dispose(closeDocument: true);
            candidate.Activate();
            if (_settingsStore is not null)
            {
                try
                {
                    _settingsStore.RecordSuccessfulOpen(rootPath);
                    RecentWorkspacesChanged?.Invoke(this, EventArgs.Empty);
                }
                catch (Exception exception)
                {
                    _showError(exception);
                }
            }
            completion.TrySetResult(true);
        }
        catch (Exception exception)
        {
            lock (_gate)
            {
                if (ReferenceEquals(_activeSession, candidate))
                {
                    _activeSession = null;
                }
            }

            if (candidate != null)
            {
                candidate.Dispose(closeDocument: true);
            }
            else
            {
                runtime.Dispose();
                document?.Dispose();
            }

            _showError(exception);
            completion.TrySetResult(false);
        }
    }

    private bool IsCurrentSession (MinecraftWorkspaceHostSession session)
    {
        lock (_gate)
        {
            return _disposed == 0 && ReferenceEquals(_activeSession, session);
        }
    }

    private void OnDocumentClosed (MinecraftWorkspaceHostSession session)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_activeSession, session))
            {
                _activeSession = null;
            }
        }

        session.Dispose(closeDocument: false);
    }
}

[SupportedOSPlatform("windows")]
internal sealed class MinecraftWorkspaceHostSession : IDisposable
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly IMinecraftWorkspaceHostRuntime _runtime;
    private readonly IMinecraftWorkspaceRefreshTrigger _trigger;
    private readonly IMinecraftWorkspaceHostDispatcher _dispatcher;
    private readonly Func<MinecraftWorkspaceHostSession, bool> _isCurrent;
    private readonly Action<MinecraftWorkspaceHostSession> _documentClosed;
    private readonly IMinecraftWorkspaceSourceNavigator? _sourceNavigator;
    private readonly MinecraftWorkspaceSettingsStore? _settingsStore;
    private readonly Action<Exception> _showError;
    private readonly object _gate = new();
    private readonly object _sourcePolicyGate = new();
    private bool _refreshRunning;
    private bool _refreshPending;
    private bool _paused;
    private MinecraftWorkspaceTimelineFilterQuery _currentQuery;
    private long _queryRevision;
    private MinecraftWorkspaceReadOnlyViewSnapshot? _pausedSnapshot;
    private long _pausedSnapshotRevision;
    private long? _allowPausedApplyRevision;
    private long _sourceNavigationRevision;
    private readonly ConcurrentDictionary<string, long> _sourcePolicyRevisions = new(StringComparer.Ordinal);
    private int _disposed;

    public MinecraftWorkspaceHostSession (
        IMinecraftWorkspaceHostRuntime runtime,
        MinecraftWorkspaceReadOnlyDocument document,
        IMinecraftWorkspaceRefreshTrigger trigger,
        IMinecraftWorkspaceHostDispatcher dispatcher,
        Func<MinecraftWorkspaceHostSession, bool> isCurrent,
        Action<MinecraftWorkspaceHostSession> documentClosed,
        IMinecraftWorkspaceSourceNavigator? sourceNavigator,
        MinecraftWorkspaceSettingsStore? settingsStore,
        Action<Exception> showError)
    {
        _runtime = runtime;
        Document = document;
        _trigger = trigger;
        _dispatcher = dispatcher;
        _isCurrent = isCurrent;
        _documentClosed = documentClosed;
        _sourceNavigator = sourceNavigator;
        _settingsStore = settingsStore;
        _showError = showError;
        _currentQuery = Document.WorkspaceControl.Snapshot.QuerySnapshot;
        Document.FormClosed += OnDocumentFormClosed;
        Document.WorkspaceControl.FilterQueryChanged += OnFilterQueryChanged;
        Document.WorkspaceControl.PauseChanged += OnPauseChanged;
        Document.WorkspaceControl.LiveSourceEnabledChanged += OnLiveSourceEnabledChanged;
        Document.WorkspaceControl.RescanRequested += OnRescanRequested;
        if (_sourceNavigator != null)
        {
            Document.WorkspaceControl.ShowInSourceRequested += OnShowInSourceRequested;
        }
    }

    public MinecraftWorkspaceReadOnlyDocument Document { get; }

    public void Activate ()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        _trigger.Tick += OnTriggerTick;
        _trigger.Start();
        RequestRefresh();
    }

    public void Dispose () => Dispose(closeDocument: true);

    public void Dispose (bool closeDocument)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        lock (_gate)
        {
            _refreshPending = false;
            _pausedSnapshot = null;
            _pausedSnapshotRevision = 0;
            _allowPausedApplyRevision = null;
        }

        _trigger.Tick -= OnTriggerTick;
        _trigger.StopTrigger();
        _trigger.Dispose();
        Document.WorkspaceControl.FilterQueryChanged -= OnFilterQueryChanged;
        Document.WorkspaceControl.PauseChanged -= OnPauseChanged;
        Document.WorkspaceControl.LiveSourceEnabledChanged -= OnLiveSourceEnabledChanged;
        Document.WorkspaceControl.RescanRequested -= OnRescanRequested;
        if (_sourceNavigator != null)
        {
            Document.WorkspaceControl.ShowInSourceRequested -= OnShowInSourceRequested;
        }
        Document.FormClosed -= OnDocumentFormClosed;
        try
        {
            _runtime.Dispose();
        }
        catch (Exception exception)
        {
            Logger.Error(exception, "Could not dispose Minecraft workspace runtime.");
        }

        if (closeDocument && !Document.IsDisposed)
        {
            Document.Close();
            Document.Dispose();
        }
    }

    private void OnTriggerTick (object? sender, EventArgs e) => RequestRefresh();

    private void OnRescanRequested (object? sender, EventArgs e) => RequestRefresh();

    private void OnLiveSourceEnabledChanged (object? sender, MinecraftWorkspaceSourceEnabledChangedEventArgs e)
    {
        long revision = _sourcePolicyRevisions.AddOrUpdate(e.SourceId, 1, (_, current) => checked(current + 1));
        try
        {
            _dispatcher.QueueBackgroundWork(() => SetSourceEnabled(e.SourceId, e.IsEnabled, revision));
        }
        catch (Exception exception)
        {
            ClearPendingSourceChange(e.SourceId, exception);
        }
    }

    private void SetSourceEnabled (string sourceId, bool enabled, long revision)
    {
        lock (_sourcePolicyGate)
        {
            if (Volatile.Read(ref _disposed) != 0 ||
                !_sourcePolicyRevisions.TryGetValue(sourceId, out long currentRevision) ||
                revision != currentRevision ||
                !_isCurrent(this))
            {
                return;
            }

            if (_runtime is not IMinecraftWorkspaceHostSourceManagement sourceManagement)
            {
                ClearPendingSourceChange(sourceId, new InvalidOperationException());
                return;
            }

            MinecraftWorkspaceLogicalSourceSnapshot? current = sourceManagement.GetLogicalSourceSnapshot()
                .FirstOrDefault(source => source.SourceId == sourceId);
            bool previousEnabled = current?.IsEnabled ?? !enabled;
            try
            {
                _settingsStore?.SetSourceEnabledForWorkspaceId(sourceManagement.WorkspaceId, sourceId, enabled);
                sourceManagement.SetSourceEnabled(sourceId, enabled);
                RequestRefresh();
            }
            catch (Exception exception)
            {
                try
                {
                    _settingsStore?.SetSourceEnabledForWorkspaceId(sourceManagement.WorkspaceId, sourceId, previousEnabled);
                }
                catch (Exception rollbackException)
                {
                    Logger.Error(rollbackException, "Could not restore Minecraft workspace source policy after a failed toggle.");
                }

                ClearPendingSourceChange(sourceId, exception);
            }
        }
    }

    private void ClearPendingSourceChange (string sourceId, Exception exception)
    {
        _ = _dispatcher.TryPostToUi(() =>
        {
            if (Volatile.Read(ref _disposed) != 0 || !_isCurrent(this) || Document.IsDisposed || Document.WorkspaceControl.IsDisposed)
            {
                return;
            }

            IReadOnlyList<MinecraftWorkspaceLogicalSourceSnapshot> sources =
                (_runtime as IMinecraftWorkspaceHostSourceManagement)?.GetLogicalSourceSnapshot() ?? Array.Empty<MinecraftWorkspaceLogicalSourceSnapshot>();
            Document.WorkspaceControl.ApplyLogicalSourceSnapshot(sources, clearPendingSourceId: sourceId);
            _showError(exception);
        });
    }

    private void OnShowInSourceRequested (object? sender, MinecraftWorkspaceShowInSourceEventArgs e)
    {
        IMinecraftWorkspaceSourceNavigator navigator = _sourceNavigator!;
        long revision = Interlocked.Increment(ref _sourceNavigationRevision);
        MinecraftWorkspaceTimelineEntry entry = e.Entry;
        try
        {
            _dispatcher.QueueBackgroundWork(() => ValidateSourceTarget(navigator, entry, revision));
        }
        catch (Exception exception)
        {
            CompleteSourceNavigation(entry, CreateNavigationFailure(entry, exception), revision);
        }
    }

    private void ValidateSourceTarget (
        IMinecraftWorkspaceSourceNavigator navigator,
        MinecraftWorkspaceTimelineEntry entry,
        long revision)
    {
        MinecraftWorkspaceSourceNavigationResult result;
        try
        {
            result = navigator.Validate(entry);
        }
        catch (Exception exception)
        {
            result = CreateNavigationFailure(entry, exception);
        }

        _ = _dispatcher.TryPostToUi(() => CompleteSourceNavigation(entry, result, revision));
    }

    private void CompleteSourceNavigation (
        MinecraftWorkspaceTimelineEntry entry,
        MinecraftWorkspaceSourceNavigationResult result,
        long revision)
    {
        if (Volatile.Read(ref _disposed) != 0 ||
            revision != Interlocked.Read(ref _sourceNavigationRevision) ||
            !_isCurrent(this) ||
            Document.IsDisposed ||
            Document.WorkspaceControl.IsDisposed)
        {
            return;
        }

        if (result.Status == MinecraftWorkspaceSourceNavigationStatus.Success)
        {
            try
            {
                result = _sourceNavigator!.Open(entry, result);
            }
            catch (Exception exception)
            {
                result = CreateNavigationFailure(entry, exception);
            }
        }

        Document.WorkspaceControl.SetSourceNavigationStatus(FormatSourceNavigationStatus(result));
    }

    private static MinecraftWorkspaceSourceNavigationResult CreateNavigationFailure (
        MinecraftWorkspaceTimelineEntry entry,
        Exception exception)
    {
        NormalizedLogEvent logEvent = entry.IngressEvent.Event;
        FileRef file = logEvent.Ref.File;
        long line = logEvent.Ref.StartLineNumber;
        return new MinecraftWorkspaceSourceNavigationResult(
            MinecraftWorkspaceSourceNavigationStatus.OpenFailed,
            file.Path,
            null,
            file.FileId,
            file.Generation,
            line is >= 1 and <= int.MaxValue ? (int)line : 0,
            false,
            exception.Message);
    }

    private static string FormatSourceNavigationStatus (MinecraftWorkspaceSourceNavigationResult result)
    {
        string provenance = $"FileId {result.FileId}, generation {result.Generation}";
        return result.Status switch
        {
            MinecraftWorkspaceSourceNavigationStatus.Success when result.WasRelocated =>
                $"Source moved: {Path.GetFileName(result.OriginalPath)} → {Path.GetFileName(result.ResolvedPath)} · line {result.StartLineNumber}",
            MinecraftWorkspaceSourceNavigationStatus.Success =>
                $"Source opened: {Path.GetFileName(result.ResolvedPath)} · line {result.StartLineNumber}",
            MinecraftWorkspaceSourceNavigationStatus.SourceUnavailable => $"Source unavailable · {provenance}",
            MinecraftWorkspaceSourceNavigationStatus.SourceChanged => $"Source changed since this event was captured · {provenance}",
            MinecraftWorkspaceSourceNavigationStatus.InvalidLocation => $"Invalid source location · {provenance}",
            _ => $"Source open failed · {provenance}{(string.IsNullOrWhiteSpace(result.Error) ? string.Empty : $" · {result.Error}")}"
        };
    }

    private void OnFilterQueryChanged (object? sender, MinecraftWorkspaceFilterQueryChangedEventArgs e)
    {
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            _currentQuery = e.Query;
            _queryRevision = checked(_queryRevision + 1);
            if (_paused)
            {
                _pausedSnapshot = null;
                _pausedSnapshotRevision = 0;
                _allowPausedApplyRevision = _queryRevision;
            }
        }

        if (Document.WorkspaceControl.IsPaused)
        {
            Document.WorkspaceControl.SetPaused(isPaused: true, backlogCount: 0);
        }

        RequestRefresh();
    }

    private void OnPauseChanged (object? sender, EventArgs e)
    {
        bool isPaused = Document.WorkspaceControl.IsPaused;
        MinecraftWorkspaceReadOnlyViewSnapshot? snapshotToApply = null;
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            _paused = isPaused;
            _allowPausedApplyRevision = null;
            if (isPaused)
            {
                _pausedSnapshot = null;
                _pausedSnapshotRevision = 0;
            }
            else
            {
                if (_pausedSnapshotRevision == _queryRevision)
                {
                    snapshotToApply = _pausedSnapshot;
                }

                _pausedSnapshot = null;
                _pausedSnapshotRevision = 0;
            }
        }

        Document.WorkspaceControl.SetPaused(isPaused, backlogCount: 0);
        if (!isPaused)
        {
            if (snapshotToApply != null && _isCurrent(this) && !Document.IsDisposed && !Document.WorkspaceControl.IsDisposed)
            {
                Document.WorkspaceControl.ApplySnapshot(snapshotToApply);
            }

            RequestRefresh();
        }
    }

    private void RequestRefresh ()
    {
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            if (_refreshRunning)
            {
                _refreshPending = true;
                return;
            }

            _refreshRunning = true;
        }

        QueueRefresh();
    }

    private void QueueRefresh ()
    {
        try
        {
            _dispatcher.QueueBackgroundWork(RunRefresh);
        }
        catch (Exception exception)
        {
            Logger.Error(exception, "Could not schedule Minecraft workspace refresh.");
            lock (_gate)
            {
                _refreshRunning = false;
                _refreshPending = false;
            }
        }
    }

    private void RunRefresh ()
    {
        MinecraftWorkspaceTimelineFilterQuery query;
        long queryRevision;
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                _refreshRunning = false;
                _refreshPending = false;
                return;
            }

            query = _currentQuery;
            queryRevision = _queryRevision;
            _refreshPending = false;
        }

        MinecraftWorkspaceReadOnlyViewSnapshot? snapshot = null;
        IReadOnlyList<MinecraftWorkspaceLogicalSourceSnapshot> sources = Array.Empty<MinecraftWorkspaceLogicalSourceSnapshot>();
        if (Volatile.Read(ref _disposed) == 0)
        {
            try
            {
                snapshot = _runtime.Refresh(query);
                sources = (_runtime as IMinecraftWorkspaceHostSourceManagement)?.GetLogicalSourceSnapshot() ?? sources;
            }
            catch (Exception exception)
            {
                Logger.Error(exception, "Minecraft workspace refresh failed; keeping the last successful view.");
            }
        }

        if (snapshot != null && Volatile.Read(ref _disposed) == 0)
        {
            _ = _dispatcher.TryPostToUi(() => ApplySnapshotIfCurrent(snapshot, sources, queryRevision));
        }

        bool runCoalescedRefresh;
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                _refreshRunning = false;
                _refreshPending = false;
                return;
            }

            runCoalescedRefresh = _refreshPending;
            _refreshPending = false;
            if (!runCoalescedRefresh)
            {
                _refreshRunning = false;
            }
        }

        if (runCoalescedRefresh)
        {
            QueueRefresh();
        }
    }

    private void ApplySnapshotIfCurrent (
        MinecraftWorkspaceReadOnlyViewSnapshot snapshot,
        IReadOnlyList<MinecraftWorkspaceLogicalSourceSnapshot> sources,
        long queryRevision)
    {
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0 || queryRevision != _queryRevision)
            {
                return;
            }
        }

        if (!_isCurrent(this) || Document.IsDisposed || Document.WorkspaceControl.IsDisposed)
        {
            return;
        }

        bool applySnapshot = true;
        int backlogCount = 0;
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0 || queryRevision != _queryRevision)
            {
                return;
            }

            if (_paused)
            {
                if (_allowPausedApplyRevision == queryRevision)
                {
                    _allowPausedApplyRevision = null;
                    _pausedSnapshot = null;
                    _pausedSnapshotRevision = 0;
                }
                else
                {
                    _pausedSnapshot = snapshot;
                    _pausedSnapshotRevision = queryRevision;
                    backlogCount = CountUnseenIdentities(snapshot, Document.WorkspaceControl.Snapshot);
                    applySnapshot = false;
                }
            }
        }

        if (!applySnapshot)
        {
            Document.WorkspaceControl.ApplyLogicalSourceSnapshot(sources);
            Document.WorkspaceControl.SetPaused(isPaused: true, backlogCount);
            return;
        }

        try
        {
            Document.WorkspaceControl.ApplySnapshot(snapshot);
            Document.WorkspaceControl.ApplyLogicalSourceSnapshot(sources);
            if (Document.WorkspaceControl.IsPaused)
            {
                Document.WorkspaceControl.SetPaused(isPaused: true, backlogCount: 0);
            }
        }
        catch (Exception exception)
        {
            Logger.Error(exception, "Could not apply Minecraft workspace view snapshot.");
        }
    }

    private static int CountUnseenIdentities (
        MinecraftWorkspaceReadOnlyViewSnapshot pendingSnapshot,
        MinecraftWorkspaceReadOnlyViewSnapshot visibleSnapshot)
    {
        HashSet<long> visibleIdentities = visibleSnapshot.Rows.Select(row => row.Identity).ToHashSet();
        return pendingSnapshot.Rows
            .Select(row => row.Identity)
            .Distinct()
            .Count(identity => !visibleIdentities.Contains(identity));
    }

    private void OnDocumentFormClosed (object? sender, FormClosedEventArgs e)
    {
        Document.FormClosed -= OnDocumentFormClosed;
        _documentClosed(this);
    }
}

#pragma warning restore CA1031
