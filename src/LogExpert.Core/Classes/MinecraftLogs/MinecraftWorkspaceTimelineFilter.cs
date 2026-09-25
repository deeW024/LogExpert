using System.Text.RegularExpressions;

using LogExpert.Core.Helpers;

namespace LogExpert.Core.Classes.MinecraftLogs;

public enum MinecraftWorkspaceTextScope
{
    Message,
    RawText,
    MessageOrRawText
}

/// <summary>A typed known facet value or a distinct Unknown value; Unknown is never a string sentinel.</summary>
public readonly struct MinecraftWorkspaceFacetValue<T> : IEquatable<MinecraftWorkspaceFacetValue<T>>
    where T : notnull
{
    private readonly T? _value;
    private readonly bool _hasValue;

    internal MinecraftWorkspaceFacetValue (T value)
    {
        _value = value;
        _hasValue = true;
    }

    public bool IsUnknown => !_hasValue;

    public T Value => _hasValue ? _value! : throw new InvalidOperationException();

    public bool Equals (MinecraftWorkspaceFacetValue<T> other) =>
        _hasValue == other._hasValue && (!_hasValue || EqualityComparer<T>.Default.Equals(_value!, other._value!));

    public override bool Equals (object? obj) => obj is MinecraftWorkspaceFacetValue<T> other && Equals(other);

    public override int GetHashCode () => _hasValue ? HashCode.Combine(true, EqualityComparer<T>.Default.GetHashCode(_value!)) : 0;

    public static bool operator == (MinecraftWorkspaceFacetValue<T> left, MinecraftWorkspaceFacetValue<T> right) => left.Equals(right);

    public static bool operator != (MinecraftWorkspaceFacetValue<T> left, MinecraftWorkspaceFacetValue<T> right) => !left.Equals(right);
}

public static class MinecraftWorkspaceFacetValues
{
    public static MinecraftWorkspaceFacetValue<T> Unknown<T> ()
        where T : notnull => default;

    public static MinecraftWorkspaceFacetValue<T> Known<T> (T value)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(value);
        return new MinecraftWorkspaceFacetValue<T>(value);
    }
}

/// <summary>
/// Immutable workspace query over normalized timeline entries. This is separate from legacy
/// FilterParams, which remains scoped to single-window filtering.
/// </summary>
public sealed class MinecraftWorkspaceTimelineFilterQuery
{
    public MinecraftWorkspaceTimelineFilterQuery (
        string? searchText = null,
        bool isRegex = false,
        bool isCaseSensitive = false,
        bool isInvert = false,
        MinecraftWorkspaceTextScope textScope = MinecraftWorkspaceTextScope.Message,
        IEnumerable<string>? fileIds = null,
        IEnumerable<MinecraftWorkspaceFacetValue<string>>? sources = null,
        IEnumerable<MinecraftWorkspaceFacetValue<string>>? components = null,
        IEnumerable<MinecraftWorkspaceFacetValue<LogLevel>>? levels = null,
        IEnumerable<MinecraftWorkspaceFacetValue<string>>? threads = null)
    {
        if (!Enum.IsDefined(textScope))
        {
            throw new ArgumentOutOfRangeException(nameof(textScope));
        }

        SearchText = searchText;
        IsRegex = isRegex;
        IsCaseSensitive = isCaseSensitive;
        IsInvert = isInvert;
        TextScope = textScope;
        FileIds = Freeze(fileIds);
        Sources = Freeze(sources);
        Components = Freeze(components);
        Levels = Freeze(levels);
        Threads = Freeze(threads);
    }

    public string? SearchText { get; }

    public bool IsRegex { get; }

    public bool IsCaseSensitive { get; }

    public bool IsInvert { get; }

    public MinecraftWorkspaceTextScope TextScope { get; }

    public IReadOnlyList<string> FileIds { get; }

    public IReadOnlyList<MinecraftWorkspaceFacetValue<string>> Sources { get; }

    public IReadOnlyList<MinecraftWorkspaceFacetValue<string>> Components { get; }

    public IReadOnlyList<MinecraftWorkspaceFacetValue<LogLevel>> Levels { get; }

    public IReadOnlyList<MinecraftWorkspaceFacetValue<string>> Threads { get; }

