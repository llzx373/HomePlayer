namespace HomePlayer.Core;

public enum LiveDubbingCueState
{
    Pending = 0,
    Ready = 1,
    Done = 2,
    Skipped = 3
}

public sealed record LiveDubbingJob(long Key, double StartSeconds, double EndSeconds, string Text, int Speaker = -1);

/// <summary>
/// 实时配音逐句状态机：识别定稿先登记，译文和说话人都就绪后才排队合成，
/// 覆盖率用于判断播放器需要缓冲到哪个时间点。
/// </summary>
public sealed class LiveDubbingScheduler
{
    private const double LateTolerance = 0.5;

    private sealed class Entry(double start, double end)
    {
        public double Start { get; } = start;
        public double End { get; } = end;
        public string? Text { get; set; }
        public bool TranslationReady { get; set; }
        public bool SpeakerReady { get; set; }
        public int Speaker { get; set; } = -1;
        public LiveDubbingCueState State { get; set; } = LiveDubbingCueState.Pending;
    }

    private readonly object _gate = new();
    private readonly Dictionary<long, Entry> _entries = [];
    private readonly SortedSet<(double Start, long Key)> _order = [];
    private double _recognizedThrough;

    public void Reset(double position)
    {
        lock (_gate)
        {
            _entries.Clear();
            _order.Clear();
            _recognizedThrough = Math.Max(0, position);
        }
    }

    public void Track(long key, double startSeconds, double endSeconds)
    {
        if (endSeconds <= startSeconds)
            return;
        lock (_gate)
        {
            if (_entries.ContainsKey(key))
                return;
            _entries[key] = new Entry(startSeconds, endSeconds);
            _order.Add((startSeconds, key));
        }
    }

    public void Translate(long key, string? text)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var entry) || entry.State is LiveDubbingCueState.Done or LiveDubbingCueState.Skipped)
                return;
            if (string.IsNullOrWhiteSpace(text))
            {
                entry.Text = null;
                entry.State = LiveDubbingCueState.Skipped;
                return;
            }

            entry.Text = text.Trim();
            entry.TranslationReady = true;
            RefreshLocked(entry);
        }
    }

    public void AssignSpeaker(long key, int speaker)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var entry) || entry.State is LiveDubbingCueState.Done or LiveDubbingCueState.Skipped)
                return;
            entry.Speaker = speaker;
            entry.SpeakerReady = true;
            RefreshLocked(entry);
        }
    }

    /// <summary>
    /// 分离已经越过句子末尾仍没有匹配到说话人时，用默认音色继续，避免缓冲无限等待。
    /// </summary>
    public int ExpireSpeakers(double diarizedThrough, double graceSeconds)
    {
        lock (_gate)
        {
            var count = 0;
            foreach (var (_, key) in _order)
            {
                var entry = _entries[key];
                if (entry.State is LiveDubbingCueState.Done or LiveDubbingCueState.Skipped)
                    continue;
                if (entry.SpeakerReady || entry.End + graceSeconds > diarizedThrough)
                    continue;
                entry.Speaker = -1;
                entry.SpeakerReady = true;
                RefreshLocked(entry);
                count++;
            }

            return count;
        }
    }

    public void Skip(long key)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var entry) && entry.State is not (LiveDubbingCueState.Done or LiveDubbingCueState.Skipped))
                entry.State = LiveDubbingCueState.Skipped;
        }
    }

    public void Complete(long key)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var entry))
                entry.State = LiveDubbingCueState.Done;
        }
    }

    public void SetRecognizedThrough(double seconds)
    {
        lock (_gate)
            _recognizedThrough = Math.Max(_recognizedThrough, seconds);
    }

    /// <summary>
    /// 已完整处理到的时间点：识别前沿和最早未完成句子起点中的较小值。
    /// </summary>
    public double CoveredThrough
    {
        get
        {
            lock (_gate)
            {
                var boundary = double.PositiveInfinity;
                foreach (var (start, key) in _order)
                {
                    var state = _entries[key].State;
                    if (state is LiveDubbingCueState.Pending or LiveDubbingCueState.Ready)
                    {
                        boundary = start;
                        break;
                    }
                }

                return Math.Min(_recognizedThrough, boundary);
            }
        }
    }

    /// <summary>
    /// 返回播放头附近最早一句待合成台词；太晚的句子直接标记完成，避免无限等待。
    /// </summary>
    public LiveDubbingJob? Next(double playheadSeconds, double lookaheadSeconds)
    {
        lock (_gate)
        {
            foreach (var (_, key) in _order)
            {
                var entry = _entries[key];
                if (entry.State is LiveDubbingCueState.Done or LiveDubbingCueState.Skipped)
                    continue;
                if (entry.End <= playheadSeconds - LateTolerance)
                    entry.State = LiveDubbingCueState.Done;
            }

            foreach (var (start, key) in _order)
            {
                var entry = _entries[key];
                if (start > playheadSeconds + lookaheadSeconds)
                    break;
                if (entry.State != LiveDubbingCueState.Ready || entry.Text == null)
                    continue;
                if (entry.End <= playheadSeconds - LateTolerance)
                    continue;
                return new LiveDubbingJob(key, entry.Start, entry.End, entry.Text, entry.Speaker);
            }

            return null;
        }
    }

    private static void RefreshLocked(Entry entry)
    {
        if (entry.TranslationReady && entry.SpeakerReady)
            entry.State = LiveDubbingCueState.Ready;
    }
}
