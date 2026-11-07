using HomePlayer.Core;

namespace HomePlayer.Playback.Tests;

public sealed class PlaybackSessionTests
{
    [Fact]
    public void Media_time_base_rescales_with_integer_rounding()
    {
        var timeBase = new MediaTimeBase(1, 90000);
        Assert.Equal(10000, timeBase.ToTimeSpanTicks(90));
        Assert.Equal(90, timeBase.FromTimeSpanTicks(10000));
        Assert.Equal(TimeSpan.TicksPerSecond, timeBase.ToTimeSpanTicks(90000));
        Assert.Equal(0, default(MediaTimeBase).ToTimeSpanTicks(123));
    }

    [Fact]
    public async Task Commands_report_acceptance_completion_and_seek_epoch()
    {
        using var session = new PlaybackSession(new FakeEngine(), "media");
        var results = new List<PlaybackCommandResult>();
        session.CommandCompleted += results.Add;

        var result = await session.ExecuteAsync(PlaybackCommandType.Seek, TimeSpan.FromSeconds(5));

        Assert.Equal(PlaybackCommandStatus.Completed, result.Status);
        Assert.Equal(1, result.Epoch);
        Assert.Equal(2, results.Count);
        Assert.Equal(PlaybackCommandStatus.Accepted, results[0].Status);
        Assert.Equal(PlaybackCommandStatus.Completed, results[1].Status);
        Assert.Equal(PlaybackOperation.None, session.Snapshot.CurrentOperation);
    }

    [Fact]
    public void Old_epoch_frames_are_returned_without_reaching_subscribers()
    {
        using var session = new PlaybackSession(new FakeEngine(), "media");
        var delivered = 0;
        var returned = 0;
        session.FrameArrived += frame =>
        {
            delivered++;
            frame.ReturnBuffer();
        };
        session.Seek(TimeSpan.FromSeconds(1));
        var frame = new VideoFrame
        {
            Width = 1,
            Height = 1,
            Stride = 4,
            Data = new byte[4],
            SessionId = Guid.NewGuid(),
            Epoch = session.Epoch - 1,
            ReleaseBuffer = () => returned++
        };

        ((FakeEngine)session.InnerEngine).Emit(frame);

        Assert.Equal(0, delivered);
        Assert.Equal(1, returned);
    }

    [Fact]
    public void Close_is_idempotent()
    {
        var engine = new FakeEngine();
        var session = new PlaybackSession(engine, "media");
        session.Dispose();
        session.Dispose();
        Assert.Equal(1, engine.DisposeCount);
        Assert.Equal(PlaybackLifecycle.Closed, session.Snapshot.Lifecycle);
    }

    [Fact]
    public async Task Inner_fault_marks_the_session_failed_and_rejects_further_play()
    {
        var engine = new FakeEngine();
        using var session = new PlaybackSession(engine, "media");
        Exception? fault = null;
        session.Faulted += error => fault = error;
        var error = new InvalidOperationException("decode failed");

        engine.RaiseFault(error);

        Assert.Same(error, fault);
        Assert.Equal(PlaybackLifecycle.Failed, session.Snapshot.Lifecycle);
        var play = await session.ExecuteAsync(PlaybackCommandType.Play);
        Assert.Equal(PlaybackCommandStatus.Failed, play.Status);
        Assert.Equal(0, engine.PlayCount);
        var stop = await session.ExecuteAsync(PlaybackCommandType.Stop);
        Assert.Equal(PlaybackCommandStatus.Completed, stop.Status);
        Assert.Equal(0, engine.StopCount);
    }

    [Fact]
    public async Task Stop_after_close_is_idempotent_even_when_the_engine_rejects_it()
    {
        var engine = new FakeEngine { ThrowOnStopAfterDispose = true };
        var session = new PlaybackSession(engine, "media");
        session.Dispose();

        var stop = await session.ExecuteAsync(PlaybackCommandType.Stop);
        var close = await session.ExecuteAsync(PlaybackCommandType.Close);

        Assert.Equal(PlaybackCommandStatus.Completed, stop.Status);
        Assert.Equal(PlaybackCommandStatus.Completed, close.Status);
        Assert.Equal(1, engine.DisposeCount);
    }

    private sealed class FakeEngine : IPlaybackEngine, IPlaybackFaultSource
    {
        private bool _disposed;

        public string Name => "Fake";
        public bool IsOpen { get; private set; } = true;
        public bool IsPlaying { get; private set; }
        public TimeSpan Position { get; private set; }
        public TimeSpan Duration => TimeSpan.FromSeconds(10);
        public double Rate { get; private set; } = 1;
        public int AudioTrackIndex => -1;
        public int SubtitleTrackIndex => -1;
        public int DisposeCount { get; private set; }
        public int PlayCount { get; private set; }
        public int StopCount { get; private set; }
        public bool ThrowOnStopAfterDispose { get; init; }
        public event Action<FrameLease>? FrameArrived;
        public event Action? Ended { add { } remove { } }
        public event Action<Exception>? Faulted;
        public void Open(string path) { }
        public void Play()
        {
            PlayCount++;
            IsPlaying = true;
        }

        public void Pause() => IsPlaying = false;
        public void Stop()
        {
            StopCount++;
            if (_disposed && ThrowOnStopAfterDispose)
                throw new ObjectDisposedException(nameof(FakeEngine));
            IsPlaying = false;
        }

        public void Seek(TimeSpan position) => Position = position;
        public void SetVolume(double linear) { }
        public void SetMuted(bool muted) { }
        public void SetRate(double rate) => Rate = rate;
        public void StepFrame() { }
        public IReadOnlyList<MediaTrack> GetAudioTracks() => [];
        public void SetAudioTrack(int index) { }
        public IReadOnlyList<MediaTrack> GetSubtitleTracks() => [];
        public void SetSubtitleTrack(int index) { }
        public PipelineInfo GetPipeline() => new() { Engine = Name };
        public void Dispose()
        {
            _disposed = true;
            DisposeCount++;
        }

        public void Emit(VideoFrame frame) => FrameArrived?.Invoke(frame);
        public void RaiseFault(Exception error) => Faulted?.Invoke(error);
    }
}
