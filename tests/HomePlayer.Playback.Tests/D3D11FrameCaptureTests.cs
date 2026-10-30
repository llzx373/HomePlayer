using HomePlayer.Playback;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace HomePlayer.Playback.Tests;

public sealed unsafe class D3D11FrameCaptureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Staging_copy_returns_sdr_bgra_pixels(bool hdr)
    {
        var result = D3D11.D3D11CreateDevice(
            null,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            new[] { FeatureLevel.Level_11_0 },
            out ID3D11Device device,
            out ID3D11DeviceContext context);
        if (!result.Success)
            return;
        using var _ = device;
        using var __ = context;
        using var source = device.CreateTexture2D(new Texture2DDescription
        {
            Width = 2,
            Height = 1,
            MipLevels = 1,
            ArraySize = 1,
            Format = hdr ? Format.R16G16B16A16_Float : Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.Write
        });
        var mapped = context.Map(source, 0, MapMode.WriteDiscard);
        var input = (byte*)mapped.DataPointer;
        if (hdr)
        {
            var pixels = (Half*)input;
            pixels[0] = pixels[1] = pixels[2] = (Half)0;
            pixels[3] = (Half)1;
            pixels[4] = pixels[5] = pixels[6] = pixels[7] = (Half)1;
        }
        else
        {
            var expected = new byte[] { 10, 20, 30, 255, 40, 50, 60, 128 };
            for (var i = 0; i < expected.Length; i++)
                input[i] = expected[i];
        }
        context.Unmap(source, 0);

        var captured = D3D11FrameCapture.CopyBgra(device, context, source, hdr);

        Assert.Equal(2, captured.Width);
        Assert.Equal(1, captured.Height);
        Assert.Equal(8, captured.Stride);
        Assert.Equal(8, captured.Data.Length);
        if (hdr)
        {
            Assert.Equal([0, 0, 0, 255], captured.Data.Take(4).ToArray());
            Assert.All(captured.Data.Skip(4).Take(3), value => Assert.InRange(value, 150, 255));
            Assert.Equal(255, captured.Data[7]);
        }
        else
        {
            Assert.Equal([10, 20, 30, 255, 40, 50, 60, 128], captured.Data);
        }
    }
}
