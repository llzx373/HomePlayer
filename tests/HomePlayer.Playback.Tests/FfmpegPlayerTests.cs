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
        var frames = 0;
        player.FrameArrived += frame =>
        {
            frame.ReturnBuffer();
            Interlocked.Increment(ref frames);
        };
        player.Ended += () => ended.TrySetResult();
        player.Faulted += error => fault = error;
        player.Open(_mediaPath);

        player.Play();

        await ended.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Null(fault);
        Assert.Equal(12, Volatile.Read(ref frames));
        Assert.True(Math.Abs((player.Duration - player.Position).TotalMilliseconds) <= 250,
            $"播完位置应接近总时长 {player.Duration}，实际为 {player.Position}。");
    }

    [Fact]
    public async Task Real_media_supports_one_hundred_repeated_seeks_without_demux_fault()
    {
        using var player = new FfmpegPlayer();
        Exception? fault = null;
        var expectedEpoch = 0L;
        var target = TimeSpan.Zero;
        TaskCompletionSource next = NewSignal();
        player.FrameArrived += frame =>
        {
            var tolerance = Math.Max(1, frame.TimeBase.ToTimeSpanTicks(frame.Duration) / 2);
            var matches = frame.Epoch == expectedEpoch
                && frame.TimeBase.ToTimeSpanTicks(frame.PresentationTimestamp) + tolerance >= target.Ticks;
            frame.ReturnBuffer();
            if (matches)
                next.TrySetResult();
        };
        player.Faulted += error => fault = error;
        player.Open(_mediaPath);
        player.Play();
        await next.Task.WaitAsync(TimeSpan.FromSeconds(2));

        for (var i = 0; i < 100; i++)
        {
            next = NewSignal();
            target = TimeSpan.FromMilliseconds((i * 137) % 1800);
            expectedEpoch = player.Epoch + 1;
            player.Seek(target);
            try
            {
                await next.Task.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (TimeoutException ex)
            {
                throw new TimeoutException($"Seek {i} 在 {target.TotalMilliseconds} ms、Epoch {expectedEpoch} 超时；actualEpoch={player.Epoch}, position={player.Position}, queue={player.VideoQueueDepth}, playing={player.IsPlaying}, fault={fault}", ex);
            }
            Assert.Null(fault);
        }
    }

    [Fact]
    public async Task Watchdog_cancels_a_session_when_frame_delivery_stalls()
    {
        using var player = new FfmpegPlayer { WatchdogTimeout = TimeSpan.FromMilliseconds(100) };
        var entered = NewSignal();
        var release = NewSignal();
        var fault = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        player.FrameArrived += frame =>
        {
            entered.TrySetResult();
            release.Task.GetAwaiter().GetResult();
            frame.ReturnBuffer();
        };
        player.Faulted += error => fault.TrySetResult(error);
        player.Open(_mediaPath);
        player.Play();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var error = await fault.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Contains("看门狗超时", error.Message);
            Assert.False(player.IsPlaying);
        }
        finally
        {
            release.TrySetResult();
        }
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

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

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
