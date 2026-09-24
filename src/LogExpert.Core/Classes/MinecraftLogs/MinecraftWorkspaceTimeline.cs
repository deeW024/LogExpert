namespace LogExpert.Core.Classes.MinecraftLogs;

public enum MinecraftWorkspaceTimelineTimestampBasis
{
    Source,
    EventIngest,
    WorkspaceIngressFallback
}

/// <summary>An immutable timeline view over one unchanged workspace ingress envelope.</summary>
public sealed record MinecraftWorkspaceTimelineEntry (
    MinecraftWorkspaceIngressEvent IngressEvent,
    DateTimeOffset CandidateTimestampUtc,
    DateTimeOffset EffectiveTimestampUtc,
    MinecraftWorkspaceTimelineTimestampBasis TimestampBasis,
    bool IsLate,
    bool WasTimestampAdjustedForSourceOrder)
{
    /// <summary>Stable identity for this timeline entry.</summary>
    public long Identity => IngressEvent.IngressSequence;
}

/// <summary>
/// Merges source-ordered workspace ingress tracks into a deterministic chronological snapshot.
/// It owns no readers, discovery, or background ingestion.
/// </summary>
public sealed class MinecraftWorkspaceTimeline
{
    private static readonly IComparer<TimelinePriority> PriorityComparer = new TimelinePriorityComparer();

    private readonly string _workspaceId;
    private readonly object _gate = new();
    private readonly Dictionary<TrackKey, List<TrackItem>> _tracks = new();
    private readonly Dictionary<long, MinecraftWorkspaceIngressEvent> _ingressEvents = new();
    private DateTimeOffset? _watermarkUtc;
    private int _lateCount;

