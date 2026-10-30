using HomePlayer.Ffmpeg;

namespace HomePlayer.Playback.Tests;

public sealed class FfmpegProbeTests
{
    [Fact]
    public void Cancelled_probe_does_not_open_media()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => FfmpegProbe.Read("unused.mkv", cancellation.Token));
    }
}
