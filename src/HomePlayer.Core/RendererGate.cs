namespace HomePlayer.Core;

public static class RendererGate
{
    public const int Stopped = 0;
    public const int Paused = 1;
    public const int Running = 2;
    public const int PrerollReady = 0;
    public const int PrerollWaiting = 1;

    /// <summary>
    /// 只有从停止进入暂停才等待第一帧。播放中暂停必须立刻完成，
    /// 否则托盘暂停会卡住；暂停后的后续帧仍由 <see cref="Decide"/> 扣住，不连续送画面。
    /// </summary>
    public static bool NeedsPreroll(int previousState, int nextState) =>
        previousState == Stopped && nextState == Paused;

    /// <summary>第一帧拿走预滚动名额，后面的样本不再抢。</summary>
    public static bool ConsumePreroll(ref int waiting) =>
        Interlocked.Exchange(ref waiting, PrerollReady) == PrerollWaiting;

    public static bool? Decide(int state, bool flushing, long streamTime)
    {
        if (flushing || state == Stopped)
            return false;
        if (state == Paused)
            return null;
        if (state != Running)
            return false;
        if (streamTime <= 0)
            return true;
        return null;
    }
}
