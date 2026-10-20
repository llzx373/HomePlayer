namespace HomePlayer.Core;

public static class RendererGate
{
    public const int Stopped = 0;
    public const int Paused = 1;
    public const int Running = 2;

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
