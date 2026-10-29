using HomePlayer.Core;

namespace HomePlayer.Playback;

public sealed class PlaybackController : IDisposable
{
    private readonly Action<Action> _post;
    private readonly Func<CodecPlan> _codecs;
    private readonly Func<string?> _audioOutput;
    private readonly IPlaybackStateStore? _store;
    private readonly Func<string, bool, (IPlaybackEngine Engine, string Reason)> _openEngine;
    private readonly Dictionary<string, PlaybackState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _stateGate = new();
    private readonly CancellationTokenSource _flushStop = new();
    private readonly Task? _flushLoop;
    private IPlaybackEngine? _engine;
    private string? _reason;
    private string? _playingPath;
    private int _token;
    private double _volume = 0.8;
    private double _rate = 1.0;
    private bool _muted;
    private int _videoWidth;
    private int _videoHeight;

    public PlaybackController(
        Action<Action> post,
        Func<CodecPlan> codecs,
        Func<string?> audioOutput,
        IPlaybackStateStore? store = null,
        Func<string, bool, (IPlaybackEngine Engine, string Reason)>? openEngine = null)
    {
        _post = post;
        _codecs = codecs;
        _audioOutput = audioOutput;
        _store = store;
        _openEngine = openEngine ?? OpenEngine;
        if (store == null)
            return;
        try
        {
            foreach (var (path, state) in store.LoadAll(2000))
                _states[path] = state;
        }
        catch (Exception)
        {
        }

        _flushLoop = Task.Run(() => FlushLoopAsync(_flushStop.Token));
    }

    public Playlist Playlist { get; } = new();
    public string Status { get; private set; } = "就绪";
    public string EngineLabel { get; private set; } = "未播放";
    public bool IsOpen => _engine?.IsOpen == true;
    public bool IsPlaying => _engine?.IsPlaying == true;
    public TimeSpan Position => _engine?.Position ?? TimeSpan.Zero;
    public TimeSpan Duration => _engine?.Duration ?? TimeSpan.Zero;
    public double Volume => _volume;
    public double Rate => _rate;
    public bool Muted => _muted;
    public int VideoWidth => _videoWidth;
    public int VideoHeight => _videoHeight;
    public event Action? Changed;
    public event Action<VideoFrame>? FrameArrived;
    public event Action? VideoSizeChanged;
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

    public void SetRate(double rate)
    {
        _rate = PlaybackRates.Clamp(rate);
        if (_engine != null)
        {
            _engine.SetRate(_rate);
            Status = Math.Abs(_engine.Rate - _rate) > 0.01
                ? "当前滤镜不支持倍速"
                : Status == "当前滤镜不支持倍速"
                    ? "正在播放"
                    : Status;
        }

        Notify();
    }

    public Task PlayPathsAsync(IReadOnlyList<string> paths, int start, bool forceFfmpeg = false)
    {
        RememberPosition();
        var playing = _playingPath;
        Playlist.Replace(paths, start);
        var next = Playlist.Current?.Path;
        if (!forceFfmpeg
            && IsOpen
            && next != null
            && string.Equals(playing, next, StringComparison.OrdinalIgnoreCase)
            && PlaybackResume.Position(Position, Duration) != null)
        {
            if (_engine is { IsPlaying: false })
                _engine.Play();
            Notify();
            return Task.CompletedTask;
        }

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
        {
            _engine.Pause();
        }
        else
        {
            if (Status == "播放结束")
                _engine.Seek(TimeSpan.Zero);
            _engine.Play();
        }

        Notify();
    }

    public void Pause()
    {
        if (_engine?.IsPlaying != true)
            return;
        _engine.Pause();
        PersistProgress();
        Notify();
    }

    public void Stop()
    {
        RememberPosition();
        _store?.Flush();
        _token++;
        _playingPath = null;
        DisposeEngine();
        ClearVideoSize();
        Status = "已停止";
        EngineLabel = "未播放";
        Notify();
    }

    public Task ShutdownAsync()
    {
        PersistProgress();
        _flushStop.Cancel();
        _token++;
        _playingPath = null;
        var engine = TakeEngine();
        ClearVideoSize();
        Status = "已停止";
        EngineLabel = "未播放";
        if (engine == null)
            return Task.CompletedTask;
        return Task.Run(() => Discard(engine));
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
        PersistProgress();
        Notify();
    }

    public void StepFrame()
    {
        if (_engine == null)
            return;
        _engine.StepFrame();
        Notify();
    }

