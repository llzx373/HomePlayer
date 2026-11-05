using HomePlayer.Core;

namespace HomePlayer.Core.Tests;

public class SubtitleDisplayTests
{
    private static SubtitleCue Cue(string text, string? translation) =>
        new(TimeSpan.Zero, TimeSpan.FromSeconds(2), text, Translation: translation);

    [Fact]
    public void Both_shows_original_and_translation()
    {
        var (primary, secondary) = SubtitleDisplay.Split(Cue("原文", "译文"), SubtitleDisplayMode.Both);

        Assert.Equal("原文", primary);
        Assert.Equal("译文", secondary);
    }

    [Fact]
    public void Original_hides_translation()
    {
        var (primary, secondary) = SubtitleDisplay.Split(Cue("原文", "译文"), SubtitleDisplayMode.Original);

        Assert.Equal("原文", primary);
        Assert.Null(secondary);
    }

    [Fact]
    public void Translation_replaces_original()
    {
        var (primary, secondary) = SubtitleDisplay.Split(Cue("原文", "译文"), SubtitleDisplayMode.Translation);

        Assert.Equal("译文", primary);
        Assert.Null(secondary);
    }

    [Fact]
    public void Missing_translation_falls_back_to_original()
    {
        foreach (var mode in new[] { SubtitleDisplayMode.Both, SubtitleDisplayMode.Original, SubtitleDisplayMode.Translation })
        {
            var (primary, secondary) = SubtitleDisplay.Split(Cue("原文", null), mode);

            Assert.Equal("原文", primary);
            Assert.Null(secondary);
        }
    }

    [Fact]
    public void Empty_cue_produces_empty_text()
    {
        var (primary, secondary) = SubtitleDisplay.Split(null, SubtitleDisplayMode.Both);

        Assert.Equal("", primary);
        Assert.Null(secondary);
    }
}
