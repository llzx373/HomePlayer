namespace HomePlayer.Core;

public readonly record struct RepairedMediaTimestamp(long PresentationTimestamp, long Duration);

public sealed class MediaTimestampRepair
{
    private long _epoch;
    private bool _started;
    private long _next;
    private long _lastDuration;
    private Int128 _offset;

    public RepairedMediaTimestamp Next(
        long epoch,
        long? presentationTimestamp,
        long duration,
        long fallbackDuration,
        long maxForwardGap)
    {
        if (!_started || epoch != _epoch)
        {
            _epoch = epoch;
            _started = false;
            _next = 0;
            _lastDuration = 0;
            _offset = 0;
        }

        var step = duration > 0 ? duration : fallbackDuration > 0 ? fallbackDuration : _lastDuration;
        if (step <= 0)
            step = 1;
        long repaired;
        if (!_started)
        {
            repaired = presentationTimestamp ?? 0;
            _started = true;
        }
        else if (presentationTimestamp is long raw)
        {
            var candidate = (Int128)raw + _offset;
            if (candidate < _next || maxForwardGap > 0 && candidate - _next > maxForwardGap)
            {
                _offset = (Int128)_next - raw;
                candidate = _next;
            }
            repaired = (long)candidate;
        }
        else
        {
            repaired = _next;
        }

        _lastDuration = step;
        var next = (Int128)repaired + step;
        _next = next > long.MaxValue ? long.MaxValue : next < long.MinValue ? long.MinValue : (long)next;
        return new RepairedMediaTimestamp(repaired, step);
    }
}
