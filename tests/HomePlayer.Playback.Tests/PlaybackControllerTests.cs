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

    private static PlaybackController Create(FakeEngine engine) => new(
        action => action(),
        () => throw new InvalidOperationException("测试不应读取编解码器"),
        () => null,
        openEngine: (_, _) => (engine, "测试引擎"));

    private sealed class FakeEngine : IPlaybackEngine
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
    }
}
