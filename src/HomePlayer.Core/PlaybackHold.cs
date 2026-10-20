namespace HomePlayer.Core;

public static class PlaybackHold
{
    public static readonly TimeSpan SeekDrift = TimeSpan.FromMilliseconds(250);

    public static TimeSpan? Capture(bool isOpen, TimeSpan? existing, TimeSpan position)
    {
        if (existing != null)
            return existing;
        if (!isOpen || position < TimeSpan.Zero)
            return null;
        return position;
    }

    public static bool NeedsSeek(TimeSpan? held, TimeSpan current) =>
        held is TimeSpan position && (current - position).Duration() > SeekDrift;
}