    public IReadOnlyList<MediaTrack> GetAudioTracks() => _engine?.GetAudioTracks() ?? [];

    public int AudioTrackIndex => _engine?.AudioTrackIndex ?? -1;

    public void SetAudioTrack(int index)
    {
        _engine?.SetAudioTrack(index);
        Notify();
    }

    public IReadOnlyList<MediaTrack> GetSubtitleTracks() => _engine?.GetSubtitleTracks() ?? [];

    public int SubtitleTrackIndex => _engine?.SubtitleTrackIndex ?? -1;

    public void SetSubtitleTrack(int index)
    {
        _engine?.SetSubtitleTrack(index);
        Notify();
    }

    public PipelineInfo? Pipeline()
    {
        var info = _engine?.GetPipeline();
        if (info != null)
            info.EngineReason = _reason;
        return info;
    }

    public void Dispose()
    {
        Stop();
        _flushStop.Cancel();
        try
        {
            _flushLoop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
        }

        _flushStop.Dispose();
    }

    public void PersistProgress()
    {
        RememberPosition();
        _store?.Flush();
    }

    /// <summary>
    /// 文件内容变了或已被删除时丢掉内存里的旧进度，避免下次从错误位置续播。
    /// </summary>
    public void ForgetProgress(string path)
    {
        if (string.IsNullOrEmpty(path))
            return;
        lock (_stateGate)
            _states.Remove(path);
    }

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
        RememberPosition();
        _playingPath = null;
        _rate = 1.0;
        Notify();
        var token = ++_token;
        var previous = _engine;
        _engine = null;
        if (previous != null)
        {
            Detach(previous);
            PlaybackActiveChanged?.Invoke(false);
            await Task.Run(() =>
            {
                try
                {
                    previous.Dispose();
                }
                catch
                {
                }
            });
            if (token != _token)
                return;
        }

