namespace HomePlayer.Core;

public sealed record LiveDubbingSegment(
    long Key,
    double StartSeconds,
    double EndSeconds,
    int SampleRate,
    short[] Samples);

public sealed record LiveDubbingSpeechSpan(double StartSeconds, double EndSeconds);

/// <summary>
/// 实时配音的动态段落表。写入方是后台合成任务，读取方是播放器的音频线程，
/// 用写时复制保证混音时不需要长时间持锁。
/// </summary>
public sealed class LiveDubbingTrack
{
    private const double MergeGapSeconds = 0.15;

    private readonly object _gate = new();
    private LiveDubbingSegment[] _segments = [];
    private LiveDubbingSpeechSpan[] _speech = [];
    private long _epoch;

    public long Epoch => Volatile.Read(ref _epoch);

    public LiveDubbingSegment[] Snapshot() => Volatile.Read(ref _segments);

    /// <summary>
    /// 已识别到的人声区间。混音按这里压低原声，而不是只看已经合成好的配音片段，
    /// 否则识别到但还没合成（或合成失败）的句子会漏出原音。
    /// </summary>
    public LiveDubbingSpeechSpan[] SpeechSnapshot() => Volatile.Read(ref _speech);

    public void Reset(double position)
    {
        lock (_gate)
        {
            _segments = [];
            _speech = [];
            Interlocked.Increment(ref _epoch);
        }
    }

    public void AddSpeech(double startSeconds, double endSeconds)
    {
        if (endSeconds <= startSeconds)
            return;

        lock (_gate)
        {
            var merged = new List<LiveDubbingSpeechSpan>(_speech.Length + 1);
            var start = startSeconds;
            var end = endSeconds;
            var inserted = false;
            foreach (var span in _speech)
            {
                if (span.EndSeconds < start - MergeGapSeconds)
                {
                    merged.Add(span);
                    continue;
                }

                if (span.StartSeconds > end + MergeGapSeconds)
                {
                    if (!inserted)
                    {
                        merged.Add(new LiveDubbingSpeechSpan(start, end));
                        inserted = true;
                    }

                    merged.Add(span);
                    continue;
                }

                start = Math.Min(start, span.StartSeconds);
                end = Math.Max(end, span.EndSeconds);
            }

            if (!inserted)
                merged.Add(new LiveDubbingSpeechSpan(start, end));
            _speech = [.. merged];
        }
    }

    /// <summary>
    /// 某句最终没有配音（翻译失败或被跳过）时撤掉它的压低区间，宁可放回原声也不要整段静音。
    /// </summary>
    public void RemoveSpeech(double startSeconds, double endSeconds)
    {
        if (endSeconds <= startSeconds)
            return;

        lock (_gate)
        {
            var next = new List<LiveDubbingSpeechSpan>(_speech.Length + 1);
            foreach (var span in _speech)
            {
                if (span.EndSeconds <= startSeconds || span.StartSeconds >= endSeconds)
                {
                    next.Add(span);
                    continue;
                }

                if (startSeconds - span.StartSeconds > 0.05)
                    next.Add(span with { EndSeconds = startSeconds });
                if (span.EndSeconds - endSeconds > 0.05)
                    next.Add(span with { StartSeconds = endSeconds });
            }

            _speech = [.. next];
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
            if (_segments.Length > 0 && _segments[0].EndSeconds < seconds)
                _segments = _segments.Where(segment => segment.EndSeconds >= seconds).ToArray();
            if (_speech.Length > 0 && _speech[0].EndSeconds < seconds)
                _speech = _speech.Where(span => span.EndSeconds >= seconds).ToArray();
        }
    }
}
