namespace LogExpert.Core.Classes.MinecraftLogs;

public sealed class MinecraftLiveSourceEventsProducedEventArgs : EventArgs
{
    public MinecraftLiveSourceEventsProducedEventArgs (IReadOnlyList<NormalizedLogEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        Events = events;
    }

    public IReadOnlyList<NormalizedLogEvent> Events { get; }
}
