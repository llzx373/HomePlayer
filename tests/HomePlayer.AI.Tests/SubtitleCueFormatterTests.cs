using HomePlayer.AI;

namespace HomePlayer.AI.Tests;

public class AsrTextOverlapTests
{
    [Fact]
    public void Removes_longest_duplicate_prefix_across_chunks()
    {
        var result = AsrTextOverlap.Remove(
            "ask not what your country can do for you",
            "your country can do for you. Ask what you can do");

        Assert.Equal("Ask what you can do", result);
    }

    [Fact]
    public void Removes_cjk_overlap_ignoring_punctuation_and_case()
    {
        var result = AsrTextOverlap.Remove("这是第一句，不要重复", "第一句, 不要重复。下一句");

        Assert.Equal("下一句", result);
    }

    [Fact]
    public void Keeps_text_when_no_overlap_exists()
    {
        Assert.Equal("new sentence", AsrTextOverlap.Remove("old sentence", "new sentence"));
    }
}

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
    public void Empty_or_invalid_input_returns_no_cues()
    {
        Assert.Empty(SubtitleCueFormatter.Build(" ", TimeSpan.Zero, TimeSpan.FromSeconds(1)));
        Assert.Empty(SubtitleCueFormatter.Build("text", TimeSpan.Zero, TimeSpan.Zero));
    }
}
