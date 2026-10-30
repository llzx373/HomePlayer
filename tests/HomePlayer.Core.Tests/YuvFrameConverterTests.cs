using HomePlayer.Core;

namespace HomePlayer.Core.Tests;

public class YuvFrameConverterTests
{
    [Fact]
    public void Neutral_nv12_converts_to_bgra_only_on_demand()
    {
        var frame = new CpuFrameLease
        {
            Width = 2,
            Height = 2,
            PixelFormat = VideoPixelFormat.Nv12,
            Data = [128, 128, 128, 128, 128, 128],
            Planes = [new VideoFramePlane(0, 2), new VideoFramePlane(4, 2)]
        };

        Assert.True(YuvFrameConverter.TryCopyBgra(frame, out var stride, out var data));
        Assert.Equal(8, stride);
        Assert.Equal(16, data.Length);
        for (var i = 0; i < data.Length; i += 4)
        {
            Assert.InRange(data[i], 128, 132);
            Assert.True(Math.Abs(data[i] - data[i + 1]) <= 1);
            Assert.True(Math.Abs(data[i] - data[i + 2]) <= 1);
            Assert.Equal(255, data[i + 3]);
        }
    }

    [Fact]
    public void Invalid_yuv_planes_do_not_allocate_an_output_frame()
    {
        var frame = new CpuFrameLease
        {
            Width = 2,
            Height = 2,
            PixelFormat = VideoPixelFormat.Nv12,
            Data = new byte[2],
            Planes = [new VideoFramePlane(0, 2), new VideoFramePlane(4, 2)]
        };

        Assert.False(YuvFrameConverter.TryCopyBgra(frame, out _, out var data));
        Assert.Empty(data);
    }
}
