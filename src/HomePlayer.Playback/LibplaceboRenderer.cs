using HomePlayer.Core;
using Vortice.Direct3D11;
using Vortice.DXGI;
using PlFrame = HomePlayer.Playback.LibplaceboNative.PlFrame;
using PlPlane = HomePlayer.Playback.LibplaceboNative.PlPlane;
using PlColorRepr = HomePlayer.Playback.LibplaceboNative.PlColorRepr;
using PlColorSpace = HomePlayer.Playback.LibplaceboNative.PlColorSpace;
using PlRect2Df = HomePlayer.Playback.LibplaceboNative.PlRect2Df;
using PlHdrMetadata = HomePlayer.Playback.LibplaceboNative.PlHdrMetadata;
using PlHdrBezier = HomePlayer.Playback.LibplaceboNative.PlHdrBezier;
using PlBitEncoding = HomePlayer.Playback.LibplaceboNative.PlBitEncoding;
using PlRawPrimaries = HomePlayer.Playback.LibplaceboNative.PlRawPrimaries;
using PlCieXy = HomePlayer.Playback.LibplaceboNative.PlCieXy;

namespace HomePlayer.Playback;

public sealed unsafe class LibplaceboRenderer : IDisposable
{
    private readonly ID3D11Device _d3dDevice;
    private readonly ID3D11DeviceContext _context;
    private nint _device;
    private nint _gpu;
    private nint _renderer;

    public LibplaceboRenderer(ID3D11Device device, ID3D11DeviceContext context)
    {
        _d3dDevice = device;
        _context = context;
        _device = LibplaceboNative.CreateDevice(device);
        if (_device == 0)
            throw new InvalidOperationException("libplacebo 无法包装 D3D11 设备。");
        try
        {
            _gpu = System.Runtime.InteropServices.Marshal.ReadIntPtr(_device);
            if (_gpu == 0)
                throw new InvalidOperationException("libplacebo 未返回有效 GPU 句柄。");
            _renderer = LibplaceboNative.CreateRenderer(_gpu);
            if (_renderer == 0)
                throw new InvalidOperationException("libplacebo 无法创建渲染器。");
        }
        catch
        {
            LibplaceboNative.DestroyDevice(ref _device);
            throw;
        }
    }

    public static bool IsAvailable => LibplaceboNative.IsAvailable;

    public static string Version => LibplaceboNative.Version;

    public static bool SupportsInput(VideoPixelFormat pixelFormat, bool sharedPlanes) =>
        !sharedPlanes || pixelFormat == VideoPixelFormat.Bgra32;

