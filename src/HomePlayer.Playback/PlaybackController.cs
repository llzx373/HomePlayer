using HomePlayer.Core;

namespace HomePlayer.Playback;

public sealed partial class PlaybackController : IDisposable
{
    private readonly Action<Action> _post;
    private readonly Func<CodecPlan> _codecs;
    private readonly Func<string?> _audioOutput;
    private readonly IPlaybackStateStore? _store;
    private readonly Func<string, PlaybackRoute, (IPlaybackEngine Engine, string Reason)> _openEngine;
    private readonly Func<bool> _historyEnabled;
    private readonly IMediaPreferenceStore? _preferences;
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
    private DateTime _openStartedUtc;
    private bool _firstFramePending;

    public PlaybackController(
        Action<Action> post,
        Func<CodecPlan> codecs,
        Func<string?> audioOutput,
        IPlaybackStateStore? store = null,
        Func<string, bool, (IPlaybackEngine Engine, string Reason)>? openEngine = null,
        Func<bool>? historyEnabled = null,
        IMediaPreferenceStore? preferences = null,
        Func<string, CancellationToken, Task<MediaProbe>>? probeNext = null)
    {
        _post = post;
        _codecs = codecs;
        _audioOutput = audioOutput;
        _store = store;
        _openEngine = openEngine == null
            ? OpenEngine
            : (path, route) => openEngine(path, route == PlaybackRoute.NativeFfmpeg);
        _historyEnabled = historyEnabled ?? (() => true);
        _preferences = preferences;
        _probeNext = probeNext ?? ProbeNextDefault;
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
        _ = Task.Run(() => MonitorAudioDeviceAsync(_flushStop.Token));
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
    public bool AutoAdvance { get; set; } = true;
    public int VideoWidth => _videoWidth;
    public int VideoHeight => _videoHeight;
    public bool UsesExternalVideoOutput => _engine != null && InnerEngine(_engine) is LavPlaybackEngine { ExternalOutput: not null };
    public event Action? Changed;
    public event Action<FrameLease>? FrameArrived;
    public event Action? VideoSizeChanged;
    public event Action<bool>? PlaybackActiveChanged;
    public event Action? Stopped;
    public event Action? SeekStarted;

    public void SetVolume(double volume)
    {
        _volume = Math.Clamp(volume, 0, 1);
        _engine?.SetVolume(_volume);
        SavePlaybackPreference();
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

        SavePlaybackPreference();
        Notify();
    }

    public Task PlayPathsAsync(
        IReadOnlyList<string> paths,
        int start,
        bool forceFfmpeg = false,
        bool restart = false,
        PlaybackRoute? route = null)
    {
        var selectedRoute = route ?? (forceFfmpeg ? PlaybackRoute.NativeFfmpeg : PlaybackRoute.Auto);
        RememberPosition();
        var playing = _playingPath;
        Playlist.Replace(paths, start);
        var next = Playlist.Current?.Path;
        if (selectedRoute == PlaybackRoute.Auto
            && !restart
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

        if (selectedRoute != PlaybackRoute.Auto
            && !restart
            && _engine != null
            && next != null
            && string.Equals(playing, next, StringComparison.OrdinalIgnoreCase))
            return SwitchRouteAsync(selectedRoute);
        return PlayCurrentAsync(selectedRoute, restart);
    }

    public void Enqueue(IEnumerable<string> paths)
    {
        var idle = Playlist.Items.Count == 0;
        Playlist.Add(paths);
        Notify();
        if (idle && Playlist.Items.Count > 0)
        {
            Playlist.CurrentIndex = 0;
            _ = PlayCurrentAsync(PlaybackRoute.Auto);
        }
    }

    public void TogglePlay()
    {
        if (_engine == null)
        {
            if (Playlist.Current != null)
                _ = PlayCurrentAsync(PlaybackRoute.Auto);
            return;
        }

        if (_engine.IsPlaying)
        {
            _engine.Pause();
        }
        else
        {
            if (Status == "播放结束")
                SeekEngine(_engine, TimeSpan.Zero);
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
        CancelNextProbe();
        ResetRecovery();
        InvalidateRouteSwitch();
        _playingPath = null;
        DisposeEngine();
        ClearVideoSize();
        Status = "已停止";
        EngineLabel = "未播放";
        Stopped?.Invoke();
        Notify();
    }

    public Task ShutdownAsync()
    {
        PersistProgress();
        _flushStop.Cancel();
        _token++;
        CancelNextProbe();
        ResetRecovery();
        InvalidateRouteSwitch();
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
            SeekEngine(_engine, TimeSpan.Zero);
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
        return PlayCurrentAsync(PlaybackRoute.Auto);
    }

    public void Seek(TimeSpan position)
    {
        if (position < TimeSpan.Zero)
            position = TimeSpan.Zero;
        if (Duration > TimeSpan.Zero && position > Duration)
            position = Duration;
        SeekEngine(_engine, position);
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
        if (_engine != null)
            SeekStarted?.Invoke();
        SaveAudioPreference(index);
        Notify();
    }

    public void ClearAudioPreference()
    {
        if (_playingPath == null || _preferences == null)
            return;
        var preference = LoadPreference(_playingPath);
        _preferences.SavePreference(preference with
        {
            AudioTrackIndex = null,
            AudioTrackTitle = null,
            AudioTrackLanguage = null,
            AudioTrackStableId = null
        });
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

    public void SetWatched(string path, bool watched)
    {
        PlaybackState? current;
        lock (_stateGate)
            _states.TryGetValue(path, out current);
        if (current == null && _store != null)
        {
            try
            {
                current = _store.Load(path);
            }
            catch (Exception)
            {
            }
        }

        var state = new PlaybackState(
            path, 0, current?.DurationSeconds ?? 0, watched, DateTime.UtcNow.Ticks);
        lock (_stateGate)
            _states[path] = state;
        _store?.Save(state);
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

    public void ClearHistory()
    {
        lock (_stateGate)
            _states.Clear();
    }

    private async Task MoveAsync(int? index)
    {
        if (index == null)
        {
            Stop();
            return;
        }

        Playlist.CurrentIndex = index.Value;
        await PlayCurrentAsync(PlaybackRoute.Auto);
    }

    private async Task PlayCurrentAsync(PlaybackRoute route, bool restart = false, bool recovery = false, RouteSwitchOperation? routeSwitch = null)
    {
        if (routeSwitch == null)
            InvalidateRouteSwitch();
        RememberPosition();
        var currentPath = Playlist.Current?.Path;
        if (!string.Equals(_recoveryPath, currentPath, StringComparison.OrdinalIgnoreCase))
        {
            _recoveryPath = currentPath;
            _recoveryAttempts = 0;
        }
        if (!string.Equals(_playingPath, currentPath, StringComparison.OrdinalIgnoreCase))
            ClearLoop();
        _playingPath = null;
        _rate = 1.0;
        _openStartedUtc = DateTime.UtcNow;
        _firstFramePending = true;
        Notify();
        var token = ++_token;
        CancelNextProbe();
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
            {
                routeSwitch?.Complete(false, "路线切换已取消。");
                return;
            }
        }

        var failures = 0;
        while (token == _token && Playlist.Current != null && failures < Playlist.Items.Count)
        {
            var item = Playlist.Current;
            Status = "正在打开";
            Notify();
            try
            {
                var (engine, reason) = await Task.Run(() => _openEngine(item.Path, route));
                if (token != _token)
                {
                    _ = Task.Run(() => Discard(engine));
                    routeSwitch?.Complete(false, "路线切换已取消。");
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
                        ApplyAudioPreference(engine, item.Path);
                        ApplyAudioSettings(engine, item.Path);
                        ApplyPlaybackPreferences(engine, item.Path);
                        if (!restart)
                            ApplyResume(engine, item.Path);
                        engine.Play();
                        Status = recovery ? "播放已恢复" : _audioFallbackNotice ?? (Math.Abs(engine.Rate - _rate) > 0.01
                            ? "当前滤镜不支持倍速"
                            : InnerEngine(engine) is LavPlaybackEngine { AudioMissing: true }
                                ? "正在播放，音频没有接上"
                                : "正在播放");
                        _audioFallbackNotice = null;
                        EngineLabel = engine.Name;
                        CaptureVideoSize(engine);
                        ScheduleNextProbe(token);
                        Notify();
                        routeSwitch?.Complete(true);
                    }
                    catch (Exception ex)
                    {
                        DisposeEngine();
                        Status = ex.Message;
                        EngineLabel = "未播放";
                        Notify();
                        routeSwitch?.Complete(false, ex.Message);
                    }
                });
                return;
            }
            catch (Exception ex)
            {
                failures++;
                var failed = item;
                if (recovery && route != PlaybackRoute.Auto)
                {
                    route = PlaybackRoute.Auto;
                    continue;
                }
                var next = recovery || routeSwitch != null ? null : Playlist.NextIndex(false);
                _post(() =>
                {
                    failed.Error = ex.Message;
                    Status = ex.Message;
                    Notify();
                });
                if (next == null || next == Playlist.CurrentIndex)
                    break;
                Playlist.CurrentIndex = next.Value;
                route = PlaybackRoute.Auto;
            }
        }

        if (token == _token)
        {
            _post(() =>
            {
                DisposeEngine();
                Status = recovery ? "播放恢复失败" : "无法播放";
                EngineLabel = "未播放";
                Notify();
                routeSwitch?.Complete(false, "新路线打开失败。");
            });
        }
        else
        {
            routeSwitch?.Complete(false, "路线切换已取消。");
        }
    }

    private (IPlaybackEngine Engine, string Reason) OpenEngine(string path, PlaybackRoute route)
    {
        var registry = new PlaybackProviderRegistry(
        [
            new NativeFfmpegProvider(),
            new DirectShowProvider(_codecs)
        ]);
        var opened = registry.Open(
            path,
            _useDefaultAudio ? null : _audioOutput(),
            _codecs,
            route);
        return (opened.Session, opened.Reason);
    }

    private void Attach(IPlaybackEngine engine)
    {
        engine.FrameArrived += OnFrame;
        engine.Ended += OnEnded;
        var inner = InnerEngine(engine);
        if (inner is IPlaybackFaultSource faults)
            faults.Faulted += OnFaulted;
        if (inner is IPlaybackAudioOutputEvents audioEvents)
            audioEvents.AudioOutputFailed += OnAudioOutputFailed;
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
        var inner = InnerEngine(engine);
        if (inner is IPlaybackFaultSource faults)
            faults.Faulted -= OnFaulted;
        if (inner is IPlaybackAudioOutputEvents audioEvents)
            audioEvents.AudioOutputFailed -= OnAudioOutputFailed;
    }

    private void OnFrame(FrameLease frame)
    {
        if (_firstFramePending)
        {
            _firstFramePending = false;
            PerformanceMetrics.Record("playback.first-decoded-frame", (DateTime.UtcNow - _openStartedUtc).TotalMilliseconds);
            PlaybackTelemetry.Trace("FirstDecodedFrame", frame.SessionId, frame.Epoch, frame.SeekCommandId, PresentationObservation.Decoded);
        }
        RememberVideoSize(frame.Width, frame.Height, true);
        var handler = FrameArrived;
        if (handler == null)
        {
            frame.ReturnBuffer();
            return;
        }

        handler(frame);
    }

    private static IPlaybackEngine InnerEngine(IPlaybackEngine engine) => (engine as PlaybackSession)?.InnerEngine ?? engine;

    private void SeekEngine(IPlaybackEngine? engine, TimeSpan position)
    {
        if (engine == null)
            return;
        engine.Seek(position);
        SeekStarted?.Invoke();
    }

    private void OnFaulted(Exception error)
    {
        var token = _token;
        _post(() =>
        {
            if (token != _token)
                return;
            _engine?.Pause();
            PersistProgress();
            if (TryRecoverFromFault())
                return;
            Status = "播放失败：" + error.Message + "（已停止自动恢复）";
            Notify();
        });
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
            var next = AutoAdvance ? Playlist.NextIndex(true) : null;
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
                SeekEngine(_engine, TimeSpan.Zero);
                _engine?.Play();
                return;
            }

            if (path != null)
                SaveState(path, duration, duration);
            Playlist.CurrentIndex = next.Value;
            _ = PlayCurrentAsync(PlaybackRoute.Auto);
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
        if (!_historyEnabled())
            return;
        var (seconds, finished) = PlaybackProgress.Evaluate(position, duration);
        var state = new PlaybackState(path, seconds, duration.TotalSeconds, finished, DateTime.UtcNow.Ticks);
        lock (_stateGate)
            _states[path] = state;
        _store?.Save(state);
    }

    private MediaPreference LoadPreference(string path)
    {
        try
        {
            return _preferences?.LoadPreference(path) ?? MediaPreference.Empty(path);
        }
        catch (Exception)
        {
            return MediaPreference.Empty(path);
        }
    }

    private void ApplyAudioPreference(IPlaybackEngine engine, string path)
    {
        try
        {
            var preference = _preferences?.LoadPreference(path);
            var index = MediaPreferenceSelection.AudioTrack(preference, engine.GetAudioTracks(), PreferredAudioLanguage());
            if (index is int selected)
                engine.SetAudioTrack(selected);
        }
        catch (Exception)
        {
        }
    }

    private void SaveAudioPreference(int index)
    {
        if (_playingPath == null || _engine == null || _preferences == null)
            return;
        try
        {
            var track = _engine.GetAudioTracks().FirstOrDefault(item => item.Index == index);
            if (track == null)
                return;
            var preference = LoadPreference(_playingPath);
            _preferences.SavePreference(preference with
            {
                AudioTrackIndex = track.Index,
                AudioTrackTitle = track.Title,
                AudioTrackLanguage = track.Language,
                AudioTrackStableId = track.StableId
            });
        }
        catch (Exception)
        {
        }
    }

    private void ApplyResume(IPlaybackEngine engine, string path)
    {
        PlaybackState? state;
        lock (_stateGate)
            _states.TryGetValue(path, out state);
        if (state == null && _store != null)
        {
            try
            {
                state = _store.Load(path);
            }
            catch (Exception)
            {
            }

            if (state != null)
            {
                lock (_stateGate)
                    _states[path] = state;
            }
        }

        if (state == null || state.Finished)
            return;
        var resume = PlaybackResume.Position(TimeSpan.FromSeconds(state.PositionSeconds), engine.Duration);
        if (resume == null)
            return;
        var rewind = TimeSpan.FromSeconds(Math.Clamp(ResumeRewindSeconds(), 0, 10));
        SeekEngine(engine, resume.Value - rewind);
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
