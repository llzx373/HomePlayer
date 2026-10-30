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
        Assert.Equal(TimeSpan.FromSeconds(25), engine.LastSeek);
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

    [Fact]
    public async Task Restart_ignores_the_saved_resume_position()
    {
        var engine = new FakeEngine();
        var store = new FakeStateStore();
        store.OnDemand["video.mkv"] = new PlaybackState("video.mkv", 30, 100, false, 1);
        using var controller = Create(engine, store);

        await controller.PlayPathsAsync(["video.mkv"], 0, restart: true);

        Assert.Equal(0, engine.SeekCount);
    }

    [Fact]
    public void Manual_watched_state_is_saved_even_when_history_is_disabled()
    {
        var store = new FakeStateStore();
        using var controller = Create(new FakeEngine(), store, () => false);

        controller.SetWatched("video.mkv", true);

        Assert.True(Assert.Single(store.Saved).Finished);
    }

    [Fact]
    public async Task Audio_preference_is_restored_and_updated()
    {
        var engine = new FakeEngine();
        engine.AudioTracks.Add(new MediaTrack(1, MediaTrackKind.Audio, "Japanese", "ja", true));
        engine.AudioTracks.Add(new MediaTrack(3, MediaTrackKind.Audio, "Commentary", "en", false));
        var preferences = new FakePreferenceStore
        {
            Stored = MediaPreference.Empty("video.mkv") with
            {
                AudioTrackIndex = 1,
                AudioTrackTitle = "Commentary",
                AudioTrackLanguage = "en"
            }
        };
        using var controller = Create(engine, preferences: preferences);

        await controller.PlayPathsAsync(["video.mkv"], 0);

        Assert.Equal(3, engine.AudioTrackIndex);
        controller.SetAudioTrack(1);
        Assert.Equal(1, preferences.Stored?.AudioTrackIndex);
        Assert.Equal("ja", preferences.Stored?.AudioTrackLanguage);
    }

    [Fact]
    public async Task End_of_episode_advances_to_a_new_engine_and_saves_completion()
    {
        var first = new FakeEngine();
        var second = new FakeEngine();
        var store = new FakeStateStore();
        var engines = new Queue<IPlaybackEngine>([first, second]);
        using var controller = new PlaybackController(
            action => action(),
            () => throw new InvalidOperationException("测试不应读取编解码器"),
            () => null,
            store,
            openEngine: (_, _) => (engines.Dequeue(), "测试引擎"));
        await controller.PlayPathsAsync(["one.mkv", "two.mkv"], 0);

        first.RaiseEnded();
        await WaitFor(() => second.PlayCount > 0);

        Assert.Equal("two.mkv", controller.Playlist.Current?.Path);
        Assert.Contains(store.Saved, state => state.Path == "one.mkv" && state.Finished);
    }

    [Fact]
    public void RestorePlaylist_selects_current_item_without_playing()
    {
        var engine = new FakeEngine();
        using var controller = Create(engine);

        controller.RestorePlaylist(["one.mkv", "two.mkv"], 1);

        Assert.Equal("two.mkv", controller.Playlist.Current?.Path);
        Assert.False(controller.IsOpen);
        Assert.Equal(0, engine.PlayCount);
    }

    [Fact]
    public async Task Resume_applies_configured_rewind()
    {
        var engine = new FakeEngine();
        var store = new FakeStateStore();
        store.OnDemand["video.mkv"] = new PlaybackState("video.mkv", 30, 100, false, 1);
        using var controller = Create(engine, store);
        controller.ResumeRewindSeconds = () => 8;

        await controller.PlayPathsAsync(["video.mkv"], 0);

        Assert.Equal(TimeSpan.FromSeconds(22), engine.LastSeek);
    }

    [Fact]
    public async Task Volume_and_rate_preferences_are_restored_and_saved()
    {
        var engine = new FakeEngine();
        var preferences = new FakePreferenceStore
        {
            Stored = MediaPreference.Empty("video.mkv") with { Volume = 0.35, PlaybackRate = 1.5 }
        };
        using var controller = Create(engine, preferences: preferences);
        controller.RememberVolumePerFile = () => true;
        controller.RememberPlaybackRatePerFile = () => true;

        await controller.PlayPathsAsync(["video.mkv"], 0);

        Assert.Equal(0.35, engine.LastAppliedVolume);
        Assert.Equal(1.5, engine.Rate);
        controller.SetVolume(0.6);
        controller.SetRate(1.25);

        Assert.Equal(0.6, preferences.Stored?.Volume);
        Assert.Equal(1.25, preferences.Stored?.PlaybackRate);
    }

    [Fact]
    public async Task Forgotten_file_progress_is_reloaded_on_next_open()
    {
        var first = new FakeEngine();
        var second = new FakeEngine();
        var store = new FakeStateStore();
        store.OnDemand["video.mkv"] = new PlaybackState("video.mkv", 30, 100, false, 1);
        var engines = new Queue<IPlaybackEngine>([first, second]);
        using var controller = new PlaybackController(
            action => action(),
            () => throw new InvalidOperationException("测试不应读取编解码器"),
            () => null,
            store,
            openEngine: (_, _) => (engines.Dequeue(), "测试引擎"));
        controller.ResumeRewindSeconds = () => 0;
        await controller.PlayPathsAsync(["video.mkv"], 0);
        controller.Stop();
        controller.ForgetProgress("video.mkv");
        store.OnDemand["video.mkv"] = new PlaybackState("video.mkv", 12, 100, false, 2);

        await controller.PlayPathsAsync(["video.mkv"], 0);

        Assert.Equal(TimeSpan.FromSeconds(12), second.LastSeek);
    }

    [Fact]
    public async Task Ab_loop_seeks_back_to_a_at_b()
    {
        var engine = new FakeEngine();
        using var controller = Create(engine);
        await controller.PlayPathsAsync(["video.mkv"], 0);
        controller.Seek(TimeSpan.FromSeconds(5));
        controller.SetLoopA();
        controller.Seek(TimeSpan.FromSeconds(8));
        controller.SetLoopB();

        controller.Seek(TimeSpan.FromSeconds(8.1));
        controller.EnforceLoop();

        Assert.Equal(TimeSpan.FromSeconds(5), engine.LastSeek);
    }

    [Fact]
    public async Task Audio_delay_is_applied_and_saved_per_file()
    {
        var engine = new FakeEngine();
        var preferences = new FakePreferenceStore();
        using var controller = Create(engine, preferences: preferences);
        await controller.PlayPathsAsync(["video.mkv"], 0);

        controller.SetAudioDelay(500);

        Assert.Equal(500, engine.AudioDelayMs);
        Assert.Equal(500, preferences.Stored?.AudioDelayMs);
    }

    [Fact]
    public async Task Audio_output_failure_reopens_current_item_with_default_device()
    {
        var first = new FakeEngine();
        var second = new FakeEngine();
        var engines = new Queue<IPlaybackEngine>([first, second]);
        var outputIds = new List<string?>();
        using var controller = new PlaybackController(
            action => action(),
            () => throw new InvalidOperationException("测试不应读取编解码器"),
            () => "saved-device",
            openEngine: (_, _) =>
            {
                outputIds.Add("saved-device");
                return (engines.Dequeue(), "测试引擎");
            });
        await controller.PlayPathsAsync(["video.mkv"], 0);

        first.RaiseAudioOutputFailure(new InvalidOperationException("device removed"));
        await WaitFor(() => second.PlayCount > 0);

        Assert.Equal("video.mkv", controller.Playlist.Current?.Path);
        Assert.Equal(2, outputIds.Count);
        Assert.Contains("系统默认", controller.Status);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 100; i++)
        {
            if (condition())
                return;
            await Task.Delay(20);
        }
        Assert.Fail("异步播放状态没有及时更新。");
    }

    private static PlaybackController Create(
        FakeEngine engine,
        IPlaybackStateStore? store = null,
        Func<bool>? historyEnabled = null,
        IMediaPreferenceStore? preferences = null) => new(
        action => action(),
        () => throw new InvalidOperationException("测试不应读取编解码器"),
        () => null,
        store,
        openEngine: (_, _) => (engine, "测试引擎"),
        historyEnabled: historyEnabled,
        preferences: preferences);

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

    private sealed class FakePreferenceStore : IMediaPreferenceStore
    {
        public MediaPreference? Stored { get; set; }

        public MediaPreference? LoadPreference(string path) => Stored;

        public void SavePreference(MediaPreference preference) => Stored = preference;
    }

    private sealed class FakeEngine : IPlaybackEngine, IPlaybackFaultSource, IPlaybackAudioEnhancements, IPlaybackAudioOutputEvents
    {
        public string Name => "Fake";
        public bool IsOpen { get; private set; } = true;
        public bool IsPlaying { get; private set; }
        public TimeSpan Position { get; private set; }
        public TimeSpan Duration { get; } = TimeSpan.FromSeconds(100);
        public double Rate { get; private set; } = 1;
        public double LastAppliedVolume { get; private set; } = 0.8;
        public int AudioTrackIndex { get; private set; } = -1;
        public int AudioDelayMs { get; private set; }
        public int SubtitleTrackIndex => -1;
        public int PlayCount { get; private set; }
        public int SeekCount { get; private set; }
        public TimeSpan LastSeek { get; private set; }
        public List<MediaTrack> AudioTracks { get; } = [];

        public event Action<FrameLease>? FrameArrived;
        public event Action? Ended;
        public event Action<Exception>? Faulted;
        public event Action<Exception>? AudioOutputFailed;

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
            SeekCount++;
            LastSeek = position;
            Position = position;
        }

        public void SetVolume(double linear) => LastAppliedVolume = linear;

        public void SetMuted(bool muted)
        {
        }

        public void SetRate(double rate) => Rate = rate;

        public void SetAudioDelay(int milliseconds) => AudioDelayMs = milliseconds;

        public void SetNormalization(bool enabled)
        {
        }

        public void SetNightMode(bool enabled)
        {
        }

        public void StepFrame()
        {
        }

        public IReadOnlyList<MediaTrack> GetAudioTracks() => AudioTracks;

        public void SetAudioTrack(int index) => AudioTrackIndex = index;

        public IReadOnlyList<MediaTrack> GetSubtitleTracks() => [];

        public void SetSubtitleTrack(int index)
        {
        }

        public PipelineInfo GetPipeline() => new() { Engine = Name };

        public void Dispose() => IsOpen = false;

        public void RaiseFrame(FrameLease frame) => FrameArrived?.Invoke(frame);

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

        public void RaiseAudioOutputFailure(Exception error) => AudioOutputFailed?.Invoke(error);
    }
}
