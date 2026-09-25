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
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };

    public WinFormsMinecraftWorkspaceRefreshTrigger ()
    {
        _timer.Tick += OnTimerTick;
    }

    public event EventHandler? Tick;

    public void Start () => _timer.Start();

    public void StopTrigger () => _timer.Stop();

    public void Dispose ()
    {
        _timer.Tick -= OnTimerTick;
        _timer.Dispose();
    }

    private void OnTimerTick (object? sender, EventArgs e) => Tick?.Invoke(this, e);
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
internal sealed class MinecraftWorkspaceHostRuntimeFactory : IMinecraftWorkspaceHostRuntimeFactory
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
    {
        MinecraftWorkspace workspace = new(rootPath);
        MinecraftSourceDiscovery discovery = new(workspace);
        MinecraftWorkspaceLiveSourceSessionFactory sessionFactory = new(
            _pluginRegistry,
            new EncodingOptions { Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false) },
            _maximumLineLength);
        MinecraftWorkspaceLiveCoordinator coordinator = new(discovery, sessionFactory);
        return new MinecraftWorkspaceHostRuntime(workspace, discovery, sessionFactory, coordinator);
    }
}

[SupportedOSPlatform("windows")]
internal sealed class MinecraftWorkspaceHostRuntime : IMinecraftWorkspaceHostRuntime
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
        Action<Exception> showError)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _dockPanel = dockPanel ?? throw new ArgumentNullException(nameof(dockPanel));
        _folderPicker = folderPicker ?? throw new ArgumentNullException(nameof(folderPicker));
        _runtimeFactory = runtimeFactory ?? throw new ArgumentNullException(nameof(runtimeFactory));
        _triggerFactory = triggerFactory ?? throw new ArgumentNullException(nameof(triggerFactory));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _showError = showError ?? throw new ArgumentNullException(nameof(showError));
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
            runtime = _runtimeFactory.Create(rootPath);
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
        if (!_dispatcher.TryPostToUi(() => CompleteCandidate(preparedRuntime, initialSnapshot, openFailure, completion)))
        {
            preparedRuntime?.Dispose();
            completion.TrySetResult(false);
        }
    }

    private void CompleteCandidate (
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
                OnDocumentClosed);
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
    private readonly object _gate = new();
    private bool _refreshRunning;
    private bool _refreshPending;
    private MinecraftWorkspaceTimelineFilterQuery _currentQuery;
    private long _queryRevision;
    private int _disposed;

    public MinecraftWorkspaceHostSession (
        IMinecraftWorkspaceHostRuntime runtime,
        MinecraftWorkspaceReadOnlyDocument document,
        IMinecraftWorkspaceRefreshTrigger trigger,
        IMinecraftWorkspaceHostDispatcher dispatcher,
        Func<MinecraftWorkspaceHostSession, bool> isCurrent,
        Action<MinecraftWorkspaceHostSession> documentClosed)
    {
        _runtime = runtime;
        Document = document;
        _trigger = trigger;
        _dispatcher = dispatcher;
        _isCurrent = isCurrent;
        _documentClosed = documentClosed;
        _currentQuery = Document.WorkspaceControl.Snapshot.QuerySnapshot;
        Document.FormClosed += OnDocumentFormClosed;
        Document.WorkspaceControl.FilterQueryChanged += OnFilterQueryChanged;
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
        }

        _trigger.Tick -= OnTriggerTick;
        _trigger.StopTrigger();
        _trigger.Dispose();
        Document.WorkspaceControl.FilterQueryChanged -= OnFilterQueryChanged;
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
        }

        RequestRefresh();
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
        if (Volatile.Read(ref _disposed) == 0)
        {
            try
            {
                snapshot = _runtime.Refresh(query);
            }
            catch (Exception exception)
            {
                Logger.Error(exception, "Minecraft workspace refresh failed; keeping the last successful view.");
            }
        }

        if (snapshot != null && Volatile.Read(ref _disposed) == 0)
        {
            _ = _dispatcher.TryPostToUi(() => ApplySnapshotIfCurrent(snapshot, queryRevision));
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

    private void ApplySnapshotIfCurrent (MinecraftWorkspaceReadOnlyViewSnapshot snapshot, long queryRevision)
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

        try
        {
            Document.WorkspaceControl.ApplySnapshot(snapshot);
        }
        catch (Exception exception)
        {
            Logger.Error(exception, "Could not apply Minecraft workspace view snapshot.");
        }
    }

    private void OnDocumentFormClosed (object? sender, FormClosedEventArgs e)
    {
        Document.FormClosed -= OnDocumentFormClosed;
        _documentClosed(this);
    }
}

#pragma warning restore CA1031
