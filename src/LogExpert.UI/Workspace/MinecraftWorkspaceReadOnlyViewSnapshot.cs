using System.Globalization;

using LogExpert.Core.Classes.MinecraftLogs;

namespace LogExpert.UI.Workspace;

/// <summary>Creates a display-only snapshot from an already evaluated workspace filter result.</summary>
public static class MinecraftWorkspaceReadOnlyViewPresenter
{
    public static MinecraftWorkspaceReadOnlyViewSnapshot CreateSnapshot (
        string workspaceDisplayName,
        MinecraftWorkspaceTimelineFilterResult filterResult)
    {
        ArgumentNullException.ThrowIfNull(workspaceDisplayName);
        ArgumentNullException.ThrowIfNull(filterResult);

        MinecraftWorkspaceReadOnlyRow[] rows = filterResult.MatchedEntries
            .Select(entry => new MinecraftWorkspaceReadOnlyRow(entry))
            .ToArray();

        return new MinecraftWorkspaceReadOnlyViewSnapshot(
            workspaceDisplayName,
            filterResult.QuerySnapshot,
            filterResult.Status,
            filterResult.TotalLoadedCount,
            filterResult.MatchedCount,
            filterResult.ErrorDetail,
            Array.AsReadOnly(rows),
            new MinecraftWorkspaceReadOnlyFacetSnapshot(
                MapBuckets(filterResult.FacetBuckets.FileIds, value => value),
                MapBuckets(filterResult.FacetBuckets.Sources, DisplayStringFacet),
                MapBuckets(filterResult.FacetBuckets.Components, DisplayStringFacet),
                MapBuckets(filterResult.FacetBuckets.Levels, DisplayLevelFacet),
                MapBuckets(filterResult.FacetBuckets.Threads, DisplayStringFacet)));
    }

    private static IReadOnlyList<MinecraftWorkspaceFacetDisplayItem<TValue>> MapBuckets<TValue> (
        IReadOnlyList<MinecraftWorkspaceFacetBucket<TValue>> buckets,
        Func<TValue, string> getLabel)
    {
        MinecraftWorkspaceFacetDisplayItem<TValue>[] items = buckets
            .Select(bucket => new MinecraftWorkspaceFacetDisplayItem<TValue>(bucket, getLabel(bucket.Value)))
            .ToArray();

        return Array.AsReadOnly(items);
    }

    private static string DisplayStringFacet (MinecraftWorkspaceFacetValue<string> value) => value.IsUnknown
        ? "Unknown"
        : value.Value == "Unknown" ? "\"Unknown\" (literal)" : value.Value;

    private static string DisplayLevelFacet (MinecraftWorkspaceFacetValue<LogLevel> value) =>
        value.IsUnknown ? "Unknown" : MinecraftWorkspaceReadOnlyDisplay.Level(value.Value);
}

public sealed class MinecraftWorkspaceReadOnlyViewSnapshot
{
    internal MinecraftWorkspaceReadOnlyViewSnapshot (
        string workspaceDisplayName,
        MinecraftWorkspaceTimelineFilterQuery querySnapshot,
        MinecraftWorkspaceFilterStatus filterStatus,
        int totalLoadedCount,
        int? matchedCount,
        string? errorText,
        IReadOnlyList<MinecraftWorkspaceReadOnlyRow> rows,
        MinecraftWorkspaceReadOnlyFacetSnapshot facets)
    {
        WorkspaceDisplayName = workspaceDisplayName;
        QuerySnapshot = querySnapshot;
        FilterStatus = filterStatus;
        TotalLoadedCount = totalLoadedCount;
        MatchedCount = matchedCount;
        ErrorText = errorText;
        Rows = rows;
        Facets = facets;
    }

    public string WorkspaceDisplayName { get; }

    /// <summary>The exact immutable query used to produce this snapshot.</summary>
    public MinecraftWorkspaceTimelineFilterQuery QuerySnapshot { get; }

    public MinecraftWorkspaceFilterStatus FilterStatus { get; }

    public int TotalLoadedCount { get; }

    /// <summary>Null when the filter result is incomplete due to a regex error or timeout.</summary>
    public int? MatchedCount { get; }

    public string? ErrorText { get; }

