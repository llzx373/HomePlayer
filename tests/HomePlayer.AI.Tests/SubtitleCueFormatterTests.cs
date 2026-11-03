using HomePlayer.AI;

namespace HomePlayer.AI.Tests;

public class SubtitleCueFormatterTests
{
    [Fact]
    public void Long_sentence_is_split_into_readable_cues_with_continuous_time()
    {
        var cues = SubtitleCueFormatter.Build(
            "This is a very long sentence that should not be squeezed into one subtitle because viewers need enough time to read all of the words comfortably.",
            TimeSpan.Zero, TimeSpan.FromSeconds(10));

        Assert.True(cues.Count > 1);
        Assert.Equal(TimeSpan.Zero, cues[0].Start);
        Assert.Equal(TimeSpan.FromSeconds(10), cues[^1].End);
        Assert.All(cues, cue => Assert.True(cue.End > cue.Start));
        for (var index = 1; index < cues.Count; index++)
            Assert.Equal(cues[index - 1].End, cues[index].Start);
    }

    [Fact]
    public void Cjk_text_is_wrapped_without_losing_content()
    {
        var cues = SubtitleCueFormatter.Build(
            "这是一句需要拆分的很长中文字幕。它应该在阅读速度和两行限制之间取得平衡，而不是直接塞进一整块。",
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(8));

        Assert.NotEmpty(cues);
        Assert.All(cues, cue => Assert.True(cue.Text.Split('\n').Length <= 2));
        Assert.Equal("这是一句需要拆分的很长中文字幕。它应该在阅读速度和两行限制之间取得平衡，而不是直接塞进一整块。", string.Concat(cues.Select(cue => cue.Text.Replace("\n", ""))));
    }

    [Fact]
    public void Wrapping_does_not_split_an_english_word()
    {
        var cues = SubtitleCueFormatter.Build(
            "Finally, we will review the results and discuss the next steps together.",
            TimeSpan.Zero, TimeSpan.FromSeconds(4));

        Assert.DoesNotContain(cues, cue => cue.Text.Contains("th\ne", StringComparison.Ordinal));
    }

    [Fact]
    public void Cjk_sentence_splits_at_token_boundary_without_losing_content()
    {
        const string text = "内部 進学 を する から 付き 合っ ている 彼氏 が 内部 進学 する こと に なった。";
        var cues = SubtitleCueFormatter.Build(text, TimeSpan.Zero, TimeSpan.FromSeconds(10));

        Assert.True(cues.Count > 1);
        Assert.Equal(text.Replace(" ", ""), string.Concat(cues.Select(cue => cue.Text.Replace("\n", "").Replace(" ", ""))));
        Assert.DoesNotContain(cues, cue => cue.Text.EndsWith("じ", StringComparison.Ordinal));
    }

    [Fact]
    public void Empty_or_invalid_input_returns_no_cues()
    {
        Assert.Empty(SubtitleCueFormatter.Build(" ", TimeSpan.Zero, TimeSpan.FromSeconds(1)));
        Assert.Empty(SubtitleCueFormatter.Build("text", TimeSpan.Zero, TimeSpan.Zero));
    }
}
