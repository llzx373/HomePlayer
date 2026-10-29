using System.Diagnostics;
using HomePlayer.Ffmpeg;

namespace HomePlayer.Playback.Tests;

public class FfmpegAssExtractionTests
{
    [Fact]
    public void Embedded_ass_events_and_font_attachments_are_extracted()
    {
        var directory = Path.Combine(Path.GetTempPath(), "HomePlayer.AssEmbed." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var ass = Path.Combine(directory, "sample.ass");
            File.WriteAllText(ass, """
                [Script Info]
                ScriptType: v4.00+
                PlayResX: 320
                PlayResY: 180
                [V4+ Styles]
                Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
                Style: Default,Arial,28,&H00FFFFFF,&H00FFFFFF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,1,0,7,10,10,10,1
                [Events]
                Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
                Dialogue: 0,0:00:00.00,0:00:03.00,Default,,0,0,0,,Embedded
                """);
            var media = Path.Combine(directory, "sample.mkv");
            var args = $"-y -loglevel error -f lavfi -i color=c=black:s=320x180:d=3 -i \"{ass}\" -attach \"C:\\Windows\\Fonts\\arial.ttf\" -metadata:s:t:0 mimetype=application/x-truetype-font -map 0:v -map 1:s -c:v ffv1 -c:s ass \"{media}\"";
            using (var process = Process.Start(new ProcessStartInfo("ffmpeg", args) { UseShellExecute = false })!)
            {
                process.WaitForExit(30_000);
                Assert.Equal(0, process.ExitCode);
            }
            var extracted = Path.Combine(directory, "extracted.ass");
            var fonts = Path.Combine(directory, "fonts");

            FfmpegAssExtraction.Extract(media, 1, extracted, fonts);

            var text = File.ReadAllText(extracted);
            Assert.Contains("Dialogue:", text);
            Assert.Contains("Embedded", text);
            Assert.True(File.Exists(Path.Combine(fonts, "arial.ttf")));
            using var renderer = new FfmpegAssRenderer(extracted, 320, 180, fonts);
            Assert.True(renderer.Render(TimeSpan.FromSeconds(1)).Bgra.Any(value => value != 0), text);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
