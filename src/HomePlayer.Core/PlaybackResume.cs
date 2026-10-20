namespace HomePlayer.Core;

public static class PlaybackResume
{
    public static readonly TimeSpan Minimum = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan EndGrace = TimeSpan.FromSeconds(5);

    public static TimeSpan? Position(TimeSpan position, TimeSpan duration)
    {
        if (position < Minimum)
            return null;
        if (duration > TimeSpan.Zero && duration - position <= EndGrace)
            return null;
        return position;
    }
}
