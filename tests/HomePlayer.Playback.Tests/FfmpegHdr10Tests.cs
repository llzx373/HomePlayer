using System.Diagnostics;
using HomePlayer.Core;
using HomePlayer.Ffmpeg;

namespace HomePlayer.Playback.Tests;

public sealed class FfmpegHdr10Tests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "HomePlayer.Hdr10." + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Decoded_frame_preserves_source_mastering_and_content_light_metadata()
    {
        var media = CreateMedia();
        using var player = new FfmpegPlayer();
        var captured = new TaskCompletionSource<VideoHdr10Metadata>(TaskCreationOptions.RunContinuationsAsynchronously);
        player.FrameArrived += frame =>
        {
            if (frame.Color.Hdr10 is { } metadata)
                captured.TrySetResult(metadata);
            frame.ReturnBuffer();
        };
        player.Open(media);
        player.Play();

        var metadata = await captured.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(34000, metadata.RedPrimaryX);
        Assert.Equal(16000, metadata.RedPrimaryY);
        Assert.Equal(8500, metadata.GreenPrimaryX);
        Assert.Equal(39850, metadata.GreenPrimaryY);
        Assert.Equal(6550, metadata.BluePrimaryX);
        Assert.Equal(2300, metadata.BluePrimaryY);
        Assert.Equal(15635, metadata.WhitePointX);
        Assert.Equal(16450, metadata.WhitePointY);
        Assert.Equal(1000u, metadata.MaxMasteringLuminance);
        Assert.Equal(50u, metadata.MinMasteringLuminance);
        Assert.Equal(1200u, metadata.MaxContentLightLevel);
        Assert.Equal(500u, metadata.MaxFrameAverageLightLevel);
    }

    private string CreateMedia()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "hdr10.mkv");
        using var process = Process.Start(new ProcessStartInfo("ffmpeg")
        {
            UseShellExecute = false,
            Arguments = $"-y -loglevel error -mastering_display \"G(8500,39850)B(6550,2300)R(34000,16000)WP(15635,16450)L(10000000,50)\" -f lavfi -i \"testsrc2=size=64x48:rate=25:duration=1\" -c:v libx265 -pix_fmt yuv420p10le -color_primaries bt2020 -color_trc smpte2084 -colorspace bt2020nc -x265-params \"max-cll=1200,500\" -an \"{path}\""
        })!;
        Assert.True(process.WaitForExit(30_000), "生成 HDR10 元数据测试媒体超时。");
        Assert.Equal(0, process.ExitCode);
        return path;
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
