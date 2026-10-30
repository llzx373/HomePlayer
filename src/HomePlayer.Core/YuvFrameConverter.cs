namespace HomePlayer.Core;

public static class YuvFrameConverter
{
    public static bool TryCopyBgra(CpuFrameLease frame, out int stride, out byte[] data)
    {
        stride = 0;
        data = [];
        if (frame.PixelFormat is not (VideoPixelFormat.Nv12 or VideoPixelFormat.P010)
            || frame.Width < 2 || frame.Height < 2 || frame.Width % 2 != 0 || frame.Height % 2 != 0)
            return false;
        var bytesPerSample = frame.PixelFormat == VideoPixelFormat.P010 ? 2 : 1;
        if (!VideoFrameLayout.TryGetPlane(frame, 0, frame.Width * bytesPerSample, frame.Height, out var luma)
            || !VideoFrameLayout.TryGetPlane(frame, 1, frame.Width * bytesPerSample, frame.Height / 2, out var chroma))
            return false;

        stride = checked(frame.Width * 4);
        data = new byte[checked(stride * frame.Height)];
        var color = YuvColorConversion.For(frame.Color, frame.Height);
        for (var y = 0; y < frame.Height; y++)
        {
            for (var x = 0; x < frame.Width; x++)
            {
                var yy = ReadSample(frame.Data, luma, x, y, 0, frame.Width, frame.Height, bytesPerSample, 1);
                var (u, v) = SampleChroma(frame.Data, chroma, frame.Height / 2, bytesPerSample,
                    x * 0.5f - 0.25f, y * 0.5f - 0.25f);
                u -= color.ChromaOffset;
                v -= color.ChromaOffset;
                var scaledY = (yy - color.YOffset) * color.YScale;
                var index = y * stride + x * 4;
                data[index] = ToByte(scaledY + color.BlueU * u);
                data[index + 1] = ToByte(scaledY - color.GreenU * u - color.GreenV * v);
                data[index + 2] = ToByte(scaledY + color.RedV * v);
                data[index + 3] = 255;
            }
        }
        return true;
    }

    private static (float U, float V) SampleChroma(
        byte[] data,
        VideoFramePlane plane,
        int height,
        int bytesPerSample,
        float x,
        float y)
    {
        var width = plane.Stride / (2 * bytesPerSample);
        var left = (int)MathF.Floor(x);
        var top = (int)MathF.Floor(y);
        var fx = x - left;
        var fy = y - top;
        var x0 = Math.Clamp(left, 0, width - 1);
        var x1 = Math.Clamp(left + 1, 0, width - 1);
        var y0 = Math.Clamp(top, 0, height - 1);
        var y1 = Math.Clamp(top + 1, 0, height - 1);
        var u0 = Lerp(Read(data, plane, x0, y0, 0, bytesPerSample), Read(data, plane, x1, y0, 0, bytesPerSample), fx);
        var u1 = Lerp(Read(data, plane, x0, y1, 0, bytesPerSample), Read(data, plane, x1, y1, 0, bytesPerSample), fx);
        var v0 = Lerp(Read(data, plane, x0, y0, 1, bytesPerSample), Read(data, plane, x1, y0, 1, bytesPerSample), fx);
        var v1 = Lerp(Read(data, plane, x0, y1, 1, bytesPerSample), Read(data, plane, x1, y1, 1, bytesPerSample), fx);
        return (Lerp(u0, u1, fy), Lerp(v0, v1, fy));
    }

    private static float ReadSample(
        byte[] data,
        VideoFramePlane plane,
        int x,
        int y,
        int component,
        int width,
        int height,
        int bytesPerSample,
        int componentsPerPixel)
    {
        x = Math.Clamp(x, 0, width - 1);
        y = Math.Clamp(y, 0, height - 1);
        return Read(data, plane, x, y, component, bytesPerSample, componentsPerPixel);
    }

    private static float Read(byte[] data, VideoFramePlane plane, int x, int y, int component, int bytesPerSample, int componentsPerPixel = 2)
    {
        var index = plane.Offset + (y * plane.Stride) + ((x * componentsPerPixel + component) * bytesPerSample);
        return bytesPerSample == 1
            ? data[index] / 255f
            : (data[index] | (data[index + 1] << 8)) / 65535f;
    }

    private static float Lerp(float left, float right, float amount) => left + (right - left) * amount;

    private static byte ToByte(float value) => (byte)Math.Clamp((int)MathF.Round(value * 255), 0, 255);
}
