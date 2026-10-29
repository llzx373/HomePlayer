using HomePlayer.Ffmpeg;

namespace HomePlayer.Playback.Tests;

public class FfmpegAssRendererTests
{
    [Fact]
    public void Libass_filter_renders_ass_with_alpha()
    {
        Assert.True(FfmpegAssRenderer.IsAvailable(), "当前 FFmpeg 构建未提供 ass 滤镜。");
        var directory = Path.Combine(Path.GetTempPath(), "HomePlayer.Ass." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "sample.ass");
        File.WriteAllText(path, """
            [Script Info]
            ScriptType: v4.00+
            PlayResX: 320
            PlayResY: 180

            [V4+ Styles]
            Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
            Style: Default,Segoe UI,28,&H00FFFFFF,&H00FFFFFF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,1,0,7,10,10,10,1

            [Events]
            Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
            Dialogue: 0,0:00:00.00,0:00:03.00,Default,,0,0,0,,{\pos(40,40)\c&H000000FF&}Libass
            """);
        try
        {
            using var renderer = new FfmpegAssRenderer(path, 320, 180);
            var frame = renderer.Render(TimeSpan.FromSeconds(1));

            Assert.Equal(320, frame.Width);
            Assert.Equal(180, frame.Height);
            Assert.Contains(frame.Bgra, value => value != 0);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
