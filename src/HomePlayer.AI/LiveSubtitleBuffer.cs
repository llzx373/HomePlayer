using HomePlayer.Core;

namespace HomePlayer.AI;

public sealed record SubtitleSegmentUpdate(int SegmentId, bool Final, IReadOnlyList<SubtitleCue> Cues, double EndSeconds = 0);

public sealed class LiveSubtitleBuffer
{
    private readonly SortedDictionary<int, Segment> _segments = [];
    private readonly HashSet<SubtitleCue> _provisional = new(ReferenceEqualityComparer.Instance);
    private IReadOnlyList<SubtitleCue>? _cues;
    private IReadOnlyList<SubtitleCue>? _finalCues;

    public IReadOnlyList<SubtitleCue> Cues => _cues ??= Flatten(finalOnly: false);

    public IReadOnlyList<SubtitleCue> FinalCues => _finalCues ??= Flatten(finalOnly: true);

    public bool Apply(SubtitleSegmentUpdate update)
    {
        if (update.Cues.Count == 0)
        {
            if (!_segments.TryGetValue(update.SegmentId, out var existing))
                return false;
            if (existing.Final && !update.Final)
                return false;
            _segments.Remove(update.SegmentId);
            Invalidate();
            return true;
        }

        if (_segments.TryGetValue(update.SegmentId, out var current))
        {
            if (current.Final && !update.Final)
                return false;
            if (current.Final == update.Final && SameCues(current.Cues, update.Cues))
                return false;
        }

        _segments[update.SegmentId] = new Segment(update.Final, update.Cues.ToArray());
        Invalidate();
        return true;
    }

    public bool IsProvisional(SubtitleCue cue) => _provisional.Contains(cue);

    public void Clear()
    {
        _segments.Clear();
        Invalidate();
    }

    private IReadOnlyList<SubtitleCue> Flatten(bool finalOnly)
    {
        var result = new List<SubtitleCue>();
        foreach (var segment in _segments.Values)
        {
            if (finalOnly && !segment.Final)
                continue;
            result.AddRange(segment.Cues);
        }
        return result;
    }

    private void Invalidate()
    {
        _cues = null;
        _finalCues = null;
        _provisional.Clear();
        foreach (var segment in _segments.Values)
        {
            if (segment.Final)
                continue;
            foreach (var cue in segment.Cues)
                _provisional.Add(cue);
        }
    }

    private static bool SameCues(IReadOnlyList<SubtitleCue> left, IReadOnlyList<SubtitleCue> right)
    {
        if (left.Count != right.Count)
            return false;
        for (var index = 0; index < left.Count; index++)
        {
            if (left[index].Start != right[index].Start
                || left[index].End != right[index].End
                || !string.Equals(left[index].Text, right[index].Text, StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    private sealed record Segment(bool Final, IReadOnlyList<SubtitleCue> Cues);
}
