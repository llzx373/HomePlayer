using HomePlayer.Core;

namespace HomePlayer.Playback;

internal interface IPlaybackTimedSeek
{
    void Seek(TimeSpan position, long epoch, long commandId, long startedTimestamp);
}

public sealed class PlaybackSession : IPlaybackSession, IPlaybackFaultSource, IPlaybackAudioEnhancements, IPlaybackAudioOutputEvents, IPlaybackChapterSource
{
    private readonly object _gate = new();
    private long _commandId;
    private long _epoch;
    private long _surfaceGeneration;
    private long _deviceGeneration;
    private PlaybackLifecycle _lifecycle = PlaybackLifecycle.Ready;
    private PlaybackTransportState _transport = PlaybackTransportState.Stopped;
    private PlaybackOperation _operation;
    private bool _closed;

    public PlaybackSession(IPlaybackEngine engine, string mediaId)
    {
        InnerEngine = engine;
        MediaId = mediaId;
        SessionId = Guid.NewGuid();
        TimelineId = SessionId.ToString("N");
        InnerEngine.FrameArrived += OnFrame;
        InnerEngine.Ended += OnEnded;
        if (InnerEngine is IPlaybackFaultSource faults)
            faults.Faulted += error => Faulted?.Invoke(error);
        if (InnerEngine is IPlaybackAudioOutputEvents audioEvents)
            audioEvents.AudioOutputFailed += error => AudioOutputFailed?.Invoke(error);
    }

    public IPlaybackEngine InnerEngine { get; }
    public Guid SessionId { get; }
    public string MediaId { get; }
    public long Epoch => Interlocked.Read(ref _epoch);
    public string TimelineId { get; }
    public string Name => InnerEngine.Name;
    public bool IsOpen => !_closed && InnerEngine.IsOpen;
    public bool IsPlaying => !_closed && InnerEngine.IsPlaying;
    public TimeSpan Position => InnerEngine.Position;
    public TimeSpan Duration => InnerEngine.Duration;
    public double Rate => InnerEngine.Rate;
    public int AudioTrackIndex => InnerEngine.AudioTrackIndex;
    public int SubtitleTrackIndex => InnerEngine.SubtitleTrackIndex;
    public PlaybackSessionSnapshot Snapshot
    {
        get
        {
            lock (_gate)
                return new PlaybackSessionSnapshot(SessionId, MediaId, Epoch, TimelineId, _lifecycle, _transport, _operation, false,
                    Interlocked.Read(ref _surfaceGeneration), Interlocked.Read(ref _deviceGeneration));
        }
    }

    public event Action<VideoFrame>? FrameArrived;
    public event Action? Ended;
    public event Action<PlaybackCommandResult>? CommandCompleted;
    public event Action<Exception>? Faulted;
    public event Action<Exception>? AudioOutputFailed;

    public Task<PlaybackCommandResult> ExecuteAsync(PlaybackCommandType command, TimeSpan? position = null, CancellationToken cancellationToken = default)
    {
        var commandId = Interlocked.Increment(ref _commandId);
        var epoch = Epoch;
        var accepted = new PlaybackCommandResult(commandId, SessionId, epoch, command, PlaybackCommandStatus.Accepted);
        CommandCompleted?.Invoke(accepted);
        PlaybackTelemetry.Trace("CommandAccepted", SessionId, epoch, commandId, data: new Dictionary<string, object?> { ["command"] = command.ToString() });
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_closed && command != PlaybackCommandType.Close && command != PlaybackCommandType.Stop)
                throw new InvalidOperationException("播放会话已关闭。");
            switch (command)
            {
                case PlaybackCommandType.Play:
                    InnerEngine.Play();
                    _transport = PlaybackTransportState.Playing;
                    break;
                case PlaybackCommandType.Pause:
                    InnerEngine.Pause();
                    _transport = PlaybackTransportState.Paused;
                    break;
                case PlaybackCommandType.Stop:
                    InnerEngine.Stop();
                    _transport = PlaybackTransportState.Stopped;
                    break;
                case PlaybackCommandType.Seek:
                    epoch = Interlocked.Increment(ref _epoch);
                    SetOperation(PlaybackOperation.Seeking);
                    var started = PlaybackTraceEvent.Now;
                    if (InnerEngine is IPlaybackTimedSeek timed)
                        timed.Seek(position ?? TimeSpan.Zero, epoch, commandId, started);
                    else
                        InnerEngine.Seek(position ?? TimeSpan.Zero);
                    break;
                case PlaybackCommandType.Close:
                    Dispose();
                    break;
                case PlaybackCommandType.SetAudioTrack:
                case PlaybackCommandType.SetSubtitleTrack:
                    throw new NotSupportedException("轨道命令需要指定轨道标识。");
                default:
                    throw new ArgumentOutOfRangeException(nameof(command));
            }

