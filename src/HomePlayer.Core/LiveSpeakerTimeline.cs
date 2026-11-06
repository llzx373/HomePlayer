namespace HomePlayer.Core;

public sealed record LiveSpeakerSegment(int Speaker, double StartSeconds, double EndSeconds);

/// <summary>
/// 流式说话人时间线：记录已分析的说话人段和分离前沿，供实时配音分配角色。
/// </summary>
public sealed class LiveSpeakerTimeline
{
    private const double MergeGapSeconds = 0.35;
    private const double MinimumOverlapSeconds = 0.1;

    private readonly object _gate = new();
    private readonly List<LiveSpeakerSegment> _segments = [];
    private double _through;

    public double DiarizedThrough
    {
        get
        {
            lock (_gate)
                return _through;
        }
    }

    public IReadOnlyList<LiveSpeakerSegment> Snapshot()
    {
        lock (_gate)
            return _segments.ToArray();
    }

    public void SetThrough(double seconds)
    {
        lock (_gate)
            _through = Math.Max(_through, seconds);
    }

    public void Add(LiveSpeakerSegment segment)
    {
        if (segment.Speaker < 0 || segment.EndSeconds <= segment.StartSeconds)
            return;
        lock (_gate)
        {
            _segments.Add(segment);
            _segments.Sort((left, right) => left.StartSeconds.CompareTo(right.StartSeconds));
            MergeLocked();
            _through = Math.Max(_through, segment.EndSeconds);
        }
    }

    public int DominantSpeaker(double startSeconds, double endSeconds)
    {
        if (endSeconds <= startSeconds)
            return -1;
        lock (_gate)
        {
            var best = -1;
            var bestOverlap = MinimumOverlapSeconds;
            foreach (var segment in _segments)
            {
                if (segment.StartSeconds >= endSeconds)
                    break;
                var overlap = Math.Min(endSeconds, segment.EndSeconds) - Math.Max(startSeconds, segment.StartSeconds);
                if (overlap > bestOverlap)
                {
                    bestOverlap = overlap;
                    best = segment.Speaker;
                }
            }

            return best;
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _segments.Clear();
            _through = 0;
        }
    }

    private void MergeLocked()
    {
        var merged = new List<LiveSpeakerSegment>(_segments.Count);
        foreach (var segment in _segments)
        {
            if (merged.Count > 0)
            {
                var last = merged[^1];
                if (last.Speaker == segment.Speaker && segment.StartSeconds <= last.EndSeconds + MergeGapSeconds)
                {
                    merged[^1] = last with { EndSeconds = Math.Max(last.EndSeconds, segment.EndSeconds) };
                    continue;
                }
            }

            merged.Add(segment);
        }

        _segments.Clear();
        _segments.AddRange(merged);
    }
}
