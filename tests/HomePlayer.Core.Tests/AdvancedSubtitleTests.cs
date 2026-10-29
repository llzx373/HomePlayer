using HomePlayer.Core;

namespace HomePlayer.Core.Tests;

public class AdvancedSubtitleTests
{
    [Fact]
    public void Ass_override_style_and_event_are_preserved()
    {
        var cues = SubtitleText.Parse(
            "[Events]\nDialogue: 0,0:00:01.00,0:00:03.00,Default,,0,0,0,,{\\pos(120,240)\\an7\\fs48\\fnArial\\c&H00332211\\b1\\i1}Styled",
            ".ass");

        var cue = Assert.Single(cues);
        Assert.Equal("Styled", cue.Text);
        var style = AssSubtitleStyle.Parse(cue.AssEvent);
        Assert.NotNull(style);
        Assert.Equal("Arial", style.FontName);
        Assert.Equal(48, style.FontSize);
        Assert.Equal(0x332211u, style.BgrColor);
        Assert.Equal(7, style.Alignment);
        Assert.Equal(120, style.X);
        Assert.Equal(240, style.Y);
        Assert.True(style.Bold);
        Assert.True(style.Italic);
    }

    [Fact]
    public void Snapshot_composer_blends_semitransparent_subtitles()
    {
        var frame = new byte[] { 100, 100, 100, 255 };
        var overlay = new BgraOverlay(1, 1, [50, 50, 50, 128]);

        SnapshotComposer.Apply(frame, 1, 1, overlay);

        Assert.Equal(99, frame[0]);
        Assert.Equal(255, frame[3]);
    }

    [Fact]
    public void Bitmap_cue_is_available_from_shared_track_lookup()
    {
        var bitmap = new SubtitleBitmap(10, 20, 2, 2, new byte[16]);
        var cue = new SubtitleCue(TimeSpan.Zero, TimeSpan.FromSeconds(2), "", [bitmap]);
        var track = new SubtitleTrack([cue]);

        var selected = track.At(TimeSpan.FromSeconds(1));
        Assert.NotNull(selected);
        Assert.Equal(bitmap, Assert.Single(selected.Bitmaps));
    }
}
