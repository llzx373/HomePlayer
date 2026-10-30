using HomePlayer.Core;
using HomePlayer.Ffmpeg;

namespace HomePlayer.Playback.Tests;

public sealed class FfmpegVideoFrameTests
{
    [Fact]
    public async Task Nv12_media_delivers_z2_planes_and_preserves_bgra_fallback()
    {
        var media = Environment.GetEnvironmentVariable("HOMEPLAYER_TEST_NV12_MEDIA");
        if (string.IsNullOrWhiteSpace(media))
            return;

        var yuv = await CaptureAsync(media, true);
        Assert.Equal(VideoPixelFormat.Nv12, yuv.PixelFormat);
        Assert.Equal(ZeroCopyLevel.Z2, yuv.ZeroCopyLevel);
        Assert.Equal(2, yuv.Planes.Count);
        var lease = new CpuFrameLease { Width = yuv.Width, Height = yuv.Height, Data = yuv.Data, Planes = yuv.Planes };
        Assert.True(VideoFrameLayout.TryGetPlane(lease, 0, yuv.Width, yuv.Height, out _));
        Assert.True(VideoFrameLayout.TryGetPlane(lease, 1, yuv.Width, yuv.Height / 2, out _));
        Assert.Equal("nv12", yuv.PipelinePixelFormat);
        Assert.Equal("Z2", yuv.PipelineZeroCopyLevel);

        var bgra = await CaptureAsync(media, false);
        Assert.Equal(VideoPixelFormat.Bgra32, bgra.PixelFormat);
        Assert.Equal(ZeroCopyLevel.Z1, bgra.ZeroCopyLevel);
        Assert.Equal("bgra", bgra.PipelinePixelFormat);
        Assert.Equal("Z1", bgra.PipelineZeroCopyLevel);

        var meanError = CompareBgra(bgra, ToBgra(yuv));
        var mean601 = CompareBgra(bgra, ToBgra(yuv, VideoColorMatrix.Bt601));
        Assert.True(meanError <= 8, $"颜色元数据：{yuv.Color}；默认/BT.601 平均误差：{meanError:0.###}/{mean601:0.###}，容差 8。");
    }

    [Fact]
    public async Task P010_media_delivers_aligned_z2_planes()
    {
        var media = Environment.GetEnvironmentVariable("HOMEPLAYER_TEST_P010_MEDIA");
        if (string.IsNullOrWhiteSpace(media))
            return;

        var frame = await CaptureAsync(media, true);
        Assert.Equal(VideoPixelFormat.P010, frame.PixelFormat);
        Assert.Equal(ZeroCopyLevel.Z2, frame.ZeroCopyLevel);
        Assert.Equal("p010", frame.PipelinePixelFormat);
        Assert.Equal("Z2", frame.PipelineZeroCopyLevel);
        var lease = new CpuFrameLease { Width = frame.Width, Height = frame.Height, PixelFormat = frame.PixelFormat, Data = frame.Data, Planes = frame.Planes };
        Assert.True(VideoFrameLayout.TryGetPlane(lease, 0, frame.Width * 2, frame.Height, out _));
        Assert.True(VideoFrameLayout.TryGetPlane(lease, 1, frame.Width * 2, frame.Height / 2, out _));
        Assert.True(YuvFrameConverter.TryCopyBgra(lease, out _, out var bgra));
        Assert.Equal(frame.Width * frame.Height * 4, bgra.Length);
    }

    private static async Task<FrameSnapshot> CaptureAsync(string media, bool directYuv)
    {
        using var player = new FfmpegPlayer { EnableDirectYuv = directYuv };
        var signal = new TaskCompletionSource<FrameSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        player.FrameArrived += frame =>
        {
            var snapshot = FrameSnapshot.Capture(frame);
            frame.ReturnBuffer();
            signal.TrySetResult(snapshot);
        };
        player.Open(media);
        player.Play();
        var frame = await signal.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var pipeline = player.GetPipeline();
        return frame with { PipelinePixelFormat = pipeline.PixelFormat, PipelineZeroCopyLevel = pipeline.ZeroCopyLevel };
    }