    private static IReadOnlyList<T> Freeze<T> (IEnumerable<T>? values)
    {
        T[] snapshot = values?.Distinct().ToArray() ?? [];
        return Array.AsReadOnly(snapshot);
    }

    private static IReadOnlyList<string> Freeze (IEnumerable<string>? values)
    {
        string[] snapshot = values?.ToArray() ?? [];
        if (snapshot.Any(value => value is null))
        {
            throw new ArgumentException(null, nameof(values));
        }

        return Array.AsReadOnly(snapshot.Distinct(StringComparer.Ordinal).ToArray());
    }
}

public enum MinecraftWorkspaceFilterStatus
{
    Success,
    InvalidRegex,
    RegexTimedOut
}

public sealed record MinecraftWorkspaceFacetBucket<TValue> (
    TValue Value,
    int TotalCount,
    int? MatchingCount);

public sealed class MinecraftWorkspaceTimelineFacetBuckets
{
    internal MinecraftWorkspaceTimelineFacetBuckets (
        IReadOnlyList<MinecraftWorkspaceFacetBucket<string>> fileIds,
        IReadOnlyList<MinecraftWorkspaceFacetBucket<MinecraftWorkspaceFacetValue<string>>> sources,
        IReadOnlyList<MinecraftWorkspaceFacetBucket<MinecraftWorkspaceFacetValue<string>>> components,
        IReadOnlyList<MinecraftWorkspaceFacetBucket<MinecraftWorkspaceFacetValue<LogLevel>>> levels,
        IReadOnlyList<MinecraftWorkspaceFacetBucket<MinecraftWorkspaceFacetValue<string>>> threads)
    {
        FileIds = fileIds;
        Sources = sources;
        Components = components;
        Levels = levels;
        Threads = threads;
    }

    public IReadOnlyList<MinecraftWorkspaceFacetBucket<string>> FileIds { get; }

    public IReadOnlyList<MinecraftWorkspaceFacetBucket<MinecraftWorkspaceFacetValue<string>>> Sources { get; }

    public IReadOnlyList<MinecraftWorkspaceFacetBucket<MinecraftWorkspaceFacetValue<string>>> Components { get; }

    public IReadOnlyList<MinecraftWorkspaceFacetBucket<MinecraftWorkspaceFacetValue<LogLevel>>> Levels { get; }

    public IReadOnlyList<MinecraftWorkspaceFacetBucket<MinecraftWorkspaceFacetValue<string>>> Threads { get; }
}

public sealed class MinecraftWorkspaceTimelineFilterResult
{
    internal MinecraftWorkspaceTimelineFilterResult (
        MinecraftWorkspaceTimelineFilterQuery querySnapshot,
        MinecraftWorkspaceFilterStatus status,
        IReadOnlyList<MinecraftWorkspaceTimelineEntry> matchedEntries,
        int totalLoadedCount,
        int? matchedCount,
        MinecraftWorkspaceTimelineFacetBuckets facetBuckets,
        string? errorDetail)
    {
        QuerySnapshot = querySnapshot;
        Status = status;
        MatchedEntries = matchedEntries;
        TotalLoadedCount = totalLoadedCount;
        MatchedCount = matchedCount;
        FacetBuckets = facetBuckets;
        ErrorDetail = errorDetail;
    }

    public MinecraftWorkspaceTimelineFilterQuery QuerySnapshot { get; }

    public MinecraftWorkspaceFilterStatus Status { get; }

    public IReadOnlyList<MinecraftWorkspaceTimelineEntry> MatchedEntries { get; }

    public int TotalLoadedCount { get; }

    /// <summary>Null when an invalid or timed-out regex made complete evaluation impossible.</summary>
    public int? MatchedCount { get; }

    public MinecraftWorkspaceTimelineFacetBuckets FacetBuckets { get; }

    public string? ErrorDetail { get; }
}

/// <summary>
/// Stateless filter-to-view evaluation over a supplied YEE-48 ordered snapshot. It does not
/// subscribe to readers, materialize files, or alter the timeline entries it returns.
/// Plain text uses the legacy ordinal substring semantics; regex uses RegexHelper's timeout
/// policy. Legacy FilterParams remains for single-window filtering, with a future UI adapter
/// free to map its controls into this immutable event-native query.
/// </summary>
public sealed class MinecraftWorkspaceTimelineFilter
{
    private static readonly StringComparer FacetStringOrder = StringComparer.OrdinalIgnoreCase;