        var failures = 0;
        while (token == _token && Playlist.Current != null && failures < Playlist.Items.Count)
        {
            var item = Playlist.Current;
            Status = "正在打开";
            Notify();
            try
            {
                var (engine, reason) = await Task.Run(() => _openEngine(item.Path, forceFfmpeg));
                if (token != _token)
                {
                    _ = Task.Run(() => Discard(engine));
                    return;
                }

                Attach(engine);
                _post(() =>
                {
                    if (token != _token)
                    {
                        _ = Task.Run(() => Discard(engine));
                        return;
                    }

                    _engine = engine;
                    _reason = reason;
                    _playingPath = item.Path;
                    PlaybackActiveChanged?.Invoke(true);
                    try
                    {
                        item.Error = null;
                        engine.SetVolume(_volume);
                        engine.SetMuted(_muted);
                        engine.SetRate(_rate);
                        ApplyResume(engine, item.Path);
                        engine.Play();
                        Status = Math.Abs(engine.Rate - _rate) > 0.01
                            ? "当前滤镜不支持倍速"
                            : engine is LavPlaybackEngine { AudioMissing: true }
                                ? "正在播放，音频没有接上"
                                : "正在播放";
                        EngineLabel = engine.Name;
                        CaptureVideoSize(engine);
                        Notify();
                    }
                    catch (Exception ex)
                    {
                        DisposeEngine();
                        Status = ex.Message;
                        EngineLabel = "未播放";
                        Notify();
                    }
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

        if (token == _token)
        {
            _post(() =>
            {
                DisposeEngine();
                Status = "无法播放";
                EngineLabel = "未播放";
                Notify();
            });
        }
    }

    private (IPlaybackEngine Engine, string Reason) OpenEngine(string path, bool forceFfmpeg)
    {
        Exception? lavError = null;
        var plan = _codecs();
        if (!forceFfmpeg && plan.Splitters.Count > 0 && plan.VideoDecoders.Count > 0)
        {
            var lav = new LavPlaybackEngine(plan, _audioOutput());
            try
            {
                lav.Open(path);
                var reason = lav.AudioMissing
                    ? "画面已打开，音频没有接上：" + lav.AudioWarning
                    : string.IsNullOrWhiteSpace(lav.AudioWarning)
                        ? "已按设置的顺序建立滤镜图。"
                        : lav.AudioWarning;
                return (lav, reason);
            }
            catch (Exception ex)
            {
                lavError = ex;
                Discard(lav);
            }
        }

        var ffmpeg = new FfmpegPlaybackEngine(_audioOutput());
        try
        {
            ffmpeg.Open(path);
            var reason = lavError == null
                ? (forceFfmpeg ? "已按指定改用 FFmpeg。" : "没有可用的 64 位分离器或视频解码器，已改用 FFmpeg。")
                : "DirectShow 滤镜无法播放该文件，已改用 FFmpeg：" + lavError.Message;
            return (ffmpeg, reason);
        }
        catch (Exception ex)
        {
            Discard(ffmpeg);
            throw new InvalidOperationException(lavError == null ? ex.Message : $"{lavError.Message}；FFmpeg 也失败：{ex.Message}", ex);
        }
    }

    private void Attach(IPlaybackEngine engine)
    {
        engine.FrameArrived += OnFrame;
        engine.Ended += OnEnded;
    }

    private void Discard(IPlaybackEngine engine)
    {
        Detach(engine);
        try
        {
            engine.Dispose();
        }
        catch
        {
        }
    }

    private void Detach(IPlaybackEngine engine)
    {
        engine.FrameArrived -= OnFrame;
        engine.Ended -= OnEnded;
    }

    private void OnFrame(VideoFrame frame)
    {
        RememberVideoSize(frame.Width, frame.Height, true);
        var handler = FrameArrived;
        if (handler == null)
        {
            frame.ReturnBuffer();
            return;
        }

        handler(frame);
    }

    private void OnEnded()
    {
        var token = _token;
        var path = _playingPath;
        var duration = _engine?.Duration ?? TimeSpan.Zero;
        _post(() =>
        {
            if (token != _token)
                return;
            var next = Playlist.NextIndex(true);
            if (next == null)
            {
                if (path != null)
                    SaveState(path, duration, duration);
                _store?.Flush();
                Status = "播放结束";
                Notify();
                return;
            }

            if (next == Playlist.CurrentIndex)
            {
                if (path != null)
                    SaveState(path, TimeSpan.Zero, duration);
                _store?.Flush();
                _engine?.Seek(TimeSpan.Zero);
                _engine?.Play();
                return;
            }

            if (path != null)
                SaveState(path, duration, duration);
            Playlist.CurrentIndex = next.Value;
            _ = PlayCurrentAsync(false);
        });
    }

    private void DisposeEngine()
    {
        var engine = TakeEngine();
        if (engine == null)
            return;
        _ = Task.Run(() => Discard(engine));
    }

    private IPlaybackEngine? TakeEngine()
    {
        var engine = _engine;
        _engine = null;
        _playingPath = null;
        if (engine == null)
            return null;
        Detach(engine);
        PlaybackActiveChanged?.Invoke(false);
        return engine;
    }

    private void CaptureVideoSize(IPlaybackEngine engine)
    {
        try
        {
            var info = engine.GetPipeline();
            if (info.Width is int width && info.Height is int height)
                RememberVideoSize(width, height, false);
        }
        catch
        {
        }
    }

    private void RememberVideoSize(int width, int height, bool post)
    {
        if (width < 1 || height < 1 || (width == _videoWidth && height == _videoHeight))
            return;
        _videoWidth = width;
        _videoHeight = height;
        if (post)
            _post(() => VideoSizeChanged?.Invoke());
        else
            VideoSizeChanged?.Invoke();
    }

    private void ClearVideoSize()
    {
        if (_videoWidth == 0 && _videoHeight == 0)
            return;
        _videoWidth = 0;
        _videoHeight = 0;
        VideoSizeChanged?.Invoke();
    }

    private void RememberPosition()
    {
        if (_engine == null || string.IsNullOrEmpty(_playingPath))
            return;
        SaveState(_playingPath, _engine.Position, _engine.Duration);
    }

    private void SaveState(string path, TimeSpan position, TimeSpan duration)
    {
        var (seconds, finished) = PlaybackProgress.Evaluate(position, duration);
        var state = new PlaybackState(path, seconds, duration.TotalSeconds, finished, DateTime.UtcNow.Ticks);
        lock (_stateGate)
            _states[path] = state;
        _store?.Save(state);
    }

    private void ApplyResume(IPlaybackEngine engine, string path)
    {
        PlaybackState? state;
        lock (_stateGate)
        {
            if (!_states.TryGetValue(path, out state))
                return;
        }

        if (state.Finished)
            return;
        var resume = PlaybackResume.Position(TimeSpan.FromSeconds(state.PositionSeconds), engine.Duration);
        if (resume == null)
            return;
        engine.Seek(resume.Value);
    }

    private async Task FlushLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
                _store?.Flush();
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void Notify() => Changed?.Invoke();
}