    private static FrameSnapshot ToBgra(FrameSnapshot source, VideoColorMatrix? matrix = null)
    {
        var output = new byte[source.Width * source.Height * 4];
        var luma = source.Planes[0];
        var chroma = source.Planes[1];
        var color = YuvColorConversion.For(source.Color with { Matrix = matrix ?? source.Color.Matrix }, source.Height);
        for (var y = 0; y < source.Height; y++)
        {
            for (var x = 0; x < source.Width; x++)
            {
                var yy = source.Data[luma.Offset + y * luma.Stride + x] / 255f;
                var (u, v) = SampleChroma(source.Data, chroma, source.Height / 2, x * 0.5f - 0.25f, y * 0.5f - 0.25f);
                u -= color.ChromaOffset;
                v -= color.ChromaOffset;
                var scaledY = (yy - color.YOffset) * color.YScale;
                var index = (y * source.Width + x) * 4;
                output[index] = ToByte(scaledY + color.BlueU * u);
                output[index + 1] = ToByte(scaledY - color.GreenU * u - color.GreenV * v);
                output[index + 2] = ToByte(scaledY + color.RedV * v);
                output[index + 3] = 255;
            }
        }
        return source with { PixelFormat = VideoPixelFormat.Bgra32, Stride = source.Width * 4, Data = output };
    }

    private static (float U, float V) SampleChroma(byte[] data, VideoFramePlane plane, int height, float x, float y)
    {
        var width = plane.Stride / 2;
        var left = (int)MathF.Floor(x);
        var top = (int)MathF.Floor(y);
        var fx = x - left;
        var fy = y - top;
        var x0 = Math.Clamp(left, 0, width - 1);
        var x1 = Math.Clamp(left + 1, 0, width - 1);
        var y0 = Math.Clamp(top, 0, height - 1);
        var y1 = Math.Clamp(top + 1, 0, height - 1);
        float Sample(int px, int py, int component) => data[plane.Offset + py * plane.Stride + px * 2 + component] / 255f;
        var u0 = Sample(x0, y0, 0) + (Sample(x1, y0, 0) - Sample(x0, y0, 0)) * fx;
        var u1 = Sample(x0, y1, 0) + (Sample(x1, y1, 0) - Sample(x0, y1, 0)) * fx;
        var v0 = Sample(x0, y0, 1) + (Sample(x1, y0, 1) - Sample(x0, y0, 1)) * fx;
        var v1 = Sample(x0, y1, 1) + (Sample(x1, y1, 1) - Sample(x0, y1, 1)) * fx;
        return (u0 + (u1 - u0) * fy, v0 + (v1 - v0) * fy);
    }

    private static double CompareBgra(FrameSnapshot expected, FrameSnapshot actual)
    {
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);
        long error = 0;
        var samples = 0;
        for (var y = 0; y < expected.Height; y++)
        {
            for (var x = 0; x < expected.Width; x++)
            {
                var source = y * expected.Stride + x * 4;
                var target = y * actual.Stride + x * 4;
                for (var channel = 0; channel < 3; channel++)
                {
                    error += Math.Abs(expected.Data[source + channel] - actual.Data[target + channel]);
                    samples++;
                }
            }
        }
        return (double)error / samples;
    }

    private static byte ToByte(float value) => (byte)Math.Clamp((int)MathF.Round(value * 255), 0, 255);

    private sealed record FrameSnapshot(
        int Width,
        int Height,
        int Stride,
        byte[] Data,
        IReadOnlyList<VideoFramePlane> Planes,
        VideoPixelFormat PixelFormat,
        ZeroCopyLevel ZeroCopyLevel,
        VideoColorMetadata Color)
    {
        public string? PipelinePixelFormat { get; init; }
        public string? PipelineZeroCopyLevel { get; init; }

        public static FrameSnapshot Capture(FrameLease frame)
        {
            if (frame is VideoFrame bgra)
            {
                var length = bgra.Stride * bgra.Height;
                var data = new byte[length];
                Buffer.BlockCopy(bgra.Data, 0, data, 0, length);
                return new FrameSnapshot(bgra.Width, bgra.Height, bgra.Stride, data, [], bgra.PixelFormat, bgra.ZeroCopyLevel, bgra.Color);
            }
            var cpu = (CpuFrameLease)frame;
            var required = cpu.Planes.Count > 1
                ? cpu.Planes[1].Offset + cpu.Planes[1].Stride * (frame.Height / 2)
                : cpu.Data.Length;
            var yuvData = new byte[Math.Min(required, cpu.Data.Length)];
            Buffer.BlockCopy(cpu.Data, 0, yuvData, 0, yuvData.Length);
            return new FrameSnapshot(cpu.Width, cpu.Height, 0, yuvData, [.. cpu.Planes], cpu.PixelFormat, cpu.ZeroCopyLevel, cpu.Color);
        }
    }
}