            var completed = new PlaybackCommandResult(commandId, SessionId, epoch, command, PlaybackCommandStatus.Completed);
            CommandCompleted?.Invoke(completed);
            PlaybackTelemetry.Trace("CommandCompleted", SessionId, epoch, commandId, data: new Dictionary<string, object?> { ["command"] = command.ToString() });
            return Task.FromResult(completed);
        }
        catch (OperationCanceledException)
        {
            var cancelled = new PlaybackCommandResult(commandId, SessionId, epoch, command, PlaybackCommandStatus.Cancelled, "命令已取消。");
            CommandCompleted?.Invoke(cancelled);
            return Task.FromResult(cancelled);
        }
        catch (Exception ex)
        {
            var failed = new PlaybackCommandResult(commandId, SessionId, epoch, command, PlaybackCommandStatus.Failed, ex.Message);
            CommandCompleted?.Invoke(failed);
            return Task.FromResult(failed);
        }
        finally
        {
            if (command == PlaybackCommandType.Seek)
                SetOperation(PlaybackOperation.None);
        }
    }

    public void Open(string path) => throw new NotSupportedException("Provider 创建会话后不能重复 Open。");
    public void Play() => ExecuteAsync(PlaybackCommandType.Play).GetAwaiter().GetResult();
    public void Pause() => ExecuteAsync(PlaybackCommandType.Pause).GetAwaiter().GetResult();
    public void Stop() => ExecuteAsync(PlaybackCommandType.Stop).GetAwaiter().GetResult();
    public void Seek(TimeSpan position) => ExecuteAsync(PlaybackCommandType.Seek, position).GetAwaiter().GetResult();
    public void SetVolume(double linear) => InnerEngine.SetVolume(linear);
    public void SetMuted(bool muted) => InnerEngine.SetMuted(muted);
    public void SetRate(double rate) => InnerEngine.SetRate(rate);
    public void StepFrame() => InnerEngine.StepFrame();
    public IReadOnlyList<MediaTrack> GetAudioTracks() => InnerEngine.GetAudioTracks();
    public void SetAudioTrack(int index) => InnerEngine.SetAudioTrack(index);
    public IReadOnlyList<MediaTrack> GetSubtitleTracks() => InnerEngine.GetSubtitleTracks();
    public void SetSubtitleTrack(int index) => InnerEngine.SetSubtitleTrack(index);
    public PipelineInfo GetPipeline() => InnerEngine.GetPipeline();
    public IReadOnlyList<MediaChapter> GetChapters() => (InnerEngine as IPlaybackChapterSource)?.GetChapters() ?? [];
    public void SetAudioDelay(int milliseconds) => (InnerEngine as IPlaybackAudioEnhancements)?.SetAudioDelay(milliseconds);
    public void SetNormalization(bool enabled) => (InnerEngine as IPlaybackAudioEnhancements)?.SetNormalization(enabled);
    public void SetNightMode(bool enabled) => (InnerEngine as IPlaybackAudioEnhancements)?.SetNightMode(enabled);
    public void NotifySurfaceChanged() => Interlocked.Increment(ref _surfaceGeneration);
    public void NotifyDeviceChanged() => Interlocked.Increment(ref _deviceGeneration);

    public void Dispose()
    {
        if (_closed)
            return;
        _closed = true;
        _lifecycle = PlaybackLifecycle.Closing;
        InnerEngine.FrameArrived -= OnFrame;
        InnerEngine.Ended -= OnEnded;
        InnerEngine.Dispose();
        _lifecycle = PlaybackLifecycle.Closed;
        _transport = PlaybackTransportState.Stopped;
    }

    private void OnFrame(VideoFrame frame)
    {
        if (_closed)
        {
            frame.ReturnBuffer();
            return;
        }
        if (frame.SessionId == Guid.Empty)
        {
            frame.SessionId = SessionId;
            frame.Epoch = Epoch;
            frame.TimelineId = TimelineId;
        }
        else
        {
            frame.SessionId = SessionId;
            frame.TimelineId = TimelineId;
        }
        if (frame.Epoch != Epoch)
        {
            frame.ReturnBuffer();
            return;
        }
        FrameArrived?.Invoke(frame);
    }

    private void OnEnded()
    {
        if (_closed)
            return;
        _transport = PlaybackTransportState.Ended;
        Ended?.Invoke();
    }

    private void SetOperation(PlaybackOperation operation)
    {
        lock (_gate)
            _operation = operation;
    }
}
