namespace HomePlayer.Core;

/// <summary>
/// 一条播放进度记录，主键是文件绝对路径。
/// </summary>
public sealed record PlaybackState(
    string Path,
    double PositionSeconds,
    double DurationSeconds,
    bool Finished,
    long UpdatedUtcTicks);

public interface IPlaybackStateStore
{
    IReadOnlyDictionary<string, PlaybackState> LoadAll(int limit);

    PlaybackState? Load(string path);

    /// <summary>
    /// 只写内存里的待落库集合，调用方可以在任意线程调用。
    /// </summary>
    void Save(PlaybackState state);

    void Flush();
}

public static class PlaybackProgress
{
    /// <summary>
    /// 把当前位置换算成要落库的进度：看完的条目进度清零并标记完成。
    /// </summary>
    public static (double PositionSeconds, bool Finished) Evaluate(TimeSpan position, TimeSpan duration)
    {
        var resume = PlaybackResume.Position(position, duration);
        if (resume != null)
            return (resume.Value.TotalSeconds, false);
        var finished = position >= PlaybackResume.Minimum
            && duration > TimeSpan.Zero
            && duration - position <= PlaybackResume.EndGrace;
        return (0, finished);
    }
}