    private readonly TimeSpan _regexTimeout;

    public MinecraftWorkspaceTimelineFilter ()
        : this(RegexHelper.DefaultTimeout)
    {
    }

    internal MinecraftWorkspaceTimelineFilter (TimeSpan regexTimeout)
    {
        if (regexTimeout <= TimeSpan.Zero && regexTimeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(regexTimeout));
        }

        _regexTimeout = regexTimeout;
    }

    public MinecraftWorkspaceTimelineFilterResult Evaluate (
        IReadOnlyList<MinecraftWorkspaceTimelineEntry> orderedSnapshot,
        MinecraftWorkspaceTimelineFilterQuery query)
    {
        ArgumentNullException.ThrowIfNull(orderedSnapshot);
        ArgumentNullException.ThrowIfNull(query);

        MinecraftWorkspaceTimelineEntry[] entries = orderedSnapshot.ToArray();
        EntryFacts[] facts = entries.Select(CreateFacts).ToArray();
        Dictionary<string, int> fileTotals = Count(facts, item => item.FileId, StringComparer.Ordinal);
        Dictionary<MinecraftWorkspaceFacetValue<string>, int> sourceTotals = Count(facts, item => item.Source);
        Dictionary<MinecraftWorkspaceFacetValue<string>, int> componentTotals = Count(facts, item => item.Component);
        Dictionary<MinecraftWorkspaceFacetValue<LogLevel>, int> levelTotals = Count(facts, item => item.Level);
        Dictionary<MinecraftWorkspaceFacetValue<string>, int> threadTotals = Count(facts, item => item.Thread);

        Regex? regex = null;
        if (!string.IsNullOrEmpty(query.SearchText) && query.IsRegex)
        {
            try
            {
                RegexOptions options = query.IsCaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
                regex = RegexHelper.CreateSafeRegex(query.SearchText, options, _regexTimeout);
            }
            catch (ArgumentException exception)
            {
                return CreateFailure(
                    query,
                    MinecraftWorkspaceFilterStatus.InvalidRegex,
                    exception.Message,
                    entries.Length,
                    fileTotals,
                    sourceTotals,
                    componentTotals,
                    levelTotals,
                    threadTotals);
            }
        }

        var sourceMatches = sourceTotals.Keys.ToDictionary(key => key, _ => 0);
        var componentMatches = componentTotals.Keys.ToDictionary(key => key, _ => 0);
        var levelMatches = levelTotals.Keys.ToDictionary(key => key, _ => 0);
        var threadMatches = threadTotals.Keys.ToDictionary(key => key, _ => 0);
        var fileMatches = fileTotals.Keys.ToDictionary(key => key, _ => 0, StringComparer.Ordinal);
        var matched = new List<MinecraftWorkspaceTimelineEntry>(entries.Length);
        bool hasTextConstraint = !string.IsNullOrEmpty(query.SearchText);

        try
        {
            foreach ((MinecraftWorkspaceTimelineEntry entry, EntryFacts item) in entries.Zip(facts))
            {
                bool textMatch = !hasTextConstraint || MatchesText(entry.IngressEvent.Event, query, regex!);
                if (hasTextConstraint && query.IsInvert)
                {
                    textMatch = !textMatch;
                }

                bool fileMatch = MatchesFileSelection(query.FileIds, item.FileId);
                bool sourceMatch = MatchesSelection(query.Sources, item.Source);
                bool componentMatch = MatchesSelection(query.Components, item.Component);
                bool levelMatch = MatchesSelection(query.Levels, item.Level);
                bool threadMatch = MatchesSelection(query.Threads, item.Thread);

                if (textMatch && fileMatch && sourceMatch && componentMatch && levelMatch && threadMatch)
                {
                    matched.Add(entry);
                }

                if (textMatch && fileMatch && componentMatch && levelMatch && threadMatch)
                {
                    sourceMatches[item.Source]++;
                }

                if (textMatch && fileMatch && sourceMatch && levelMatch && threadMatch)
                {
                    componentMatches[item.Component]++;
                }

                if (textMatch && fileMatch && sourceMatch && componentMatch && threadMatch)
                {
                    levelMatches[item.Level]++;
                }

                if (textMatch && fileMatch && sourceMatch && componentMatch && levelMatch)
                {
                    threadMatches[item.Thread]++;
                }

                if (textMatch && sourceMatch && componentMatch && levelMatch && threadMatch)
                {
                    fileMatches[item.FileId]++;
                }
            }
        }
        catch (RegexMatchTimeoutException exception)
        {
            return CreateFailure(
                query,
                MinecraftWorkspaceFilterStatus.RegexTimedOut,
                exception.Message,
                entries.Length,
                fileTotals,
                sourceTotals,
                componentTotals,
                levelTotals,
                threadTotals);
        }

        var buckets = CreateBuckets(
            fileTotals,
            fileMatches,
            sourceTotals,
            sourceMatches,
            componentTotals,
            componentMatches,
            levelTotals,
            levelMatches,
            threadTotals,
            threadMatches);
        return new MinecraftWorkspaceTimelineFilterResult(
            query,
            MinecraftWorkspaceFilterStatus.Success,
            Array.AsReadOnly(matched.ToArray()),
            entries.Length,
            matched.Count,
            buckets,
            errorDetail: null);
    }

    private static MinecraftWorkspaceTimelineFilterResult CreateFailure (
        MinecraftWorkspaceTimelineFilterQuery query,
        MinecraftWorkspaceFilterStatus status,
        string errorDetail,
        int totalLoadedCount,
        Dictionary<string, int> fileTotals,
        Dictionary<MinecraftWorkspaceFacetValue<string>, int> sourceTotals,
        Dictionary<MinecraftWorkspaceFacetValue<string>, int> componentTotals,
        Dictionary<MinecraftWorkspaceFacetValue<LogLevel>, int> levelTotals,
        Dictionary<MinecraftWorkspaceFacetValue<string>, int> threadTotals)
    {
        var buckets = CreateBuckets(
            fileTotals,
            null,
            sourceTotals,
            null,
            componentTotals,
            null,
            levelTotals,
            null,
            threadTotals,
            null);
        return new MinecraftWorkspaceTimelineFilterResult(
            query,
            status,
            Array.AsReadOnly(Array.Empty<MinecraftWorkspaceTimelineEntry>()),
            totalLoadedCount,
            matchedCount: null,
            buckets,
            errorDetail);
    }

    private static bool MatchesText (NormalizedLogEvent logEvent, MinecraftWorkspaceTimelineFilterQuery query, Regex regex)
    {
        string searchText = query.SearchText!;
        bool Matches (string value) => query.IsRegex
            ? regex.IsMatch(value)
            : value.Contains(
                searchText,
                query.IsCaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

        return query.TextScope switch
        {
            MinecraftWorkspaceTextScope.Message => Matches(logEvent.Message),
            MinecraftWorkspaceTextScope.RawText => Matches(logEvent.RawText),
            MinecraftWorkspaceTextScope.MessageOrRawText => Matches(logEvent.Message) || Matches(logEvent.RawText),
            _ => throw new InvalidOperationException()
        };
    }

    private static EntryFacts CreateFacts (MinecraftWorkspaceTimelineEntry entry)
    {
        NormalizedLogEvent logEvent = entry.IngressEvent.Event;
        return new EntryFacts(
            entry.IngressEvent.FileId,
            ReadFacet(logEvent.Source),
            ReadFacet(logEvent.Component),
            ReadFacet(logEvent.Level),
            ReadFacet(logEvent.Thread));
    }

    private static MinecraftWorkspaceFacetValue<T> ReadFacet<T> (AttributedValue<T> value)
        where T : notnull => value.TryGetValue(out T known)
            ? MinecraftWorkspaceFacetValues.Known(known)
            : MinecraftWorkspaceFacetValues.Unknown<T>();

    private static Dictionary<TKey, int> Count<TItem, TKey> (
        IEnumerable<TItem> items,
        Func<TItem, TKey> selector,
        IEqualityComparer<TKey>? comparer = null)
        where TKey : notnull
    {
        var counts = new Dictionary<TKey, int>(comparer);
        foreach (TItem item in items)
        {
            TKey key = selector(item);
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }

        return counts;
    }

    private static bool MatchesFileSelection (IReadOnlyList<string> selection, string fileId) =>
        selection.Count == 0 || selection.Contains(fileId, StringComparer.Ordinal);

    private static bool MatchesSelection<T> (
        IReadOnlyList<MinecraftWorkspaceFacetValue<T>> selection,
        MinecraftWorkspaceFacetValue<T> value)
        where T : notnull => selection.Count == 0 || selection.Contains(value);

    private static MinecraftWorkspaceTimelineFacetBuckets CreateBuckets (
        Dictionary<string, int> fileTotals,
        Dictionary<string, int>? fileMatching,
        Dictionary<MinecraftWorkspaceFacetValue<string>, int> sourceTotals,
        Dictionary<MinecraftWorkspaceFacetValue<string>, int>? sourceMatching,
        Dictionary<MinecraftWorkspaceFacetValue<string>, int> componentTotals,
        Dictionary<MinecraftWorkspaceFacetValue<string>, int>? componentMatching,
        Dictionary<MinecraftWorkspaceFacetValue<LogLevel>, int> levelTotals,
        Dictionary<MinecraftWorkspaceFacetValue<LogLevel>, int>? levelMatching,
        Dictionary<MinecraftWorkspaceFacetValue<string>, int> threadTotals,
        Dictionary<MinecraftWorkspaceFacetValue<string>, int>? threadMatching) =>
        new(
            CreateBuckets(fileTotals, fileMatching, StringComparer.Ordinal),
            CreateStringFacetBuckets(sourceTotals, sourceMatching),
            CreateStringFacetBuckets(componentTotals, componentMatching),
            CreateLevelBuckets(levelTotals, levelMatching),
            CreateStringFacetBuckets(threadTotals, threadMatching));

    private static IReadOnlyList<MinecraftWorkspaceFacetBucket<string>> CreateBuckets (
        Dictionary<string, int> totals,
        Dictionary<string, int>? matching,
        IComparer<string> comparer) =>
        Array.AsReadOnly(totals
            .OrderBy(pair => pair.Key, comparer)
            .Select(pair => new MinecraftWorkspaceFacetBucket<string>(
                pair.Key,
                pair.Value,
                matching?.GetValueOrDefault(pair.Key)))
            .ToArray());

    private static IReadOnlyList<MinecraftWorkspaceFacetBucket<MinecraftWorkspaceFacetValue<string>>> CreateStringFacetBuckets (
        Dictionary<MinecraftWorkspaceFacetValue<string>, int> totals,
        Dictionary<MinecraftWorkspaceFacetValue<string>, int>? matching) =>
        Array.AsReadOnly(totals
            .OrderBy(pair => pair.Key.IsUnknown)
            .ThenBy(pair => pair.Key.IsUnknown ? string.Empty : pair.Key.Value, FacetStringOrder)
            .ThenBy(pair => pair.Key.IsUnknown ? string.Empty : pair.Key.Value, StringComparer.Ordinal)
            .Select(pair => new MinecraftWorkspaceFacetBucket<MinecraftWorkspaceFacetValue<string>>(
                pair.Key,
                pair.Value,
                matching?.GetValueOrDefault(pair.Key)))
            .ToArray());

    private static IReadOnlyList<MinecraftWorkspaceFacetBucket<MinecraftWorkspaceFacetValue<LogLevel>>> CreateLevelBuckets (
        Dictionary<MinecraftWorkspaceFacetValue<LogLevel>, int> totals,
        Dictionary<MinecraftWorkspaceFacetValue<LogLevel>, int>? matching) =>
        Array.AsReadOnly(totals
            .OrderBy(pair => pair.Key.IsUnknown)
            .ThenBy(pair => pair.Key.IsUnknown ? int.MaxValue : (int)pair.Key.Value)
            .Select(pair => new MinecraftWorkspaceFacetBucket<MinecraftWorkspaceFacetValue<LogLevel>>(
                pair.Key,
                pair.Value,
                matching?.GetValueOrDefault(pair.Key)))
            .ToArray());

    private sealed record EntryFacts (
        string FileId,
        MinecraftWorkspaceFacetValue<string> Source,
        MinecraftWorkspaceFacetValue<string> Component,
        MinecraftWorkspaceFacetValue<LogLevel> Level,
        MinecraftWorkspaceFacetValue<string> Thread);
}