    public IReadOnlyList<MinecraftWorkspaceReadOnlyRow> Rows { get; }

    public MinecraftWorkspaceReadOnlyFacetSnapshot Facets { get; }
}

public sealed class MinecraftWorkspaceReadOnlyRow
{
    internal MinecraftWorkspaceReadOnlyRow (MinecraftWorkspaceTimelineEntry entry)
    {
        Entry = entry;
        Identity = entry.Identity;
        Time = MinecraftWorkspaceReadOnlyDisplay.UtcTime(entry.CandidateTimestampUtc);
        Source = MinecraftWorkspaceReadOnlyDisplay.AttributedString(entry.IngressEvent.Event.Source);
        Component = MinecraftWorkspaceReadOnlyDisplay.AttributedString(entry.IngressEvent.Event.Component);
        Level = entry.IngressEvent.Event.Level.TryGetValue(out LogLevel level)
            ? MinecraftWorkspaceReadOnlyDisplay.Level(level)
            : "Unknown";
        Thread = MinecraftWorkspaceReadOnlyDisplay.AttributedString(entry.IngressEvent.Event.Thread);
        Message = MinecraftWorkspaceReadOnlyDisplay.SingleRowMessage(entry.IngressEvent.Event.Message);
        IsLate = entry.IsLate;
        WasTimestampAdjustedForSourceOrder = entry.WasTimestampAdjustedForSourceOrder;
        Details = new MinecraftWorkspaceReadOnlyEventDetails(entry);
    }

    /// <summary>The exact timeline entry reference supplied by the filter result.</summary>
    public MinecraftWorkspaceTimelineEntry Entry { get; }

    public long Identity { get; }

    public string Time { get; }

    public string Source { get; }

    public string Component { get; }

    public string Level { get; }

    public string Thread { get; }

    /// <summary>One-row display text; the full message and raw text remain available in Details.</summary>
    public string Message { get; }

    public bool IsLate { get; }

    public bool WasTimestampAdjustedForSourceOrder { get; }

    public MinecraftWorkspaceReadOnlyEventDetails Details { get; }
}

/// <summary>Read-only details that retain direct references to the selected core event and provenance objects.</summary>
public sealed class MinecraftWorkspaceReadOnlyEventDetails
{
    internal MinecraftWorkspaceReadOnlyEventDetails (MinecraftWorkspaceTimelineEntry entry)
    {
        Entry = entry;
        Event = entry.IngressEvent.Event;
        EventRef = Event.Ref;
        FileRef = EventRef.File;
        Message = Event.Message;
        RawText = Event.RawText;
        PhysicalPath = FileRef.Path;
        FileId = FileRef.FileId;
        Generation = FileRef.Generation;
        StartByteOffset = EventRef.StartByteOffset;
        EndByteOffset = EventRef.EndByteOffset;
        StartLineNumber = EventRef.StartLineNumber;
        EndLineNumber = EventRef.EndLineNumber;
        SourceLocalSequence = EventRef.SourceLocalSequence;
        ProducerSequence = Event.ProducerSequence;
        Timestamp = Event.Timestamp;
        TimestampBasis = entry.TimestampBasis;
        CandidateTimestampUtc = entry.CandidateTimestampUtc;
        EffectiveTimestampUtc = entry.EffectiveTimestampUtc;
        IngestedAtUtc = entry.IngressEvent.IngestedAtUtc;
        IsLate = entry.IsLate;
        WasTimestampAdjustedForSourceOrder = entry.WasTimestampAdjustedForSourceOrder;
        ParseStatus = Event.ParseStatus;
        SourceAttribution = Event.Source;
        ComponentAttribution = Event.Component;
        LevelAttribution = Event.Level;
        ThreadAttribution = Event.Thread;
    }

    public MinecraftWorkspaceTimelineEntry Entry { get; }

    public NormalizedLogEvent Event { get; }

    public EventRef EventRef { get; }

    public FileRef FileRef { get; }

    public string Message { get; }

    public string RawText { get; }

    public string PhysicalPath { get; }

    public string FileId { get; }

    public long Generation { get; }

    public long StartByteOffset { get; }

    public long EndByteOffset { get; }

    public long StartLineNumber { get; }

