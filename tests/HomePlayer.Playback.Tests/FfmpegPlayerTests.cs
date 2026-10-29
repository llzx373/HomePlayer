using HomePlayer.Core;
using HomePlayer.Ffmpeg;

namespace HomePlayer.Playback.Tests;

public class FfmpegPlayerTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "HomePlayer.Media." + Guid.NewGuid().ToString("N"));
    private readonly string _mediaPath;

    public FfmpegPlayerTests()
    {
        _mediaPath = TestMedia.CreateAvi(_directory);
    }

    [Fact]
    public async Task Real_media_opens_seeks_steps_and_switches_audio_tracks()
    {
        using var player = new FfmpegPlayer();
        var frames = 0;
        player.FrameArrived += frame =>
        {
            Assert.Equal(32, frame.Width);
            Assert.Equal(24, frame.Height);
            frame.ReturnBuffer();
            Interlocked.Increment(ref frames);
        };
        player.Open(_mediaPath);
        Assert.True(Math.Abs((player.Duration - TimeSpan.FromSeconds(2)).TotalMilliseconds) <= 100,
            $"媒体时长应为 2 秒，实际为 {player.Duration}。");
        Assert.Equal(2, player.GetAudioTracks().Count);

        player.Play();
        await WaitUntilAsync(() => Volatile.Read(ref frames) >= 1);
        player.Pause();
        player.Seek(TimeSpan.FromMilliseconds(500));
        player.StepFrame();
        await WaitUntilAsync(() => Volatile.Read(ref frames) >= 2);

        var second = player.GetAudioTracks()[1];
        player.SetAudioTrack(second.Index);
        await WaitUntilAsync(() => player.AudioTrackIndex == second.Index);
        Assert.False(player.IsPlaying);
    }

    [Fact]
    public async Task Real_media_raises_normal_end_of_file()
    {
        using var player = new FfmpegPlayer();
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? fault = null;
        player.FrameArrived += frame => frame.ReturnBuffer();
        player.Ended += () => ended.TrySetResult();
        player.Faulted += error => fault = error;
        player.Open(_mediaPath);

        player.Play();

        await ended.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Null(fault);
        Assert.True(Math.Abs((player.Duration - player.Position).TotalMilliseconds) <= 250,
            $"播完位置应接近总时长 {player.Duration}，实际为 {player.Position}。");
    }

    [Fact]
    public void Invalid_media_reports_an_open_error()
    {
        var invalid = Path.Combine(_directory, "invalid.mkv");
        File.WriteAllText(invalid, "not a video");
        using var player = new FfmpegPlayer();

        Assert.Throws<InvalidOperationException>(() => player.Open(invalid));
        Assert.False(player.IsOpen);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("等待 FFmpeg 播放状态超时。");
            await Task.Delay(50);
        }
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
