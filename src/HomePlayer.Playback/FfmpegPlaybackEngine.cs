using System.Reflection;
using HomePlayer.Core;
using HomePlayer.Ffmpeg;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace HomePlayer.Playback;

public sealed class FfmpegPlaybackEngine : IPlaybackEngine, IPlaybackFaultSource, IPlaybackChapterSource, IPlaybackAudioEnhancements, IPlaybackAudioOutputEvents, IPlaybackTimedSeek
{
    private static readonly FieldInfo? AudioClientField = typeof(WasapiOut).GetField("audioClient", BindingFlags.Instance | BindingFlags.NonPublic);
    private readonly object _gate = new();
    private readonly object _clockGate = new();
    private readonly string? _audioOutputId;
    private readonly PcmAudioEffects _effects = new();
    private FfmpegPlayer? _player;
    private WasapiOut? _output;
    private MMDevice? _device;
    private BufferedWaveProvider? _buffer;
    private AudioClient? _audioClient;
    private string? _audioDevice;
    private bool _clockAnchored;
    private ulong _anchorDevicePosition;
    private ulong _anchorQpc;
    private double _anchorMediaSeconds;
    private double _volume = 0.8;
    private double _rate = 1.0;
    private bool _muted;
    private bool _normalize;
    private bool _nightMode;
    private int _delayBytes;
    private int _skipBytes;

    public FfmpegPlaybackEngine(string? audioOutputId = null) => _audioOutputId = audioOutputId;

    public D3D11SharedDevice? D3D11Device { get; set; }

    public string Name => "FFmpeg";
    public bool IsOpen => _player?.IsOpen == true;
    public bool IsPlaying => _player?.IsPlaying == true;
    public TimeSpan Position => _player?.Position ?? TimeSpan.Zero;
    public TimeSpan Duration => _player?.Duration ?? TimeSpan.Zero;
    public double Rate => _player?.Rate ?? _rate;

    public event Action<FrameLease>? FrameArrived;
    public event Action? Ended;
    public event Action<Exception>? Faulted;
    public event Action<Exception>? AudioOutputFailed;

    public void Open(string path)
    {
        Stop();
        var player = new FfmpegPlayer { D3D11Device = D3D11Device };
        try
        {
            player.FrameArrived += frame =>
            {
                var handler = FrameArrived;
                if (handler == null)
                {
                    frame.ReturnBuffer();
                    return;
                }

                handler(frame);
            };
            player.Ended += () => Ended?.Invoke();
            player.Faulted += error => Faulted?.Invoke(error);
            player.AudioPcm += OnPcm;
            player.Open(path);
            if (player.HasAudio)
            {
                var buffer = new BufferedWaveProvider(new WaveFormatExtensible(48000, 16, player.OutputChannels))
                {
                    DiscardOnBufferOverflow = false,
                    BufferDuration = TimeSpan.FromMilliseconds(500)
                };
                MMDevice? device = null;
                WasapiOut? output = null;
                try
                {
                    string name;
                    if (string.IsNullOrWhiteSpace(_audioOutputId))
                    {
                        output = new WasapiOut(AudioClientShareMode.Shared, 100);
                        name = "系统默认";
                    }
                    else
                    {
                        device = AudioOutputs.OpenWasapi(_audioOutputId);
                        name = device == null
                            ? "系统默认（所选设备不可用）"
                            : device.ID == _audioOutputId
                                ? device.FriendlyName
                                : device.FriendlyName + "（所选设备不可用，已改用系统默认）";
                        output = device == null
                            ? new WasapiOut(AudioClientShareMode.Shared, 100)
                            : new WasapiOut(device, AudioClientShareMode.Shared, true, 100);
                    }

                    output.Init(buffer);
                    output.PlaybackStopped += (_, args) =>
                    {
                        if (args.Exception != null)
                            AudioOutputFailed?.Invoke(args.Exception);
                    };
                    output.Volume = _muted ? 0 : (float)_volume;
                    _buffer = buffer;
                    _output = output;
                    _device = device;
                    _audioDevice = name;
                    _audioClient = AudioClientField?.GetValue(output) as AudioClient;
                    if (_audioClient != null)
                        player.AudioDevicePosition = ReadDeviceMediaSeconds;
                    player.UnplayedBytes = () => _buffer?.BufferedBytes ?? 0;
                    player.AudioOutputDrained = () => (_buffer?.BufferedBytes ?? 0) == 0 && (_audioClient?.CurrentPadding ?? 0) == 0;
                }
                catch (Exception ex)
                {
                    output?.Dispose();
                    device?.Dispose();
                    _buffer = null;
                    _output = null;
                    _device = null;
                    _audioDevice = "音频输出失败，已静音播放：" + ex.Message;
                }
            }

            player.SetRate(_rate);
            lock (_gate)
                _player = player;
        }
        catch
        {
            player.Dispose();
            _output?.Dispose();
            _output = null;
            _device?.Dispose();
            _device = null;
            _buffer = null;
            _audioDevice = null;
            throw;
        }
    }

    public void Play()
    {
        ResetAudioClockAnchor();
        _output?.Play();
        _player?.Play();
    }

    public void Pause()
    {
        _player?.Pause();
        _output?.Pause();
        ResetAudioClockAnchor();
    }

    public void Stop()
    {
        var player = _player;
        _player = null;
        player?.Dispose();
        _output?.Stop();
        _output?.Dispose();
        _output = null;
        _device?.Dispose();
        _device = null;
        _buffer = null;
        _audioClient = null;
        _audioDevice = null;
        ResetAudioClockAnchor();
    }

