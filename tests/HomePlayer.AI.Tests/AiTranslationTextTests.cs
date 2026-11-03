using HomePlayer.AI;

namespace HomePlayer.AI.Tests;

public class AiTranslationTextTests
{
    [Fact]
    public void Parse_preserves_multiline_translation()
    {
        var map = AiTranslationText.Parse("@@0@@你好\n世界\n@@1@@下一句", [0, 1]);

        Assert.Equal("你好\n世界", map[0]);
        Assert.Equal("下一句", map[1]);
    }

    [Fact]
    public void Parse_rejects_duplicate_and_unknown_ids()
    {
        var map = AiTranslationText.Parse("@@0@@正确\n@@0@@重复\n@@9@@越界", [0]);

        Assert.Equal("正确", map[0]);
        Assert.Single(map);
    }

    [Fact]
    public void Prompt_keeps_stable_ids_and_language_direction()
    {
        var prompt = AiTranslationText.Prompt([new(7, "おはよう")], "ja", "zh");

        Assert.Contains("@@7@@おはよう", prompt);
        Assert.Contains("日语", prompt);
        Assert.Contains("中文普通话", prompt);
    }
}