    public bool Render(
        ID3D11Texture2D source,
        ID3D11Texture2D? chroma,
        int sourceSubresource,
        int chromaSubresource,
        VideoPixelFormat pixelFormat,
        bool bottomUp,
        VideoColorMetadata color,
        ID3D11Texture2D target,
        int sourceWidth,
        int sourceHeight,
        int targetWidth,
        int targetHeight,
        VideoQuad geometry,
        float compositionScaleX,
        float compositionScaleY,
        bool hdrOutput,
        LibplaceboQuality quality)
    {
        if (!SupportsInput(pixelFormat, chroma != null && ReferenceEquals(source, chroma)))
            return false;
        nint input = 0;
        nint inputChroma = 0;
        nint output = 0;
        ID3D11Texture2D? defaultSource = null;
        ID3D11Texture2D? defaultChroma = null;
        try
        {
            defaultSource = DefaultInput(source);
            defaultChroma = chroma == null ? null : DefaultInput(chroma);
            source = defaultSource ?? source;
            chroma = defaultChroma ?? chroma;
            var sourceDescription = source.Description;
            var inputFormat = pixelFormat switch
            {
                VideoPixelFormat.Bgra32 => Format.B8G8R8A8_UNorm,
                VideoPixelFormat.Nv12 => Format.R8_UNorm,
                VideoPixelFormat.P010 => Format.R16_UNorm,
                _ => throw new NotSupportedException($"libplacebo 输入格式不受支持：{pixelFormat}。")
            };
            input = LibplaceboNative.Wrap(_gpu, source, inputFormat, (int)sourceDescription.Width, (int)sourceDescription.Height, sourceSubresource);
            if (input == 0)
                return false;
            if (chroma != null)
            {
                var chromaDescription = chroma.Description;
                var sharedPlanes = ReferenceEquals(source, chroma);
                var chromaWidth = (int)(sharedPlanes ? (chromaDescription.Width + 1) / 2 : chromaDescription.Width);
                var chromaHeight = (int)(sharedPlanes ? (chromaDescription.Height + 1) / 2 : chromaDescription.Height);
                inputChroma = LibplaceboNative.Wrap(_gpu, chroma, pixelFormat == VideoPixelFormat.Nv12 ? Format.R8G8_UNorm : Format.R16G16_UNorm,
                    chromaWidth, chromaHeight, chromaSubresource);
                if (inputChroma == 0)
                    return false;
            }
            output = LibplaceboNative.Wrap(_gpu, target, hdrOutput ? Format.R16G16B16A16_Float : Format.B8G8R8A8_UNorm,
                targetWidth, targetHeight, 0);
            if (output == 0)
                return false;

            var image = NewFrame();
            image.NumPlanes = chroma == null ? 1 : 2;
            image.Planes[0] = pixelFormat == VideoPixelFormat.Bgra32
                ? Plane(input, 4, bottomUp, 2, 1, 0, 3)
                : Plane(input, 1, bottomUp, 0);
            if (chroma != null)
                image.Planes[1] = Plane(inputChroma, 2, bottomUp, 1, 2);
            image.Repr = ColorRepresentation(pixelFormat, color);
            image.Color = SourceColorSpace(color);
            image.Crop = Rect(
                geometry.UvLeft * sourceWidth,
                geometry.UvTop * sourceHeight,
                geometry.UvRight * sourceWidth,
                geometry.UvBottom * sourceHeight);
            if (chroma != null && color.ChromaLocation != VideoChromaLocation.Unknown)
                LibplaceboNative.SetChromaLocation(image, color.ChromaLocation);

            var outputFrame = NewFrame();
            outputFrame.NumPlanes = 1;
            outputFrame.Planes[0] = hdrOutput
                ? Plane(output, 4, false, 2, 1, 0, 3)
                : Plane(output, 4, false, 0, 1, 2, 3);
            outputFrame.Repr = RgbRepresentation(hdrOutput);
            outputFrame.Color = EmptyColorSpace();
            outputFrame.Color.Primaries = hdrOutput ? 6 : 3;
            outputFrame.Color.Transfer = hdrOutput ? 11 : 2;
            outputFrame.Crop = Rect(
                (geometry.Left + 1) * 0.5f * targetWidth * compositionScaleX,
                (1 - geometry.Top) * 0.5f * targetHeight * compositionScaleY,
                (geometry.Right + 1) * 0.5f * targetWidth * compositionScaleX,
                (1 - geometry.Bottom) * 0.5f * targetHeight * compositionScaleY);
            return LibplaceboNative.Render(_renderer, image, outputFrame, quality);
        }
        finally
        {
            if (output != 0)
                LibplaceboNative.DestroyTexture(_gpu, ref output);
            if (inputChroma != 0)
                LibplaceboNative.DestroyTexture(_gpu, ref inputChroma);
            if (input != 0)
                LibplaceboNative.DestroyTexture(_gpu, ref input);
            defaultChroma?.Dispose();
            defaultSource?.Dispose();
        }
    }

    private ID3D11Texture2D? DefaultInput(ID3D11Texture2D source)
    {
        if (source.Description.Usage != ResourceUsage.Dynamic)
            return null;
        var description = source.Description;
        description.Usage = ResourceUsage.Default;
        description.CPUAccessFlags = CpuAccessFlags.None;
        description.BindFlags = BindFlags.ShaderResource;
        var copy = _d3dDevice.CreateTexture2D(description);
        _context.CopyResource(copy, source);
        return copy;
    }

