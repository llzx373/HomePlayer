using Vortice.Direct3D11;
using Vortice.DXGI;

namespace HomePlayer.Playback;

public sealed record D3D11CapturedFrame(int Width, int Height, int Stride, byte[] Data);

public static unsafe class D3D11FrameCapture
{
    public static D3D11CapturedFrame CopyBgra(ID3D11Device device, ID3D11DeviceContext context, ID3D11Texture2D source, bool hdrInput)
    {
        var description = source.Description;
        if (description.Width == 0 || description.Height == 0 || description.Width > int.MaxValue || description.Height > int.MaxValue)
            throw new InvalidOperationException("截图纹理尺寸无效。");
        if (!hdrInput && description.Format != Format.B8G8R8A8_UNorm)
            throw new NotSupportedException($"SDR 截图格式不受支持：{description.Format}。");
        if (hdrInput && description.Format != Format.R16G16B16A16_Float)
            throw new NotSupportedException($"HDR 截图格式不受支持：{description.Format}。");
        var stagingDescription = description;
        stagingDescription.Usage = ResourceUsage.Staging;
        stagingDescription.BindFlags = BindFlags.None;
        stagingDescription.CPUAccessFlags = CpuAccessFlags.Read;
        stagingDescription.MiscFlags = ResourceOptionFlags.None;
        stagingDescription.MipLevels = 1;
        stagingDescription.ArraySize = 1;
        using var staging = device.CreateTexture2D(stagingDescription);
        context.CopyResource(staging, source);
        var width = (int)description.Width;
        var height = (int)description.Height;
        var stride = checked(width * 4);
        var data = new byte[checked(stride * height)];
        var mapped = context.Map(staging, 0, MapMode.Read);
        try
        {
            fixed (byte* destination = data)
            {
                for (var y = 0; y < height; y++)
                {
                    var input = (byte*)mapped.DataPointer + (long)y * mapped.RowPitch;
                    var output = destination + (long)y * stride;
                    if (hdrInput)
                        ConvertPqRow(input, output, width);
                    else
                        Buffer.MemoryCopy(input, output, stride, stride);
                }
            }
        }
        finally
        {
            context.Unmap(staging, 0);
        }
        return new D3D11CapturedFrame(width, height, stride, data);
    }

    private static void ConvertPqRow(byte* input, byte* output, int width)
    {
        for (var x = 0; x < width; x++)
        {
            var pixel = (Half*)input + x * 4;
            var blue = PqLinear((float)pixel[0]);
            var green = PqLinear((float)pixel[1]);
            var red = PqLinear((float)pixel[2]);
            red /= 1 + red;
            green /= 1 + green;
            blue /= 1 + blue;
            var r709 = 1.660491f * red - 0.587641f * green - 0.07285f * blue;
            var g709 = -0.12455f * red + 1.1329f * green - 0.008349f * blue;
            var b709 = -0.018151f * red - 0.100579f * green + 1.11873f * blue;
            output[x * 4] = SdrByte(b709);
            output[x * 4 + 1] = SdrByte(g709);
            output[x * 4 + 2] = SdrByte(r709);
            output[x * 4 + 3] = (byte)Math.Clamp((int)Math.Round((float)pixel[3] * 255), 0, 255);
        }
    }

    private static float PqLinear(float value)
    {
        var p = MathF.Pow(MathF.Max(value, 0), 1 / 78.84375f);
        return MathF.Pow(MathF.Max(p - 0.8359375f, 0) / (18.8515625f - 18.6875f * p), 1 / 0.1593017578125f);
    }

    private static byte SdrByte(float value) =>
        (byte)Math.Clamp((int)MathF.Round(MathF.Pow(MathF.Max(value, 0), 1 / 2.2f) * 255), 0, 255);
}
