namespace HomePlayer.Playback;

public sealed partial class PlaybackController
{
    public TimeSpan? LoopA { get; private set; }
    public TimeSpan? LoopB { get; private set; }

    public void SetLoopA()
    {
        LoopA = Position;
        if (LoopB <= LoopA)
            LoopB = null;
        Notify();
    }

    public void SetLoopB()
    {
        if (LoopA == null || Position <= LoopA)
        {
            Status = "请先设置早于当前位置的 A 点。";
            Notify();
            return;
        }
        LoopB = Position;
        Notify();
    }

    public void ClearLoop()
    {
        LoopA = null;
        LoopB = null;
        Notify();
    }

    public void EnforceLoop()
    {
        if (LoopA is not TimeSpan start || LoopB is not TimeSpan end || end <= start)
            return;
        if (Position >= end || Position < start)
            Seek(start);
    }
}
