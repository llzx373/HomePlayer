using HomePlayer.Core;

namespace HomePlayer.Core.Tests;

public class SubtitleFileWriterTests
{
    [Fact]
    public void Srt_roundtrip_preserves_times_and_multiline_text()
    {
        SubtitleCue[] cues =
        [
            new(TimeSpan.FromMilliseconds(1234), TimeSpan.FromMilliseconds(5678), "你好\n世界")
        ];

        var text = SubtitleFileWriter.FormatSrt(cues);
        var parsed = SubtitleText.Parse(text, ".srt");

        var cue = Assert.Single(parsed);
        Assert.Equal(cues[0].Start, cue.Start);
        Assert.Equal(cues[0].End, cue.End);
        Assert.Equal(cues[0].Text, cue.Text);
    }

    [Fact]
    public void Writer_rejects_invalid_time_range()
    {
        var cue = new SubtitleCue(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1), "无效");

        Assert.Throws<InvalidDataException>(() => SubtitleFileWriter.FormatSrt([cue]));
    }
}
