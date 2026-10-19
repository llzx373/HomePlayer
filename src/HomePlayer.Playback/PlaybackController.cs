using HomePlayer.Core;

namespace HomePlayer.Playback;

public sealed class PlaybackController : IDisposable
{
    private readonly Action<Action> _post;
    private IPlaybackEngine? _engine;
    private string? _reason;
    private int _token;
    private double _volume = 0.8;
    private bool _muted;

    public PlaybackController(Action<Action> post) => _post = post;

    public Playlist Playlist { get; } = new();
    public string Status { get; private set; } = "就绪";
    public string EngineLabel { get; private set; } = "未播放";
    public bool IsPlaying => _engine?.IsPlaying == true;
    public TimeSpan Position => _engine?.Position ?? TimeSpan.Zero;
    public TimeSpan Duration => _engine?.Duration ?? TimeSpan.Zero;
    public double Volume => _volume;
    public bool Muted => _muted;
    public event Action? Changed;
    public event Action<VideoFrame>? FrameArrived;
    public event Action<bool>? PlaybackActiveChanged;

    public void SetVolume(double volume)
    {
        _volume = Math.Clamp(volume, 0, 1);
        _engine?.SetVolume(_volume);
        Notify();
    }

    public void SetMuted(bool muted)
    {
        _muted = muted;
        _engine?.SetMuted(muted);
        Notify();
    }

    public Task PlayPathsAsync(IReadOnlyList<string> paths, int start, bool forceFfmpeg = false)
    {
        Playlist.Replace(paths, start);
        return PlayCurrentAsync(forceFfmpeg);
    }

    public void Enqueue(IEnumerable<string> paths)
    {
        var idle = Playlist.Items.Count == 0;
        Playlist.Add(paths);
        Notify();
        if (idle && Playlist.Items.Count > 0)
        {
            Playlist.CurrentIndex = 0;
            _ = PlayCurrentAsync(false);
        }
    }

    public void TogglePlay()
    {
        if (_engine == null)
        {
            if (Playlist.Current != null)
                _ = PlayCurrentAsync(false);
            return;
        }

        if (_engine.IsPlaying)
            _engine.Pause();
        else
            _engine.Play();
        Notify();
    }

    public void Stop()
    {
        _token++;
        DisposeEngine();
        Status = "已停止";
        EngineLabel = "未播放";
        Notify();
    }

    public Task NextAsync() => MoveAsync(Playlist.NextIndex(false));

    public Task PreviousAsync()
    {
        if (Position.TotalSeconds > 3)
        {
            _engine?.Seek(TimeSpan.Zero);
            Notify();
            return Task.CompletedTask;
        }

        return MoveAsync(Playlist.PreviousIndex());
    }

    public Task PlayIndexAsync(int index)
    {
        if (index < 0 || index >= Playlist.Items.Count)
            return Task.CompletedTask;
        Playlist.CurrentIndex = index;
        return PlayCurrentAsync(false);
    }

    public void Seek(TimeSpan position)
    {
        if (position < TimeSpan.Zero)
            position = TimeSpan.Zero;
        if (Duration > TimeSpan.Zero && position > Duration)
            position = Duration;
        _engine?.Seek(position);
        Notify();
    }

    public PipelineInfo? Pipeline()
    {
        var info = _engine?.GetPipeline();
        if (info != null)
            info.EngineReason = _reason;
        return info;
    }

    public void Dispose() => Stop();

    private async Task MoveAsync(int? index)
    {
        if (index == null)
        {
            Stop();
            return;
        }

        Playlist.CurrentIndex = index.Value;
        await PlayCurrentAsync(false);
    }

    private async Task PlayCurrentAsync(bool forceFfmpeg)
    {
        var token = ++_token;
        var failures = 0;
        while (token == _token && Playlist.Current != null && failures < Playlist.Items.Count)
        {
            var item = Playlist.Current;
            Status = "正在打开";
            Notify();
            try
            {
                await Task.Run(() => OpenEngine(item.Path, forceFfmpeg));
                if (token != _token)
                    return;
                _post(() =>
                {
                    item.Error = null;
                    _engine?.SetVolume(_volume);
                    _engine?.SetMuted(_muted);
                    _engine?.Play();
                    Status = "正在播放";
                    EngineLabel = _engine?.Name ?? "";
                    Notify();
                });
                return;
            }
            catch (Exception ex)
            {
                failures++;
                var failed = item;
                var next = Playlist.NextIndex(false);
                _post(() =>
                {
                    failed.Error = ex.Message;
                    Status = ex.Message;
                    Notify();
                });
                if (next == null || next == Playlist.CurrentIndex)
                    break;
                Playlist.CurrentIndex = next.Value;
                forceFfmpeg = false;
            }
        }

        _post(() =>
        {
            DisposeEngine();
            Status = "无法播放";
            EngineLabel = "未播放";
            Notify();
        });
    }

    private void OpenEngine(string path, bool forceFfmpeg)
    {
        DisposeEngine();
        Exception? lavError = null;
        if (!forceFfmpeg && LavLocator.Detect() != null)
        {
            var lav = new LavPlaybackEngine();
            try
            {
                Attach(lav);
                lav.Open(path);
                _engine = lav;
                _reason = "LAV 滤镜图已建立。";
                return;
            }
            catch (Exception ex)
            {
                lavError = ex;
                Detach(lav);
                lav.Dispose();
            }
        }

        var ffmpeg = new FfmpegPlaybackEngine();
        try
        {
            Attach(ffmpeg);
            ffmpeg.Open(path);
            _engine = ffmpeg;
            _reason = lavError == null
                ? (forceFfmpeg ? "已按指定改用 FFmpeg。" : "未检测到本机 LAV Filters，已改用 FFmpeg。")
                : "LAV 无法播放该文件，已改用 FFmpeg：" + lavError.Message;
        }
        catch (Exception ex)
        {
            Detach(ffmpeg);
            ffmpeg.Dispose();
            throw new InvalidOperationException(lavError == null ? ex.Message : $"{lavError.Message}；FFmpeg 也失败：{ex.Message}", ex);
        }
    }

    private void Attach(IPlaybackEngine engine)
    {
        engine.FrameArrived += OnFrame;
        engine.Ended += OnEnded;
        PlaybackActiveChanged?.Invoke(true);
    }

    private void Detach(IPlaybackEngine engine)
    {
        engine.FrameArrived -= OnFrame;
        engine.Ended -= OnEnded;
    }

    private void OnFrame(VideoFrame frame) => FrameArrived?.Invoke(frame);

    private void OnEnded()
    {
        _post(() =>
        {
            var next = Playlist.NextIndex(true);
            if (next == null)
            {
                Status = "播放结束";
                Notify();
                return;
            }

            if (next == Playlist.CurrentIndex)
            {
                _engine?.Seek(TimeSpan.Zero);
                _engine?.Play();
                return;
            }

            Playlist.CurrentIndex = next.Value;
            _ = PlayCurrentAsync(false);
        });
    }

    private void DisposeEngine()
    {
        var engine = _engine;
        _engine = null;
        if (engine == null)
            return;
        Detach(engine);
        engine.Dispose();
        PlaybackActiveChanged?.Invoke(false);
    }

    private void Notify() => Changed?.Invoke();
}
