using System.Runtime.InteropServices;
using HomePlayer.Core;
using HomePlayer.Playback;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace HomePlayer.Playback.Tests;

public sealed unsafe class LibplaceboRendererTests
{
    [Fact]
    public void Native_layout_matches_the_360_abi()
    {
        Assert.Equal(48, Marshal.SizeOf<LibplaceboNative.PlD3D11Params>());
        Assert.Equal(24, Marshal.SizeOf<LibplaceboNative.PlD3D11WrapParams>());
        Assert.Equal(48, Marshal.SizeOf<LibplaceboNative.PlPlane>());
        Assert.Equal(156, Marshal.SizeOf<LibplaceboNative.PlColorSpace>());
        Assert.Equal(208, Marshal.SizeOf<LibplaceboNative.PlFilmGrainData>());
        Assert.Equal(728, Marshal.SizeOf<LibplaceboNative.PlFrame>());
        Assert.Equal(8, Marshal.OffsetOf<LibplaceboNative.PlFrame>(nameof(LibplaceboNative.PlFrame.Planes)).ToInt32());
        Assert.Equal(512, Marshal.OffsetOf<LibplaceboNative.PlFrame>(nameof(LibplaceboNative.PlFrame.FilmGrain)).ToInt32());
    }

    [Theory]
    [InlineData(VideoPixelFormat.Bgra32, LibplaceboQuality.Fast)]
    [InlineData(VideoPixelFormat.Bgra32, LibplaceboQuality.Balanced)]
    [InlineData(VideoPixelFormat.Bgra32, LibplaceboQuality.HighQuality)]
    [InlineData(VideoPixelFormat.Nv12, LibplaceboQuality.Balanced)]
    [InlineData(VideoPixelFormat.P010, LibplaceboQuality.Balanced)]
    public void Dynamic_frames_render_red_pixels(VideoPixelFormat format, LibplaceboQuality quality)
    {
        AssertSolidRed(format, quality, ResourceUsage.Dynamic, hdrOutput: false);
    }

    [Fact]
    public void Default_gpu_frame_renders_without_cpu_readback()
    {
        AssertSolidRed(VideoPixelFormat.Nv12, LibplaceboQuality.Fast, ResourceUsage.Default, hdrOutput: false);
    }

    [Theory]
    [InlineData(VideoPixelFormat.Nv12, true, false)]
    [InlineData(VideoPixelFormat.P010, true, false)]
    [InlineData(VideoPixelFormat.Bgra32, true, true)]
    [InlineData(VideoPixelFormat.Nv12, false, true)]
    public void Shared_planar_input_has_an_explicit_basic_fallback(VideoPixelFormat format, bool shared, bool supported)
    {
        Assert.Equal(supported, LibplaceboRenderer.SupportsInput(format, shared));
    }

    [Fact]
    public void Hdr_output_preserves_display_contract()
    {
        AssertSolidRed(VideoPixelFormat.Bgra32, LibplaceboQuality.Fast, ResourceUsage.Dynamic, hdrOutput: true);
    }

    private static void AssertSolidRed(VideoPixelFormat format, LibplaceboQuality quality, ResourceUsage usage, bool hdrOutput)
    {
        if (!LibplaceboRenderer.IsAvailable)
            Assert.Fail(LibplaceboRenderer.Version);
        var result = D3D11.D3D11CreateDevice(
            null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            new[] { FeatureLevel.Level_11_0 }, out ID3D11Device device, out ID3D11DeviceContext context);
        if (!result.Success)
            return;
        using var _ = device;
        using var __ = context;
        using var renderer = new LibplaceboRenderer(device, context);
        using var luma = CreateTexture(device, 2, 2, format switch
        {
            VideoPixelFormat.Bgra32 => Format.B8G8R8A8_UNorm,
            VideoPixelFormat.Nv12 => Format.R8_UNorm,
            _ => Format.R16_UNorm
        }, usage);
        using var chroma = format == VideoPixelFormat.Bgra32
            ? null
            : CreateTexture(device, 1, 1, format == VideoPixelFormat.Nv12 ? Format.R8G8_UNorm : Format.R16G16_UNorm, usage);
        using var target = device.CreateTexture2D(new Texture2DDescription
        {
            Width = 4,
            Height = 4,
            MipLevels = 1,
            ArraySize = 1,
            Format = hdrOutput ? Format.R16G16B16A16_Float : Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget
        });
        if (format == VideoPixelFormat.Bgra32)
        {
            Upload(context, luma, Enumerable.Range(0, 4).SelectMany(_ => new byte[] { 0, 0, 255, 255 }).ToArray(), 8);
        }
        else
        {
            var scale = format == VideoPixelFormat.P010 ? 256 : 1;
            var pitch = format == VideoPixelFormat.P010 ? 4 : 2;
            Upload(context, luma, Samples(scale, 76, 76, 76, 76), pitch);
            Upload(context, chroma!, Samples(scale, 85, 255), pitch);
        }
        var color = new VideoColorMetadata(
            VideoColorMatrix.Bt709, VideoColorRange.Full,
            VideoColorPrimaries.Bt709, VideoTransferFunction.Sdr);
        var geometry = VideoAspect.Quad(4, 4, 2, 2, AspectMode.Stretch, bottomUp: false);

        var rendered = renderer.Render(luma, chroma, 0, 0, format, false, color, target,
            2, 2, 4, 4, geometry, 1, 1, hdrOutput, quality);

        Assert.True(rendered);
        var captured = D3D11FrameCapture.CopyBgra(device, context, target, hdrOutput);
        Assert.Equal(4, captured.Width);
        Assert.Equal(4, captured.Height);
        for (var pixel = 0; pixel < 16; pixel++)
        {
            var b = captured.Data[pixel * 4];
            var g = captured.Data[pixel * 4 + 1];
            var r = captured.Data[pixel * 4 + 2];
            Assert.True(b <= 30 && g <= 30 && r >= (hdrOutput ? 100 : 220),
                $"{format} pixel {pixel}: B={b} G={g} R={r}");
            Assert.Equal(255, captured.Data[pixel * 4 + 3]);
        }
    }

    private static ID3D11Texture2D CreateTexture(ID3D11Device device, int width, int height, Format format, ResourceUsage usage) =>
        device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = usage,
            BindFlags = BindFlags.ShaderResource,
            CPUAccessFlags = usage == ResourceUsage.Dynamic ? CpuAccessFlags.Write : CpuAccessFlags.None
        });

    private static byte[] Samples(int scale, params int[] values)
    {
        var bytes = new byte[values.Length * (scale == 1 ? 1 : 2)];
        for (var index = 0; index < values.Length; index++)
        {
            var sample = values[index] * scale;
            if (scale == 1)
                bytes[index] = (byte)sample;
            else
                BitConverter.TryWriteBytes(bytes.AsSpan(index * 2, 2), (ushort)sample);
        }
        return bytes;
    }

    private static void Upload(ID3D11DeviceContext context, ID3D11Texture2D texture, byte[] bytes, int pitch, uint subresource = 0)
    {
        if (texture.Description.Usage == ResourceUsage.Default)
        {
            context.UpdateSubresource(bytes, texture, subresource, (uint)pitch, (uint)bytes.Length);
            return;
        }
        var mapped = context.Map(texture, subresource, MapMode.WriteDiscard);
        fixed (byte* source = bytes)
        {
            for (var row = 0; row < texture.Description.Height; row++)
                Buffer.MemoryCopy(source + row * pitch, (byte*)mapped.DataPointer + row * mapped.RowPitch, pitch, pitch);
        }
        context.Unmap(texture, subresource);
    }
}
