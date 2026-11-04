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
    public void CleanModelOutput_strips_think_blocks_control_tokens_and_prefix()
    {
        var text = AiTranslationText.CleanModelOutput("<think>草稿</think><|extra_0|>翻译：你好<|eos|>");

        Assert.Equal("你好", text);
    }

    [Fact]
    public void Prompt_omits_auto_source_and_requires_target_language_only()
    {
        var prompt = AiTranslationText.Prompt([new(0, "おはよう")], "", "zh");

        Assert.DoesNotContain("自动检测", prompt);
        Assert.Contains("只能使用中文普通话", prompt);
    }

    [Fact]
    public void Prompt_requires_simplified_chinese_for_zh_target()
    {
        var prompt = AiTranslationText.Prompt([new(0, "おはよう")], "ja", "zh");

        Assert.Contains("简体中文", prompt);
    }

    [Fact]
    public void Prompt_strict_attempt_rejects_echoed_source()
    {
        var prompt = AiTranslationText.Prompt([new(0, "おはよう")], "ja", "zh", true);

        Assert.Contains("保留了原文", prompt);
        Assert.Contains("不得出现日文假名", prompt);
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
