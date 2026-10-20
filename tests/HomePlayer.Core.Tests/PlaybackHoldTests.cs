using HomePlayer.Core;

namespace HomePlayer.Core.Tests;

public class PlaybackHoldTests
{
    [Fact]
    public void Capture_keeps_the_first_open_position()
    {
        var first = PlaybackHold.Capture(true, null, TimeSpan.FromSeconds(12));
        var second = PlaybackHold.Capture(true, first, TimeSpan.FromSeconds(40));

        Assert.Equal(TimeSpan.FromSeconds(12), first);
        Assert.Equal(first, second);
    }

    [Fact]
    public void Capture_skips_closed_or_invalid_positions()
    {
        Assert.Null(PlaybackHold.Capture(false, null, TimeSpan.FromSeconds(8)));
        Assert.Null(PlaybackHold.Capture(true, null, TimeSpan.FromSeconds(-1)));
    }

    [Theory]
    [InlineData(12, 12, false)]
    [InlineData(12, 12.2, false)]
    [InlineData(12, 13, true)]
    [InlineData(12, 11.5, true)]
    public void Restore_seeks_only_after_the_graph_has_drifted(double held, double current, bool expected)
    {
        Assert.Equal(expected, PlaybackHold.NeedsSeek(TimeSpan.FromSeconds(held), TimeSpan.FromSeconds(current)));
        Assert.False(PlaybackHold.NeedsSeek(null, TimeSpan.FromSeconds(current)));
    }
}
