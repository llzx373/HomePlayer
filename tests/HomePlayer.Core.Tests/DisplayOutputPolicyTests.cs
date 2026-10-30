using HomePlayer.Core;

namespace HomePlayer.Core.Tests;

public class DisplayOutputPolicyTests
{
    [Theory]
    [InlineData(VideoTransferFunction.Pq, VideoOutputTransform.None)]
    [InlineData(VideoTransferFunction.Hlg, VideoOutputTransform.HlgToPq)]
    [InlineData(VideoTransferFunction.Sdr, VideoOutputTransform.SdrToPq)]
    [InlineData(VideoTransferFunction.Unknown, VideoOutputTransform.SdrToPq)]
    public void Hdr_display_selects_fp16_output_and_input_transform(VideoTransferFunction input, VideoOutputTransform expected)
    {
        var selection = DisplayOutputPolicy.Select(true, input);
        Assert.True(selection.HdrOutput);
        Assert.Equal(expected, selection.Transform);
    }

    [Theory]
    [InlineData(VideoTransferFunction.Pq, VideoOutputTransform.PqToSdr)]
    [InlineData(VideoTransferFunction.Hlg, VideoOutputTransform.HlgToSdr)]
    [InlineData(VideoTransferFunction.Sdr, VideoOutputTransform.None)]
    [InlineData(VideoTransferFunction.Unknown, VideoOutputTransform.None)]
    public void Sdr_display_preserves_sdr_or_tone_maps_hdr(VideoTransferFunction input, VideoOutputTransform expected)
    {
        var selection = DisplayOutputPolicy.Select(false, input);
        Assert.False(selection.HdrOutput);
        Assert.Equal(expected, selection.Transform);
    }
}
