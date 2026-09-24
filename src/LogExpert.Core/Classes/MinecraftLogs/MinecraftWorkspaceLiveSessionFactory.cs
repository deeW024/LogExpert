using LogExpert.Core.Classes.Log;
using LogExpert.Core.Entities;
using LogExpert.Core.Enums;
using LogExpert.Core.Interfaces;

namespace LogExpert.Core.Classes.MinecraftLogs;

/// <summary>A single-source reader session owned by the workspace coordinator.</summary>
public interface IMinecraftWorkspaceLiveSourceSession : IDisposable
{
    event EventHandler<MinecraftLiveSourceEventsProducedEventArgs>? EventsProduced;

    void StartMonitoring ();
}

/// <summary>Creates one unstarted session for one discovered physical file.</summary>
public interface IMinecraftWorkspaceLiveSourceSessionFactory
{
    IMinecraftWorkspaceLiveSourceSession Create (DiscoveredSourceFile source);
}

/// <summary>
/// Creates YEE-42 sessions through the existing single-file SystemDirect reader path.
/// XML mode stays disabled, and no second reader or watcher is introduced.
/// </summary>
public sealed class MinecraftWorkspaceLiveSourceSessionFactory :
    IMinecraftWorkspaceLiveSourceSessionFactory,
    IMinecraftWorkspaceLiveSourceSessionFactoryWithReadRequest
{
    private readonly IPluginRegistry _pluginRegistry;
    private readonly EncodingOptions _encodingOptions;
    private readonly int _maximumLineLength;

    public MinecraftWorkspaceLiveSourceSessionFactory (
        IPluginRegistry pluginRegistry,
        EncodingOptions encodingOptions,
        int maximumLineLength = 500)
    {
        ArgumentNullException.ThrowIfNull(pluginRegistry);
        ArgumentNullException.ThrowIfNull(encodingOptions);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumLineLength);

        _pluginRegistry = pluginRegistry;
        _encodingOptions = encodingOptions;
        _maximumLineLength = maximumLineLength;
    }

    public IMinecraftWorkspaceLiveSourceSession Create (DiscoveredSourceFile source)
    {
        ArgumentNullException.ThrowIfNull(source);

        MinecraftWorkspaceSourceReadRequest readRequest = source.SegmentRole is
            MinecraftSourceSegmentRole.Rotated or MinecraftSourceSegmentRole.Final
                ? MinecraftWorkspaceSourceReadRequest.Snapshot()
                : MinecraftWorkspaceSourceReadRequest.Live();
        return Create(source, readRequest);
    }

    public IMinecraftWorkspaceLiveSourceSession Create (
        DiscoveredSourceFile source,
        MinecraftWorkspaceSourceReadRequest readRequest)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(readRequest);

        var reader = new LogfileReader(
            source.FullPath,
            _encodingOptions,
            multiFile: false,
            bufferCount: 40,
            linesPerBuffer: 50,
            new MultiFileOptions(),
            ReaderType.SystemDirect,
            _pluginRegistry,
            _maximumLineLength);

        try
        {
            return new OwnedMinecraftLiveSourceSession(
                new MinecraftLiveSourceSession(source, reader, readRequest),
                reader);
        }
        catch
        {
            reader.Dispose();
            throw;
        }
    }

    private sealed class OwnedMinecraftLiveSourceSession :
        IMinecraftWorkspaceLiveSourceSession,
        IMinecraftWorkspaceLiveSourceSessionProgress,
        IMinecraftWorkspaceSourceHandoffSession
    {
        private readonly MinecraftLiveSourceSession _session;
        private readonly LogfileReader _reader;
        private EventHandler<MinecraftWorkspaceHandoffCheckpointEventArgs>? _handoffCheckpointCaptured;
        private int _disposed;

        public OwnedMinecraftLiveSourceSession (MinecraftLiveSourceSession session, LogfileReader reader)
        {
            _session = session;
            _reader = reader;
            _session.EventsProduced += ForwardEvents;
            _session.HandoffCheckpointCaptured += ForwardHandoffCheckpoint;
        }

        public event EventHandler<MinecraftLiveSourceEventsProducedEventArgs>? EventsProduced;

        public event EventHandler<MinecraftWorkspaceHandoffCheckpointEventArgs>? HandoffCheckpointCaptured
        {
            add => _handoffCheckpointCaptured += value;
            remove => _handoffCheckpointCaptured -= value;
        }

        public FileRef CurrentFile => _session.CurrentFile;

        public long NextSourceLocalSequence => _session.NextSourceLocalSequence;

        public bool IsImmutableComplete => _session.IsImmutableComplete;

        public MinecraftSourceHandoffCheckpoint GetHandoffCheckpoint () =>
            _session.GetHandoffCheckpoint();

        public MinecraftSourceHandoffCheckpoint CaptureAndCloseForHandoff () =>
            _session.CaptureAndCloseForHandoff();

        public void StartMonitoring () => _session.StartMonitoring();

        public void Dispose ()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            try
            {
                _session.Dispose();
            }
            finally
            {
                _session.EventsProduced -= ForwardEvents;
                _session.HandoffCheckpointCaptured -= ForwardHandoffCheckpoint;
                _reader.Dispose();
            }
        }

        private void ForwardEvents (object? sender, MinecraftLiveSourceEventsProducedEventArgs args) =>
            EventsProduced?.Invoke(this, args);

        private void ForwardHandoffCheckpoint (object? sender, MinecraftWorkspaceHandoffCheckpointEventArgs args) =>
            _handoffCheckpointCaptured?.Invoke(this, args);
    }
}
