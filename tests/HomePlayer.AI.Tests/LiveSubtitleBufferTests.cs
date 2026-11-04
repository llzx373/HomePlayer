using HomePlayer.AI;
using HomePlayer.Core;

namespace HomePlayer.AI.Tests;

public class LiveSubtitleBufferTests
{
    [Fact]
    public void Draft_is_replaced_by_final_and_final_cues_exclude_drafts()
    {
        var buffer = new LiveSubtitleBuffer();
        var draft = Cue(0, 2, "草稿");

        Assert.True(buffer.Apply(new SubtitleSegmentUpdate(1, false, [draft])));
        Assert.True(buffer.IsProvisional(draft));
        Assert.Single(buffer.Cues);
        Assert.Empty(buffer.FinalCues);

        var final = Cue(0, 3, "定稿");
        Assert.True(buffer.Apply(new SubtitleSegmentUpdate(1, true, [final])));
        Assert.False(buffer.IsProvisional(final));
        Assert.Single(buffer.Cues);
        Assert.Equal(final, buffer.Cues[0]);
        Assert.Equal(final, Assert.Single(buffer.FinalCues));
    }

    [Fact]
    public void Empty_final_clears_draft()
    {
        var buffer = new LiveSubtitleBuffer();
        Assert.True(buffer.Apply(new SubtitleSegmentUpdate(1, false, [Cue(0, 2, "草稿")])));

        Assert.True(buffer.Apply(new SubtitleSegmentUpdate(1, true, [])));

        Assert.Empty(buffer.Cues);
        Assert.Empty(buffer.FinalCues);
    }

    [Fact]
    public void Late_draft_after_final_is_ignored()
    {
        var buffer = new LiveSubtitleBuffer();
        var final = Cue(0, 3, "定稿");
        Assert.True(buffer.Apply(new SubtitleSegmentUpdate(1, true, [final])));

        Assert.False(buffer.Apply(new SubtitleSegmentUpdate(1, false, [Cue(0, 2, "草稿")])));

        Assert.Equal(final, Assert.Single(buffer.Cues));
    }

    [Fact]
    public void Segments_are_ordered_by_id()
    {
        var buffer = new LiveSubtitleBuffer();
        var first = Cue(0, 1, "a");
        var second = Cue(2, 3, "b");

        Assert.True(buffer.Apply(new SubtitleSegmentUpdate(2, true, [second])));
        Assert.True(buffer.Apply(new SubtitleSegmentUpdate(1, true, [first])));

        Assert.Equal(new[] { first, second }, buffer.Cues);
    }

    [Fact]
    public void Identical_update_is_ignored()
    {
        var buffer = new LiveSubtitleBuffer();
        var cue = Cue(0, 2, "x");

        Assert.True(buffer.Apply(new SubtitleSegmentUpdate(1, false, [cue])));
        Assert.False(buffer.Apply(new SubtitleSegmentUpdate(1, false, [cue])));
    }

    [Fact]
    public void Provisional_check_uses_reference_not_value()
    {
        var buffer = new LiveSubtitleBuffer();
        var draft = Cue(0, 2, "草稿");
        buffer.Apply(new SubtitleSegmentUpdate(1, false, [draft]));

        Assert.True(buffer.IsProvisional(draft));
        Assert.False(buffer.IsProvisional(Cue(0, 2, "草稿")));
    }

    [Fact]
    public void Clear_removes_all_segments()
    {
        var buffer = new LiveSubtitleBuffer();
        buffer.Apply(new SubtitleSegmentUpdate(1, false, [Cue(0, 2, "草稿")]));
        buffer.Apply(new SubtitleSegmentUpdate(2, true, [Cue(3, 4, "定稿")]));

        buffer.Clear();

        Assert.Empty(buffer.Cues);
        Assert.Empty(buffer.FinalCues);
    }

    private static SubtitleCue Cue(double start, double end, string text) =>
        new(TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end), text);
}
