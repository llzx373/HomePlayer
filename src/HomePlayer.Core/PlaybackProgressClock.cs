namespace HomePlayer.Core;

public sealed class PlaybackProgressClock
{
    private TimeSpan _anchor;
    private TimeSpan _correction;
    private double _anchorNow;

    public bool HasAnchor { get; private set; }
    public bool AnchorPlaying { get; private set; }

    public void Reset()
    {
        HasAnchor = false;
        AnchorPlaying = false;
        _anchor = TimeSpan.Zero;
        _correction = TimeSpan.Zero;
        _anchorNow = 0;
    }

    public void Anchor(TimeSpan position, TimeSpan duration, bool playing, double now)
    {
        _anchor = Clamp(position, duration);
        _correction = TimeSpan.Zero;
        AnchorPlaying = playing;
        _anchorNow = now;
        HasAnchor = true;
    }

    public void Correct(TimeSpan engine, TimeSpan predicted, TimeSpan duration, bool playing, double now)
    {
        var error = engine - predicted;
        Anchor(engine, duration, playing, now);
        if (playing && error.Duration() < TimeSpan.FromSeconds(1.5))
            _correction = -error;
    }

    public TimeSpan Predict(TimeSpan duration, double rate, double now)
    {
        if (!HasAnchor)
            return TimeSpan.Zero;
        var position = _anchor;
        if (AnchorPlaying)
        {
            var elapsed = Math.Max(0, now - _anchorNow);
            position += TimeSpan.FromSeconds(elapsed * rate);
            position += TimeSpan.FromSeconds(_correction.TotalSeconds * Math.Exp(-elapsed / 0.25));
        }

        return Clamp(position, duration);
    }

    private static TimeSpan Clamp(TimeSpan position, TimeSpan duration)
    {
        if (position < TimeSpan.Zero)
            position = TimeSpan.Zero;
        if (duration > TimeSpan.Zero && position > duration)
            position = duration;
        return position;
    }
}
