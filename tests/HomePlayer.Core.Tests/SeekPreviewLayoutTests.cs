using HomePlayer.Core;

namespace HomePlayer.Core.Tests;

public sealed class SeekPreviewLayoutTests
{
    [Theory]
    [InlineData(0, 1000, 0)]
    [InlineData(500, 1000, 0.5)]
    [InlineData(1000, 1000, 1)]
    [InlineData(-20, 1000, 0)]
    [InlineData(1200, 1000, 1)]
    [InlineData(100, 0, 0)]
    public void Timeline_ratio_is_clamped(double pointerX, double width, double expected)
    {
        Assert.Equal(expected, SeekPreviewLayout.TimelineRatio(pointerX, width), 10);
    }

    [Fact]
    public void Preview_is_centered_over_the_pointer_in_the_middle()
    {
        var placement = SeekPreviewLayout.Place(500, 1000, 176);
        Assert.Equal(412, placement.Left, 10);
        Assert.Equal(88, placement.ArrowCenter, 10);
    }

    [Theory]
    [InlineData(0, 0, 8)]
    [InlineData(40, 0, 40)]
    [InlineData(960, 824, 136)]
    [InlineData(1000, 824, 168)]
    public void Preview_stays_visible_at_the_edges(double pointerX, double expectedLeft, double expectedArrow)
    {
        var placement = SeekPreviewLayout.Place(pointerX, 1000, 176);
        Assert.Equal(expectedLeft, placement.Left, 10);
        Assert.Equal(expectedArrow, placement.ArrowCenter, 10);
    }

    [Fact]
    public void Narrow_track_does_not_produce_negative_coordinates()
    {
        var placement = SeekPreviewLayout.Place(30, 100, 176);
        Assert.Equal(0, placement.Left);
        Assert.InRange(placement.ArrowCenter, 0, 176);
    }
}
