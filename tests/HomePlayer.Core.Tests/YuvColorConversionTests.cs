using HomePlayer.Core;

namespace HomePlayer.Core.Tests;

public class YuvColorConversionTests
{
    [Fact]
    public void Limited_bt709_matches_reference_values()
    {
        var value = YuvColorConversion.For(new VideoColorMetadata(VideoColorMatrix.Bt709, VideoColorRange.Limited), 1080);

        Assert.Equal(16f / 255f, value.YOffset, 6);
        Assert.Equal(255f / 219f, value.YScale, 6);
        Assert.Equal(1.79274107f, value.RedV, 6);
        Assert.Equal(0.213248614f, value.GreenU, 6);
        Assert.Equal(0.532909328f, value.GreenV, 6);
        Assert.Equal(2.11240179f, value.BlueU, 6);
    }

    [Fact]
    public void Full_range_uses_unscaled_luma()
    {
        var value = YuvColorConversion.For(new VideoColorMetadata(VideoColorMatrix.Bt601, VideoColorRange.Full), 576);

        Assert.Equal(0, value.YOffset);
        Assert.Equal(1, value.YScale);
        Assert.Equal(1.59602717f, value.RedV, 6);
        Assert.Equal(2.01723214f, value.BlueU, 6);
    }

    [Theory]
    [InlineData(480)]
    [InlineData(720)]
    [InlineData(2160)]
    public void Unknown_matrix_preserves_swscale_bt601_default(int height)
    {
        var value = YuvColorConversion.For(default, height);
        Assert.Equal(1.59602717f, value.RedV, 6);
        Assert.Equal(16f / 255f, value.YOffset, 6);
    }

    [Fact]
    public void Bt2020_uses_its_own_matrix()
    {
        var value = YuvColorConversion.For(new VideoColorMetadata(VideoColorMatrix.Bt2020), 2160);
        Assert.Equal(1.4746f, value.RedV, 6);
        Assert.Equal(1.8814f, value.BlueU, 6);
    }
}
