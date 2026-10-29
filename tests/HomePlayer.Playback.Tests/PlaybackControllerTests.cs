using HomePlayer.Core;
using HomePlayer.Playback;

namespace HomePlayer.Playback.Tests;

public class PlaybackControllerTests
{
    [Fact]
    public async Task TogglePlay_after_completion_seeks_to_zero_before_replaying()
    {
        var engine = new FakeEngine();
        using var controller = Create(engine);
        await controller.PlayPathsAsync(["video.mkv"], 0);
        engine.RaiseEnded();
        Assert.Equal("播放结束", controller.Status);

        controller.TogglePlay();

        Assert.Equal(TimeSpan.Zero, engine.LastSeek);
        Assert.Equal(2, engine.PlayCount);
        Assert.True(engine.IsPlaying);
    }

    [Fact]
    public void Stop_notifies_presenter_to_clear_the_last_frame()
    {
        using var controller = Create(new FakeEngine());
        var stopped = 0;
        controller.Stopped += () => stopped++;

        controller.Stop();

        Assert.Equal(1, stopped);
        Assert.Equal("已停止", controller.Status);
    }

    [Fact]
    public async Task Fault_keeps_current_item_and_saves_unfinished_progress()
    {
        var engine = new FakeEngine();
        var store = new FakeStateStore();
        using var controller = Create(engine, store);
        await controller.PlayPathsAsync(["video.mkv"], 0);
        controller.Seek(TimeSpan.FromSeconds(30));
        store.Saved.Clear();

        engine.RaiseFault(new InvalidOperationException("decode failed"));

        Assert.Equal("播放失败：decode failed", controller.Status);
        Assert.Equal("video.mkv", controller.Playlist.Current?.Path);
        var state = Assert.Single(store.Saved);
        Assert.Equal(30, state.PositionSeconds);
        Assert.False(state.Finished);
    }

    [Fact]
    public async Task Resume_loads_record_on_demand_when_not_preloaded()
    {
        var engine = new FakeEngine();
        var store = new FakeStateStore();
        store.OnDemand["video.mkv"] = new PlaybackState("video.mkv", 30, 100, false, 1);
        using var controller = Create(engine, store);

        await controller.PlayPathsAsync(["video.mkv"], 0);

        Assert.Equal(2000, store.PreloadLimit);
        Assert.Equal(1, store.LoadCount);
        Assert.Equal(TimeSpan.FromSeconds(30), engine.LastSeek);
    }

    [Fact]
    public async Task Disabled_history_does_not_save_new_progress()
    {
        var store = new FakeStateStore();
        using var controller = Create(new FakeEngine(), store, () => false);

        await controller.PlayPathsAsync(["video.mkv"], 0);
        controller.Seek(TimeSpan.FromSeconds(30));
        controller.PersistProgress();

        Assert.Empty(store.Saved);
    }

    private static PlaybackController Create(
        FakeEngine engine,
        IPlaybackStateStore? store = null,
        Func<bool>? historyEnabled = null) => new(
        action => action(),
        () => throw new InvalidOperationException("测试不应读取编解码器"),
        () => null,
        store,
        openEngine: (_, _) => (engine, "测试引擎"),
        historyEnabled: historyEnabled);

    private sealed class FakeStateStore : IPlaybackStateStore
    {
        public List<PlaybackState> Saved { get; } = [];
        public Dictionary<string, PlaybackState> OnDemand { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int PreloadLimit { get; private set; }
        public int LoadCount { get; private set; }

        public IReadOnlyDictionary<string, PlaybackState> LoadAll(int limit)
        {
            PreloadLimit = limit;
            return new Dictionary<string, PlaybackState>();
        }

        public PlaybackState? Load(string path)
        {
            LoadCount++;
            return OnDemand.GetValueOrDefault(path);
        }

        public void Save(PlaybackState state) => Saved.Add(state);

        public void Flush()
        {
        }
    }

    private sealed class FakeEngine : IPlaybackEngine, IPlaybackFaultSource
    {
        public string Name => "Fake";
        public bool IsOpen { get; private set; } = true;
        public bool IsPlaying { get; private set; }
        public TimeSpan Position { get; private set; }
        public TimeSpan Duration { get; } = TimeSpan.FromSeconds(100);
        public double Rate { get; private set; } = 1;
        public int AudioTrackIndex => -1;
        public int SubtitleTrackIndex => -1;
        public int PlayCount { get; private set; }
        public TimeSpan LastSeek { get; private set; }

        public event Action<VideoFrame>? FrameArrived;
        public event Action? Ended;
        public event Action<Exception>? Faulted;

        public void Open(string path)
        {
        }

        public void Play()
        {
            PlayCount++;
            IsPlaying = true;
        }

        public void Pause() => IsPlaying = false;

        public void Stop() => IsPlaying = false;

        public void Seek(TimeSpan position)
        {
            LastSeek = position;
            Position = position;
        }

        public void SetVolume(double linear)
        {
        }

        public void SetMuted(bool muted)
        {
        }

        public void SetRate(double rate) => Rate = rate;

        public void StepFrame()
        {
        }

        public IReadOnlyList<MediaTrack> GetAudioTracks() => [];

        public void SetAudioTrack(int index)
        {
        }

        public IReadOnlyList<MediaTrack> GetSubtitleTracks() => [];

        public void SetSubtitleTrack(int index)
        {
        }

        public PipelineInfo GetPipeline() => new() { Engine = Name };

        public void Dispose() => IsOpen = false;

        public void RaiseFrame(VideoFrame frame) => FrameArrived?.Invoke(frame);

        public void RaiseEnded()
        {
            Position = Duration;
            IsPlaying = false;
            Ended?.Invoke();
        }

        public void RaiseFault(Exception error)
        {
            IsPlaying = false;
            Faulted?.Invoke(error);
        }
    }
}
