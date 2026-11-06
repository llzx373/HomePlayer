using HomePlayer.Core;

namespace HomePlayer.Playback;

/// <summary>
/// 把实时生成的配音段落混进原声。段落表和播放头都由外部维护，
/// 这里只在音频线程按媒体时间读取并压低原声。
/// </summary>
public sealed class LiveDubbingMixSource(LiveDubbingTrack track, double duckLevel = 0.25) : IAudioMixSource, IDisposable
{
    private const double DuckLead = 0.15;

    private readonly object _gate = new();
    private readonly double _duckLevel = Math.Clamp(duckLevel, 0, 1);
    private long _epoch = -1;
    private double _duckGain = 1;

    public bool Enabled => true;

    public LiveDubbingTrack Track { get; } = track;

    public void Process(double mediaSeconds, byte[] pcm, int count, int sampleRate, int channels)
    {
        if (channels < 1 || sampleRate <= 0 || count < 2 * channels)
            return;

        lock (_gate)
        {
            try
            {
                var segments = Track.Snapshot();
                var epoch = Track.Epoch;
                if (epoch != _epoch)
                {
                    _epoch = epoch;
                    _duckGain = 1;
                }

                if (segments.Length == 0)
                    return;

                var frames = count / (2 * channels);
                var blockSeconds = frames / (double)sampleRate;
                var target = TargetDuck(segments, mediaSeconds + blockSeconds / 2);
                var smoothing = 1 - Math.Exp(-1.0 / (0.08 * sampleRate));
                var cursor = FirstSegmentAt(segments, mediaSeconds);
                for (var frame = 0; frame < frames; frame++)
                {
                    _duckGain += (target - _duckGain) * smoothing;
                    var seconds = mediaSeconds + frame / (double)sampleRate;
                    while (cursor < segments.Length && segments[cursor].EndSeconds <= seconds)
                        cursor++;
                    var dub = cursor < segments.Length ? SampleAt(segments[cursor], seconds) : 0;
                    var baseIndex = frame * channels;
                    for (var channel = 0; channel < channels; channel++)
                    {
                        var index = (baseIndex + channel) * 2;
                        var original = (short)(pcm[index] | (pcm[index + 1] << 8));
                        var mixed = (int)Math.Round(original * _duckGain + dub * 32767.0);
                        var value = (short)Math.Clamp(mixed, short.MinValue, short.MaxValue);
                        pcm[index] = (byte)value;
                        pcm[index + 1] = (byte)(value >> 8);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or IndexOutOfRangeException or ArgumentException)
            {
            }
        }
    }

    private static int FirstSegmentAt(LiveDubbingSegment[] segments, double seconds)
    {
        var low = 0;
        var high = segments.Length;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (segments[middle].EndSeconds <= seconds)
                low = middle + 1;
            else
                high = middle;
        }

        return low;
    }

    private static double SampleAt(LiveDubbingSegment segment, double seconds)
    {
        if (seconds < segment.StartSeconds || seconds >= segment.EndSeconds)
            return 0;
        var position = (seconds - segment.StartSeconds) * segment.SampleRate;
        var index = (int)position;
        if (index < 0 || index >= segment.Samples.Length)
            return 0;
        if (index + 1 >= segment.Samples.Length)
            return segment.Samples[index] / 32768.0;

        var fraction = position - index;
        var first = segment.Samples[index] / 32768.0;
        var second = segment.Samples[index + 1] / 32768.0;
        return first + (second - first) * fraction;
    }

    private double TargetDuck(LiveDubbingSegment[] segments, double seconds)
    {
        foreach (var segment in segments)
        {
            if (segment.StartSeconds - DuckLead > seconds)
                break;
            if (seconds >= segment.StartSeconds - DuckLead && seconds <= segment.EndSeconds + DuckLead)
                return _duckLevel;
        }

        return 1;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _epoch = -1;
            _duckGain = 1;
        }
    }
}
