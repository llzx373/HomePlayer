using HomePlayer.Core;
using HomePlayer.Ffmpeg;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace HomePlayer.Playback;

public sealed class FfmpegPlaybackEngine : IPlaybackEngine
{
    private readonly object _gate = new();
    private readonly string? _audioOutputId;
    private FfmpegPlayer? _player;
    private WasapiOut? _output;
    private MMDevice? _device;
    private BufferedWaveProvider? _buffer;
    private string? _audioDevice;
    private double _volume = 0.8;
    private bool _muted;

    public FfmpegPlaybackEngine(string? audioOutputId = null) => _audioOutputId = audioOutputId;

    public string Name => "FFmpeg";
    public bool IsOpen => _player?.IsOpen == true;
    public bool IsPlaying => _player?.IsPlaying == true;
    public TimeSpan Position => _player?.Position ?? TimeSpan.Zero;
    public TimeSpan Duration => _player?.Duration ?? TimeSpan.Zero;

    public event Action<VideoFrame>? FrameArrived;
    public event Action? Ended;

    public void Open(string path)
    {
        Stop();
        var player = new FfmpegPlayer();
        player.FrameArrived += frame => FrameArrived?.Invoke(frame);
        player.Ended += () => Ended?.Invoke();
        player.AudioPcm += OnPcm;
        player.Open(path);
        if (player.HasAudio)
        {
            _buffer = new BufferedWaveProvider(new WaveFormat(48000, 16, 2))
            {
                DiscardOnBufferOverflow = true,
                BufferDuration = TimeSpan.FromSeconds(2)
            };
            if (string.IsNullOrWhiteSpace(_audioOutputId))
            {
                _output = new WasapiOut(AudioClientShareMode.Shared, 100);
                _audioDevice = "系统默认";
            }
            else
            {
                _device = AudioOutputs.OpenWasapi(_audioOutputId);
                _audioDevice = _device == null
                    ? "系统默认（所选设备不可用）"
                    : _device.ID == _audioOutputId
                        ? _device.FriendlyName
                        : _device.FriendlyName + "（所选设备不可用，已改用系统默认）";
                _output = _device == null
                    ? new WasapiOut(AudioClientShareMode.Shared, 100)
                    : new WasapiOut(_device, AudioClientShareMode.Shared, true, 100);
            }
            _output.Init(_buffer);
            _output.Volume = _muted ? 0 : (float)_volume;
            player.UnplayedBytes = () => _buffer?.BufferedBytes ?? 0;
        }

        lock (_gate)
            _player = player;
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
