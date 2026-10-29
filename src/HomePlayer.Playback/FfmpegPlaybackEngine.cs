using HomePlayer.Core;
using HomePlayer.Ffmpeg;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace HomePlayer.Playback;

public sealed class FfmpegPlaybackEngine : IPlaybackEngine, IPlaybackFaultSource
{
    private readonly object _gate = new();
    private readonly string? _audioOutputId;
    private FfmpegPlayer? _player;
    private WasapiOut? _output;
    private MMDevice? _device;
    private BufferedWaveProvider? _buffer;
    private string? _audioDevice;
    private double _volume = 0.8;
    private double _rate = 1.0;
    private bool _muted;

    public FfmpegPlaybackEngine(string? audioOutputId = null) => _audioOutputId = audioOutputId;

    public string Name => "FFmpeg";
    public bool IsOpen => _player?.IsOpen == true;
    public bool IsPlaying => _player?.IsPlaying == true;
    public TimeSpan Position => _player?.Position ?? TimeSpan.Zero;
    public TimeSpan Duration => _player?.Duration ?? TimeSpan.Zero;
    public double Rate => _player?.Rate ?? _rate;

    public event Action<VideoFrame>? FrameArrived;
    public event Action? Ended;
    public event Action<Exception>? Faulted;

    public void Open(string path)
    {
        Stop();
        var player = new FfmpegPlayer();
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
                var buffer = new BufferedWaveProvider(new WaveFormat(48000, 16, 2))
                {
                    DiscardOnBufferOverflow = true,
                    BufferDuration = TimeSpan.FromSeconds(2)
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
                    output.Volume = _muted ? 0 : (float)_volume;
                    _buffer = buffer;
                    _output = output;
                    _device = device;
                    _audioDevice = name;
                    player.UnplayedBytes = () => _buffer?.BufferedBytes ?? 0;
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
        _output?.Play();
        _player?.Play();
    }

    public void Pause()
    {
        _player?.Pause();
        _output?.Pause();
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
        _audioDevice = null;
    }

    public void Seek(TimeSpan position)
    {
        _buffer?.ClearBuffer();
        _player?.Seek(position);
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
        _buffer?.ClearBuffer();
        _player?.SetAudioTrack(index);
    }

    public IReadOnlyList<MediaTrack> GetSubtitleTracks() => _player?.GetSubtitleTracks() ?? [];

    public int SubtitleTrackIndex => _player?.SubtitleTrackIndex ?? -1;

    public void SetSubtitleTrack(int index) => _player?.SetSubtitleTrack(index);

    public PipelineInfo GetPipeline()
    {
        var info = _player?.GetPipeline() ?? new PipelineInfo { Engine = "FFmpeg" };
        info.AudioDevice = _audioDevice ?? AudioOutputs.Describe(_audioOutputId);
        return info;
    }

    public void Dispose() => Stop();

    private void OnPcm(byte[] data, int count)
    {
        try
        {
            _buffer?.AddSamples(data, 0, count);
        }
        catch (InvalidOperationException)
        {
        }
    }
}
