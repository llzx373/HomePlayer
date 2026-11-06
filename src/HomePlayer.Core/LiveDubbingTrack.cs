namespace HomePlayer.Core;

public sealed record LiveDubbingSegment(
    long Key,
    double StartSeconds,
    double EndSeconds,
    int SampleRate,
    short[] Samples);

/// <summary>
/// 实时配音的动态段落表。写入方是后台合成任务，读取方是播放器的音频线程，
/// 用写时复制保证混音时不需要长时间持锁。
/// </summary>
public sealed class LiveDubbingTrack
{
    private readonly object _gate = new();
    private LiveDubbingSegment[] _segments = [];
    private long _epoch;

    public long Epoch => Volatile.Read(ref _epoch);

    public LiveDubbingSegment[] Snapshot() => Volatile.Read(ref _segments);

    public void Reset(double position)
    {
        lock (_gate)
        {
            _segments = [];
            Interlocked.Increment(ref _epoch);
        }
    }

    public void Add(LiveDubbingSegment segment)
    {
        if (segment.Samples.Length == 0 || segment.EndSeconds <= segment.StartSeconds || segment.SampleRate <= 0)
            return;

        lock (_gate)
        {
            var next = new List<LiveDubbingSegment>(_segments.Length + 1);
            foreach (var existing in _segments)
            {
                if (existing.Key != segment.Key)
                    next.Add(existing);
            }

            next.Add(segment);
            next.Sort((left, right) => left.StartSeconds.CompareTo(right.StartSeconds));
            _segments = [.. next];
        }
    }

    public void EvictBefore(double seconds)
    {
        lock (_gate)
        {
            if (_segments.Length == 0 || _segments[0].EndSeconds >= seconds)
                return;
            _segments = _segments.Where(segment => segment.EndSeconds >= seconds).ToArray();
        }
    }
}