    public void Seek(TimeSpan position)
    {
        SetAudioDelay(_audioDelayMs);
        ResetAudioClockAnchor();
        _player?.Seek(position);
    }

    void IPlaybackTimedSeek.Seek(TimeSpan position, long epoch, long commandId, long startedTimestamp)
    {
        SetAudioDelay(_audioDelayMs);
        ResetAudioClockAnchor();
        _player?.Seek(position, epoch, commandId, startedTimestamp);
    }

    public void SetVolume(double linear)
    {
        _volume = Math.Clamp(linear, 0, 1);
        if (_output != null && !_muted)
            _output.Volume = (float)_volume;
    }

    public void SetMuted(bool muted)
    {
        _muted = muted;
        if (_output != null)
            _output.Volume = muted ? 0 : (float)_volume;
    }

    public void SetRate(double rate)
    {
        _rate = PlaybackRates.Clamp(rate);
        _player?.SetRate(_rate);
        _buffer?.ClearBuffer();
        ResetAudioClockAnchor();
    }

    public void StepFrame()
    {
        _output?.Pause();
        _buffer?.ClearBuffer();
        _player?.StepFrame();
    }

    public IReadOnlyList<MediaTrack> GetAudioTracks() => _player?.GetAudioTracks() ?? [];

    public int AudioTrackIndex => _player?.AudioTrackIndex ?? -1;

    public void SetAudioTrack(int index)
    {
        SetAudioDelay(_audioDelayMs);
        _player?.SetAudioTrack(index);
    }

    public IReadOnlyList<MediaTrack> GetSubtitleTracks() => _player?.GetSubtitleTracks() ?? [];

    public int SubtitleTrackIndex => _player?.SubtitleTrackIndex ?? -1;

    public IReadOnlyList<MediaChapter> GetChapters() => _player?.GetChapters() ?? [];

    public void SetSubtitleTrack(int index) => _player?.SetSubtitleTrack(index);

    public PipelineInfo GetPipeline()
    {
        var info = _player?.GetPipeline() ?? new PipelineInfo { Engine = "FFmpeg" };
        info.AudioDevice = _audioDevice ?? AudioOutputs.Describe(_audioOutputId);
        return info;
    }

    public void Dispose() => Stop();

    private int _audioDelayMs;

    public void SetAudioDelay(int milliseconds)
    {
        _audioDelayMs = Math.Clamp(milliseconds, -10_000, 10_000);
        var frameBytes = (_player?.OutputChannels ?? 2) * 2;
        var bytes = (int)(Math.Abs((long)_audioDelayMs) * 48000L * frameBytes / 1000);
        bytes -= bytes % frameBytes;
        _delayBytes = _audioDelayMs > 0 ? bytes : 0;
        _skipBytes = _audioDelayMs < 0 ? bytes : 0;
        _buffer?.ClearBuffer();
    }

    public void SetNormalization(bool enabled) => _normalize = enabled;

    public void SetNightMode(bool enabled) => _nightMode = enabled;

    private void OnPcm(byte[] data, int count, long epoch)
    {
        try
        {
            var player = _player;
            if (player == null || epoch != player.PcmEpoch)
                return;
            if (_delayBytes > 0)
            {
                var silence = new byte[_delayBytes];
                WritePcm(silence, 0, silence.Length, epoch);
                _delayBytes = 0;
            }
            var offset = Math.Min(count, _skipBytes);
            _skipBytes -= offset;
            if (offset >= count)
                return;
            if (offset > 0)
            {
                data = data[offset..count];
                count = data.Length;
                offset = 0;
            }
            _effects.Apply(data, count, _normalize, _nightMode);
            WritePcm(data, offset, count, epoch);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void WritePcm(byte[] data, int offset, int count, long epoch)
    {
        while (count > 0)
        {
            var buffer = _buffer;
            var player = _player;
            if (buffer == null || player == null || epoch != player.PcmEpoch)
                return;
            var available = buffer.BufferLength - buffer.BufferedBytes;
            if (available < buffer.WaveFormat.BlockAlign)
            {
                Thread.Sleep(2);
                continue;
            }
            var chunk = Math.Min(count, available - available % buffer.WaveFormat.BlockAlign);
            buffer.AddSamples(data, offset, chunk);
            offset += chunk;
            count -= chunk;
        }
    }

    private double? ReadDeviceMediaSeconds()
    {
        try
        {
            var clock = _audioClient?.AudioClockClient;
            if (clock == null || _output?.PlaybackState != NAudio.Wave.PlaybackState.Playing || !clock.GetPosition(out var position, out var qpc))
                return null;
            var frequency = clock.Frequency;
            if (frequency == 0)
                return null;
            lock (_clockGate)
            {
                if (!_clockAnchored || position < _anchorDevicePosition)
                {
                    _anchorDevicePosition = position;
                    _anchorQpc = qpc;
                    _anchorMediaSeconds = _player?.Position.TotalSeconds ?? 0;
                    _clockAnchored = true;
                }
                return _anchorMediaSeconds + (position - _anchorDevicePosition) / (double)frequency * _rate;
            }
        }
        catch
        {
            return null;
        }
    }

    private void ResetAudioClockAnchor()
    {
        lock (_clockGate)
        {
            _clockAnchored = false;
            _anchorDevicePosition = 0;
            _anchorQpc = 0;
            _anchorMediaSeconds = 0;
        }
    }
}