    public void Dispose()
    {
        if (_renderer != 0)
            LibplaceboNative.DestroyRenderer(ref _renderer);
        if (_device != 0)
            LibplaceboNative.DestroyDevice(ref _device);
    }

    private static PlFrame NewFrame() => new()
    {
        Color = EmptyColorSpace()
    };

    private static PlColorSpace EmptyColorSpace() => new()
    {
        Hdr = new PlHdrMetadata
        {
            SceneMax = new float[3],
            Ootf = new PlHdrBezier { Anchors = new float[15] }
        }
    };

    private static PlPlane Plane(nint texture, int components, bool flipped, params int[] mapping) => new()
    {
        Texture = texture,
        Components = components,
        Flipped = flipped,
        ComponentMapping = [.. mapping, .. Enumerable.Repeat(-1, 4 - mapping.Length)]
    };

    private static PlColorRepr ColorRepresentation(VideoPixelFormat format, VideoColorMetadata color)
    {
        var representation = new PlColorRepr
        {
            System = format == VideoPixelFormat.Bgra32 ? 12 : color.Matrix switch
            {
                VideoColorMatrix.Bt601 => 1,
                VideoColorMatrix.Bt709 => 2,
                VideoColorMatrix.Bt2020 => 4,
                _ => 0
            },
            Levels = color.Range switch
            {
                VideoColorRange.Limited => 1,
                VideoColorRange.Full => 2,
                _ => 0
            },
            Alpha = 3,
            Bits = new PlBitEncoding
            {
                SampleDepth = format == VideoPixelFormat.P010 ? 16 : 8,
                ColorDepth = format == VideoPixelFormat.P010 ? 10 : 8,
                BitShift = format == VideoPixelFormat.P010 ? 6 : 0
            }
        };
        return representation;
    }

    private static PlColorRepr RgbRepresentation(bool hdrOutput) => new()
    {
        System = 12,
        Levels = 2,
        Alpha = 3,
        Bits = new PlBitEncoding { SampleDepth = hdrOutput ? 16 : 8, ColorDepth = hdrOutput ? 16 : 8 }
    };

    private static PlColorSpace SourceColorSpace(VideoColorMetadata color)
    {
        var result = EmptyColorSpace();
        result.Primaries = color.Primaries switch
        {
            VideoColorPrimaries.Bt709 => 3,
            VideoColorPrimaries.Bt2020 => 6,
            VideoColorPrimaries.DciP3 => 11,
            _ => 0
        };
        result.Transfer = color.Transfer switch
        {
            VideoTransferFunction.Sdr => 2,
            VideoTransferFunction.Pq => 11,
            VideoTransferFunction.Hlg => 12,
            _ => 0
        };
        if (color.Hdr10 is not { } hdr)
            return result;
        result.Hdr.Primaries = new PlRawPrimaries
        {
            Red = new PlCieXy { X = hdr.RedPrimaryX / 50000f, Y = hdr.RedPrimaryY / 50000f },
            Green = new PlCieXy { X = hdr.GreenPrimaryX / 50000f, Y = hdr.GreenPrimaryY / 50000f },
            Blue = new PlCieXy { X = hdr.BluePrimaryX / 50000f, Y = hdr.BluePrimaryY / 50000f },
            White = new PlCieXy { X = hdr.WhitePointX / 50000f, Y = hdr.WhitePointY / 50000f }
        };
        result.Hdr.MinLuma = hdr.MinMasteringLuminance / 10000f;
        result.Hdr.MaxLuma = hdr.MaxMasteringLuminance;
        result.Hdr.MaxCll = hdr.MaxContentLightLevel;
        result.Hdr.MaxFall = hdr.MaxFrameAverageLightLevel;
        return result;
    }

    private static PlRect2Df Rect(float x0, float y0, float imageWidth, float imageHeight) => new()
    {
        X0 = x0,
        Y0 = y0,
        X1 = imageWidth,
        Y1 = imageHeight
    };
}
