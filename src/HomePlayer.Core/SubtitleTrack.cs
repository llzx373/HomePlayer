namespace HomePlayer.Core;

/// <summary>
/// 一条字幕轨道的查询索引。<see cref="SubtitleCues.At"/> 每次从头线性扫描，
/// 在特效字幕（上万条额外 Dialogue）上会变成每秒十万级的比较。
/// 这里在载入时记录“到当前为止最大的结束时间”，于是可以用二分定位到
/// 第一个可能覆盖目标时刻的字幕，同时保持与线性扫描完全相同的返回结果。
/// 字幕没有按开始时间排序时自动退回线性扫描。
/// </summary>
public sealed class SubtitleTrack
{
    public static SubtitleTrack Empty { get; } = new([]);

    private readonly IReadOnlyList<SubtitleCue> _cues;
    private readonly TimeSpan[]? _maxEnd;

    public SubtitleTrack(IReadOnlyList<SubtitleCue> cues)
    {
        _cues = cues;
        _maxEnd = IsSortedByStart(cues) ? BuildMaxEnd(cues) : null;
    }

    public int Count => _cues.Count;

    public SubtitleCue? At(TimeSpan time)
    {
        var maxEnd = _maxEnd;
        if (maxEnd == null)
            return SubtitleCues.At(_cues, time);
        if (maxEnd.Length == 0)
            return null;

        var lo = 0;
        var hi = maxEnd.Length;
        while (lo < hi)
        {
            var mid = lo + ((hi - lo) >> 1);
            if (maxEnd[mid] > time)
                hi = mid;
            else
                lo = mid + 1;
        }

        if (lo >= _cues.Count)
            return null;
        var cue = _cues[lo];
        return cue.Start <= time && time < cue.End ? cue : null;
    }

    private static bool IsSortedByStart(IReadOnlyList<SubtitleCue> cues)
    {
        for (var i = 1; i < cues.Count; i++)
        {
            if (cues[i].Start < cues[i - 1].Start)
                return false;
        }

        return true;
    }

    private static TimeSpan[] BuildMaxEnd(IReadOnlyList<SubtitleCue> cues)
    {
        var result = new TimeSpan[cues.Count];
        var max = TimeSpan.MinValue;
        for (var i = 0; i < cues.Count; i++)
        {
            var end = cues[i].End;
            if (end > max)
                max = end;
            result[i] = max;
        }

        return result;
    }
}
