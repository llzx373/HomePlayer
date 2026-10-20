using HomePlayer.Core;

namespace HomePlayer.Core.Tests;

public class PipelineInfoTests
{
    [Fact]
    public void Format_includes_sink_and_usage()
    {
        var info = new PipelineInfo
        {
            FilePath = @"C:\media\movie.mkv",
            Engine = "LAV",
            Container = "MKV",
            DurationSeconds = 90,
            Filters = ["DirectSound", "LAV Audio", "HomePlayer Video Sink", "LAV Video", "LAV Splitter"],
            Connections = ["LAV Video → HomePlayer Video Sink：视频 H264 1920×1080"],
            Usage = ["进程是 64 位", "视频解码：LAV Video Decoder"]
        };

        var text = info.Format();

        Assert.Contains("引擎：LAV", text);
        Assert.Contains("HomePlayer Video Sink", text);
        Assert.Contains("LAV Video → HomePlayer Video Sink", text);
        Assert.Contains("01:30", text);
    }
}