    public MinecraftWorkspaceTimeline (string workspaceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        _workspaceId = workspaceId;
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _ingressEvents.Count;
            }
        }
    }

    /// <summary>Maximum candidate UTC timestamp committed by an accepted batch.</summary>
    public DateTimeOffset? WatermarkUtc
    {
        get
        {
            lock (_gate)
            {
                return _watermarkUtc;
            }
        }
    }

    public int LateCount
    {
        get
        {
            lock (_gate)
            {
                return _lateCount;
            }
        }
    }

    /// <summary>
    /// Adds one drained ingress batch. Late classification compares against the watermark
    /// from before this call, so records in one initial/backfill batch do not mark each other late.
    /// </summary>
    public void AppendBatch (IReadOnlyList<MinecraftWorkspaceIngressEvent> ingressEvents)
    {
        ArgumentNullException.ThrowIfNull(ingressEvents);

        lock (_gate)
        {
            var batchBySequence = new Dictionary<long, MinecraftWorkspaceIngressEvent>();
            foreach (MinecraftWorkspaceIngressEvent ingressEvent in ingressEvents)
            {
                ArgumentNullException.ThrowIfNull(ingressEvent);
                if (!string.Equals(ingressEvent.WorkspaceId, _workspaceId, StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        string.Concat(nameof(MinecraftWorkspaceIngressEvent.WorkspaceId), " does not match the timeline workspace."),
                        nameof(ingressEvents));
                }

                if (_ingressEvents.TryGetValue(ingressEvent.IngressSequence, out MinecraftWorkspaceIngressEvent? accepted))
                {
                    if (!AreExactDuplicates(accepted, ingressEvent))
                    {
                        throw new InvalidOperationException(
                            $"Ingress sequence {ingressEvent.IngressSequence} was reused with different event data.");
                    }

                    continue;
                }

                if (batchBySequence.TryGetValue(ingressEvent.IngressSequence, out MinecraftWorkspaceIngressEvent? staged))
                {
                    if (!AreExactDuplicates(staged, ingressEvent))
                    {
                        throw new InvalidOperationException(
                            $"Ingress sequence {ingressEvent.IngressSequence} was reused with different event data.");
                    }

                    continue;
                }

                batchBySequence.Add(ingressEvent.IngressSequence, ingressEvent);
            }

            if (batchBySequence.Count == 0)
            {
                return;
            }

            DateTimeOffset? watermarkAtBatchStart = _watermarkUtc;
            var additionsByTrack = new Dictionary<TrackKey, List<TrackItem>>();
            foreach (MinecraftWorkspaceIngressEvent ingressEvent in batchBySequence.Values)
            {
                (DateTimeOffset candidateUtc, MinecraftWorkspaceTimelineTimestampBasis basis) = GetCandidateTimestamp(ingressEvent);
                long trackSequence = ingressEvent.Event.ProducerSequence ?? ingressEvent.Event.Ref.SourceLocalSequence;
                var trackKey = ingressEvent.Event.ProducerSequence is long
                    ? new TrackKey(true, ingressEvent.SourceId)
                    : new TrackKey(false, ingressEvent.FileId);
                var entry = new MinecraftWorkspaceTimelineEntry(
                    ingressEvent,
                    candidateUtc,
                    candidateUtc,
                    basis,
                    watermarkAtBatchStart is DateTimeOffset watermark && candidateUtc < watermark,
                    WasTimestampAdjustedForSourceOrder: false);
                if (!additionsByTrack.TryGetValue(trackKey, out List<TrackItem>? additions))
                {
                    additions = [];
                    additionsByTrack.Add(trackKey, additions);
                }

                additions.Add(new TrackItem(trackSequence, entry));
            }

            foreach ((TrackKey key, List<TrackItem> additions) in additionsByTrack)
            {
                if (!_tracks.TryGetValue(key, out List<TrackItem>? track))
                {
                    track = [];
                    _tracks.Add(key, track);
                }

                long firstChangedSequence = long.MaxValue;
                foreach (TrackItem item in additions)
                {
                    firstChangedSequence = Math.Min(firstChangedSequence, item.TrackSequence);
                    InsertByTrackSequence(track, item);
                }

                int suffixStart = FindFirstSequenceAtLeast(track, firstChangedSequence);
                RecomputeTrackSuffix(track, suffixStart);
            }

            foreach ((long sequence, MinecraftWorkspaceIngressEvent ingressEvent) in batchBySequence)
            {
                _ingressEvents.Add(sequence, ingressEvent);
            }

            _lateCount += additionsByTrack.Values.SelectMany(items => items).Count(item => item.Entry.IsLate);
            DateTimeOffset latestCandidate = batchBySequence.Values
                .Select(ingressEvent => GetCandidateTimestamp(ingressEvent).TimestampUtc)
                .Max();
            _watermarkUtc = _watermarkUtc is DateTimeOffset current && current > latestCandidate
                ? current
                : latestCandidate;
        }
    }

    /// <summary>Returns a fresh immutable snapshot merged by a deterministic priority queue.</summary>
    public IReadOnlyList<MinecraftWorkspaceTimelineEntry> GetOrderedSnapshot ()
    {
        lock (_gate)
        {
            var queue = new PriorityQueue<TrackCursor, TimelinePriority>(PriorityComparer);
            foreach (List<TrackItem> track in _tracks.Values)
            {
                if (track.Count > 0)
                {
                    queue.Enqueue(new TrackCursor(track, 0), GetPriority(track[0]));
                }
            }

            var ordered = new MinecraftWorkspaceTimelineEntry[_ingressEvents.Count];
            int outputIndex = 0;
            while (queue.TryDequeue(out TrackCursor cursor, out _))
            {
                TrackItem current = cursor.Track[cursor.Index];
                ordered[outputIndex++] = current.Entry;

                int nextIndex = cursor.Index + 1;
                if (nextIndex < cursor.Track.Count)
                {
                    TrackItem next = cursor.Track[nextIndex];
                    queue.Enqueue(new TrackCursor(cursor.Track, nextIndex), GetPriority(next));
                }
            }

            return Array.AsReadOnly(ordered);
        }
    }

    private static (DateTimeOffset TimestampUtc, MinecraftWorkspaceTimelineTimestampBasis Basis) GetCandidateTimestamp (
        MinecraftWorkspaceIngressEvent ingressEvent)
    {
        EventTimestamp timestamp = ingressEvent.Event.Timestamp;
        if (timestamp.Provenance == TimestampProvenance.Source && timestamp.Value is DateTimeOffset sourceValue)
        {
            return (sourceValue.ToUniversalTime(), MinecraftWorkspaceTimelineTimestampBasis.Source);
        }

        if (timestamp.Provenance == TimestampProvenance.Ingest && timestamp.Value is DateTimeOffset eventIngestValue)
        {
            return (eventIngestValue.ToUniversalTime(), MinecraftWorkspaceTimelineTimestampBasis.EventIngest);
        }

        return (
            ingressEvent.IngestedAtUtc.ToUniversalTime(),
            MinecraftWorkspaceTimelineTimestampBasis.WorkspaceIngressFallback);
    }

    private static bool AreExactDuplicates (
        MinecraftWorkspaceIngressEvent first,
        MinecraftWorkspaceIngressEvent second)
    {
        if (!first.Equals(second) || !first.IngestedAtUtc.EqualsExact(second.IngestedAtUtc))
        {
            return false;
        }

        DateTimeOffset? firstTimestamp = first.Event.Timestamp.Value;
        DateTimeOffset? secondTimestamp = second.Event.Timestamp.Value;
        return firstTimestamp is null
            ? secondTimestamp is null
            : secondTimestamp is DateTimeOffset value && firstTimestamp.Value.EqualsExact(value);
    }

    private static void InsertByTrackSequence (List<TrackItem> track, TrackItem item)
    {
        int low = 0;
        int high = track.Count;
        while (low < high)
        {
            int middle = low + ((high - low) / 2);
            if (track[middle].TrackSequence <= item.TrackSequence)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        track.Insert(low, item);
    }

    private static int FindFirstSequenceAtLeast (List<TrackItem> track, long sequence)
    {
        int low = 0;
        int high = track.Count;
        while (low < high)
        {
            int middle = low + ((high - low) / 2);
            if (track[middle].TrackSequence < sequence)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    private static void RecomputeTrackSuffix (List<TrackItem> track, int start)
    {
        if (start < 0)
        {
            return;
        }

        DateTimeOffset? previousEffectiveUtc = start == 0 ? null : track[start - 1].Entry.EffectiveTimestampUtc;
        int groupStart = start;
        while (groupStart < track.Count)
        {
            long sequence = track[groupStart].TrackSequence;
            int groupEnd = groupStart + 1;
            while (groupEnd < track.Count && track[groupEnd].TrackSequence == sequence)
            {
                groupEnd++;
            }

            DateTimeOffset? groupFloor = previousEffectiveUtc;
            for (int index = groupStart; index < groupEnd; index++)
            {
                TrackItem item = track[index];
                DateTimeOffset effectiveUtc = groupFloor is DateTimeOffset floor && floor > item.Entry.CandidateTimestampUtc
                    ? floor
                    : item.Entry.CandidateTimestampUtc;
                item.Entry = item.Entry with
                {
                    EffectiveTimestampUtc = effectiveUtc,
                    WasTimestampAdjustedForSourceOrder = effectiveUtc > item.Entry.CandidateTimestampUtc
                };
            }

            track.Sort(groupStart, groupEnd - groupStart, TrackPriorityComparer.Instance);
            previousEffectiveUtc = track[groupEnd - 1].Entry.EffectiveTimestampUtc;
            groupStart = groupEnd;
        }
    }

    private static TimelinePriority GetPriority (TrackItem item)
    {
        MinecraftWorkspaceTimelineEntry entry = item.Entry;
        NormalizedLogEvent parsedEvent = entry.IngressEvent.Event;
        return new TimelinePriority(
            entry.EffectiveTimestampUtc,
            item.TrackSequence,
            entry.IngressEvent.FileId,
            parsedEvent.Ref.File.Generation,
            parsedEvent.Ref.StartByteOffset,
            entry.IngressEvent.IngressSequence);
    }

    private readonly record struct TrackKey (bool IsProducerTrack, string Id);

    private sealed class TrackItem (long trackSequence, MinecraftWorkspaceTimelineEntry entry)
    {
        public long TrackSequence { get; } = trackSequence;

        public MinecraftWorkspaceTimelineEntry Entry { get; set; } = entry;
    }

    private readonly record struct TrackCursor (List<TrackItem> Track, int Index);

    private readonly record struct TimelinePriority (
        DateTimeOffset EffectiveTimestampUtc,
        long TrackSequence,
        string FileId,
        long Generation,
        long StartByteOffset,
        long IngressSequence);

    private sealed class TimelinePriorityComparer : IComparer<TimelinePriority>
    {
        public int Compare (TimelinePriority first, TimelinePriority second)
        {
            int comparison = first.EffectiveTimestampUtc.CompareTo(second.EffectiveTimestampUtc);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = first.TrackSequence.CompareTo(second.TrackSequence);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = StringComparer.Ordinal.Compare(first.FileId, second.FileId);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = first.Generation.CompareTo(second.Generation);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = first.StartByteOffset.CompareTo(second.StartByteOffset);
            return comparison != 0 ? comparison : first.IngressSequence.CompareTo(second.IngressSequence);
        }
    }

    private sealed class TrackPriorityComparer : IComparer<TrackItem>
    {
        public static TrackPriorityComparer Instance { get; } = new();

        public int Compare (TrackItem? first, TrackItem? second)
        {
            if (ReferenceEquals(first, second))
            {
                return 0;
            }

            if (first is null)
            {
                return -1;
            }

            if (second is null)
            {
                return 1;
            }

            return PriorityComparer.Compare(GetPriority(first), GetPriority(second));
        }
    }
}