    public long EndLineNumber { get; }

    public long SourceLocalSequence { get; }

    public long? ProducerSequence { get; }

    public EventTimestamp Timestamp { get; }

    public MinecraftWorkspaceTimelineTimestampBasis TimestampBasis { get; }

    public DateTimeOffset CandidateTimestampUtc { get; }

    public DateTimeOffset EffectiveTimestampUtc { get; }

    public DateTimeOffset IngestedAtUtc { get; }

    public bool IsLate { get; }

    public bool WasTimestampAdjustedForSourceOrder { get; }

    public EventParseStatus ParseStatus { get; }

    public AttributedValue<string> SourceAttribution { get; }

    public AttributedValue<string> ComponentAttribution { get; }

    public AttributedValue<LogLevel> LevelAttribution { get; }

    public AttributedValue<string> ThreadAttribution { get; }
}

public sealed class MinecraftWorkspaceReadOnlyFacetSnapshot
{
    internal MinecraftWorkspaceReadOnlyFacetSnapshot (
        IReadOnlyList<MinecraftWorkspaceFacetDisplayItem<string>> fileIds,
        IReadOnlyList<MinecraftWorkspaceFacetDisplayItem<MinecraftWorkspaceFacetValue<string>>> sources,
        IReadOnlyList<MinecraftWorkspaceFacetDisplayItem<MinecraftWorkspaceFacetValue<string>>> components,
        IReadOnlyList<MinecraftWorkspaceFacetDisplayItem<MinecraftWorkspaceFacetValue<LogLevel>>> levels,
        IReadOnlyList<MinecraftWorkspaceFacetDisplayItem<MinecraftWorkspaceFacetValue<string>>> threads)
    {
        FileIds = fileIds;
        Sources = sources;
        Components = components;
        Levels = levels;
        Threads = threads;
    }

    public IReadOnlyList<MinecraftWorkspaceFacetDisplayItem<string>> FileIds { get; }

    public IReadOnlyList<MinecraftWorkspaceFacetDisplayItem<MinecraftWorkspaceFacetValue<string>>> Sources { get; }

    public IReadOnlyList<MinecraftWorkspaceFacetDisplayItem<MinecraftWorkspaceFacetValue<string>>> Components { get; }

    public IReadOnlyList<MinecraftWorkspaceFacetDisplayItem<MinecraftWorkspaceFacetValue<LogLevel>>> Levels { get; }

    public IReadOnlyList<MinecraftWorkspaceFacetDisplayItem<MinecraftWorkspaceFacetValue<string>>> Threads { get; }
}

public sealed class MinecraftWorkspaceFacetDisplayItem<TValue>
{
    internal MinecraftWorkspaceFacetDisplayItem (MinecraftWorkspaceFacetBucket<TValue> bucket, string displayLabel)
    {
        Bucket = bucket;
        DisplayLabel = displayLabel;
    }

    /// <summary>The original YEE-50 bucket, including its exact typed facet value.</summary>
    public MinecraftWorkspaceFacetBucket<TValue> Bucket { get; }

    public TValue Value => Bucket.Value;

    public string DisplayLabel { get; }

    public int TotalCount => Bucket.TotalCount;

    public int? MatchingCount => Bucket.MatchingCount;
}

internal static class MinecraftWorkspaceReadOnlyDisplay
{
    internal static string AttributedString (AttributedValue<string> value) =>
        value.TryGetValue(out string? knownValue) ? knownValue! : "Unknown";

    internal static string UtcTime (DateTimeOffset timestampUtc) =>
        timestampUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    internal static string SingleRowMessage (string message) => message
        .Replace("\r\n", " ⏎ ", StringComparison.Ordinal)
        .Replace("\r", " ⏎ ", StringComparison.Ordinal)
        .Replace("\n", " ⏎ ", StringComparison.Ordinal);

    internal static string Level (LogLevel level) => level switch
    {
        LogLevel.Trace => "TRACE",
        LogLevel.Debug => "DEBUG",
        LogLevel.Info => "INFO",
        LogLevel.Warn => "WARN",
        LogLevel.Error => "ERROR",
        LogLevel.Fatal => "FATAL",
        _ => "Unknown"
    };
}
