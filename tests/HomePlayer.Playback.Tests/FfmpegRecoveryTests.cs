using HomePlayer.Playback;

namespace HomePlayer.Playback.Tests;

public sealed class FfmpegRecoveryTests
{
    [Fact]
    public async Task Corrupt_h264_recovers_to_eof_with_evidence()
    {
        var media = Environment.GetEnvironmentVariable("HOMEPLAYER_TEST_CORRUPT_MEDIA");
        if (string.IsNullOrWhiteSpace(media))
            return;
        using var engine = new HomePlayer.Ffmpeg.FfmpegPlayer();
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fault = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var frames = 0;
        engine.Ended += () => ended.TrySetResult();
        engine.Faulted += error => fault.TrySetResult(error);
        engine.FrameArrived += frame =>
        {
            Interlocked.Increment(ref frames);
            frame.ReturnBuffer();
        };
        engine.Open(media);
        engine.Play();
        var completed = await Task.WhenAny(ended.Task, fault.Task).WaitAsync(TimeSpan.FromSeconds(30));
        if (completed == fault.Task)
            Assert.Fail((await fault.Task) + "\n" + string.Join("\n", engine.GetPipeline().Usage));
        await ended.Task;
        Assert.True(frames > 0);
        Assert.Contains(engine.GetPipeline().Usage, line => line.Contains("恢复", StringComparison.Ordinal));
    }
}
