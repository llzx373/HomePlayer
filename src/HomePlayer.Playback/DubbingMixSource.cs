using HomePlayer.Core;

namespace HomePlayer.Playback;

/// <summary>
/// 把 &lt;视频&gt;.ai.dub.wav 混进原声：按时间轴重采样到 48 kHz，并在有配音的段落压低原声。
/// </summary>
public sealed class DubbingMixSource : IAudioMixSource, IDisposable
{
    private const double DuckLead = 0.15;

    private readonly object _gate = new();
    private readonly DubbingMode _mode;
    private readonly double _duckLevel;
    private readonly DubbingManifest? _manifest;
    private readonly int _sourceRate;
    private readonly int _sourceChannels;
    private readonly int _dataOffset;
    private readonly long _frames;
    private FileStream? _stream;
    private short[] _window = [];
    private byte[] _byteWindow = new byte[8192];
    private long _windowStart;
    private double _duckGain = 1;
    private int _segmentHint;

    private DubbingMixSource(
        FileStream stream,
        WavInfo info,
        DubbingManifest? manifest,
        DubbingMode mode,
        double duckLevel)
    {
        _stream = stream;
        _sourceRate = info.SampleRate;
        _sourceChannels = Math.Max(1, info.Channels);
        _dataOffset = info.DataOffset;
        _frames = info.Frames;
        _manifest = manifest;
        _mode = mode;
        _duckLevel = Math.Clamp(duckLevel, 0, 1);
    }

    public bool Enabled => _mode != DubbingMode.Off;

    public DubbingMode Mode => _mode;

    public static DubbingMixSource? TryCreate(string? mediaPath, DubbingMode mode, double duckLevel = 0.25)
    {
        if (mode == DubbingMode.Off || string.IsNullOrWhiteSpace(mediaPath))
            return null;
        var audio = DubbingFile.AudioPathFor(mediaPath);
        if (!File.Exists(audio))
            return null;
        try
        {
            var info = WavFile.ReadInfo(audio);
            var stream = new FileStream(audio, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024);
            return new DubbingMixSource(stream, info, DubbingFile.TryLoad(DubbingFile.ManifestPathFor(mediaPath)), mode, duckLevel);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
    }

    public void Process(double mediaSeconds, byte[] pcm, int count, int sampleRate, int channels)
    {
        if (!Enabled || channels < 1 || sampleRate <= 0 || count < 2 * channels)
            return;

        lock (_gate)
        {
            var stream = _stream;
            if (stream == null)
                return;
            try
            {
                var frames = count / (2 * channels);
                var blockSeconds = frames / (double)sampleRate;
                ReadWindow(stream, mediaSeconds, blockSeconds);

                var target = TargetDuck(mediaSeconds + blockSeconds / 2);
                var smoothing = 1 - Math.Exp(-1.0 / (0.08 * sampleRate));
                for (var frame = 0; frame < frames; frame++)
                {
                    _duckGain += (target - _duckGain) * smoothing;
                    var gain = _mode == DubbingMode.DubOnly ? 0 : _duckGain;
                    var source = (mediaSeconds + frame / (double)sampleRate) * _sourceRate;
                    var dub = SampleAt(source);
                    var baseIndex = frame * channels;
                    for (var channel = 0; channel < channels; channel++)
                    {
                        var index = (baseIndex + channel) * 2;
                        var original = (short)(pcm[index] | (pcm[index + 1] << 8));
                        var mixed = (int)Math.Round(original * gain + dub * 32767.0);
                        var value = (short)Math.Clamp(mixed, short.MinValue, short.MaxValue);
                        pcm[index] = (byte)value;
                        pcm[index + 1] = (byte)(value >> 8);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                _stream?.Dispose();
                _stream = null;
            }
        }
    }

    private void ReadWindow(FileStream stream, double mediaSeconds, double blockSeconds)
    {
        var start = mediaSeconds * _sourceRate;
        var end = (mediaSeconds + blockSeconds) * _sourceRate + 1;
        var first = Math.Max(0, (long)Math.Floor(start) - 1);
        var last = Math.Min(_frames, (long)Math.Ceiling(end) + 1);
        var needed = (int)Math.Max(0, last - first);
        _windowStart = first;
        if (needed == 0)
        {
            _window = [];
            return;
        }

        if (_window.Length < needed)
            _window = new short[needed];
        var bytes = needed * _sourceChannels * 2;
        if (_byteWindow.Length < bytes)
            _byteWindow = new byte[bytes];
        stream.Position = _dataOffset + first * _sourceChannels * 2;
        stream.ReadExactly(_byteWindow, 0, bytes);
        var stride = _sourceChannels * 2;
        for (var i = 0; i < needed; i++)
            _window[i] = (short)(_byteWindow[i * stride] | (_byteWindow[i * stride + 1] << 8));
    }

    private double SampleAt(double position)
    {
        var offset = position - _windowStart;
        if (offset < 0 || offset + 1 >= _window.Length)
            return 0;
        var index = (int)offset;
        var fraction = offset - index;
        var first = _window[index] / 32768.0;
        var second = _window[index + 1] / 32768.0;
        return first + (second - first) * fraction;
    }

    private double TargetDuck(double seconds)
    {
        if (_mode == DubbingMode.DubOnly)
            return 0;
        var segments = _manifest?.Segments;
        if (segments is not { Count: > 0 })
            return 1;

        var hint = Math.Clamp(_segmentHint, 0, segments.Count - 1);
        if (segments[hint].StartMs / 1000.0 - DuckLead > seconds)
            hint = 0;
        while (hint + 1 < segments.Count && segments[hint + 1].StartMs / 1000.0 - DuckLead <= seconds)
            hint++;
        _segmentHint = hint;

        var segment = segments[hint];
        return seconds >= segment.StartMs / 1000.0 - DuckLead && seconds <= segment.EndMs / 1000.0 + DuckLead
            ? _duckLevel
            : 1;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _stream?.Dispose();
            _stream = null;
        }
    }
}
