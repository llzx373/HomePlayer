using System.Diagnostics;
using FFmpeg.AutoGen;
using HomePlayer.Core;

namespace HomePlayer.Ffmpeg;

public sealed unsafe partial class FfmpegPlayer
{
    private string _outputPixelFormat = "unknown";
    private string _outputZeroCopyLevel = "Unknown";
    private VideoFramePlane[]? _yuvPlanes;
    private int _yuvPlanesWidth;
    private int _yuvPlanesHeight;
    private AVPixelFormat _yuvPlanesFormat = AVPixelFormat.AV_PIX_FMT_NONE;
    private AVPixelFormat _decodedSourceFormat = AVPixelFormat.AV_PIX_FMT_NONE;

    public bool EnableDirectYuv { get; set; } = true;

    private void EnsureVideoFrameFormat(AVFrame* source)
    {
        if (source->width == _width && source->height == _height && source->format == (int)_decodedSourceFormat)
            return;
        _width = source->width;
        _height = source->height;
        _decodedSourceFormat = (AVPixelFormat)source->format;
        _frameBytes = 0;
        _yuvPlanes = null;
        if (_scaler != null)
            ffmpeg.sws_freeContext(_scaler);
        _scaler = null;
        if (_decodedSourceFormat != AVPixelFormat.AV_PIX_FMT_D3D11)
        {
            _scaler = ffmpeg.sws_getContext(
                _width, _height, _decodedSourceFormat,
                _width, _height, AVPixelFormat.AV_PIX_FMT_BGRA,
                (int)SwsFlags.SWS_BILINEAR, null, null, null);
            if (_scaler == null)
                throw new InvalidOperationException("无法重建画面转换器。");
        }
    }

    private FrameLease ToFrame(AVFrame* source, long pts, long duration, long epoch)
    {
        if (source->format == (int)AVPixelFormat.AV_PIX_FMT_D3D11)
            return ToD3D11(source, pts, duration, epoch);
        if (EnableDirectYuv && source->width == _width && source->height == _height
            && _width >= 2 && _height >= 2 && _width % 2 == 0 && _height % 2 == 0
            && source->format is (int)AVPixelFormat.AV_PIX_FMT_NV12 or (int)AVPixelFormat.AV_PIX_FMT_P010LE
                or (int)AVPixelFormat.AV_PIX_FMT_YUV420P or (int)AVPixelFormat.AV_PIX_FMT_YUV420P10LE)
            return ToYuv(source, pts, duration, epoch);
        return ToBgra(source, pts, duration, epoch);
    }

    private FrameLease ToYuv(AVFrame* source, long pts, long duration, long epoch)
    {
        var format = (AVPixelFormat)source->format;
        var planar = format is AVPixelFormat.AV_PIX_FMT_YUV420P or AVPixelFormat.AV_PIX_FMT_YUV420P10LE;
        var alignTenBit = format == AVPixelFormat.AV_PIX_FMT_YUV420P10LE;
        var tenBit = format is AVPixelFormat.AV_PIX_FMT_P010LE or AVPixelFormat.AV_PIX_FMT_YUV420P10LE;
        var bytesPerSample = tenBit ? 2 : 1;
        var stride = checked(_width * bytesPerSample);
        var sourceChromaStride = planar ? stride / 2 : stride;
        var lumaBytes = checked(stride * _height);
        var chromaBytes = checked(stride * (_height / 2));
        var frameBytes = checked(lumaBytes + chromaBytes);
        if (source->data[0] == null || source->data[1] == null || (planar && source->data[2] == null)
            || Math.Abs(source->linesize[0]) < stride || Math.Abs(source->linesize[1]) < sourceChromaStride
            || (planar && Math.Abs(source->linesize[2]) < sourceChromaStride))
            return ToBgra(source, pts, duration, epoch);

        var buffer = _framePool.Rent(frameBytes);
        try
        {
            fixed (byte* pointer = buffer)
            {
                if (alignTenBit)
                    CopyAvPlaneToP010(source->data[0], source->linesize[0], _width, _height, (ushort*)pointer);
                else
                    CopyAvPlane(source->data[0], source->linesize[0], stride, _height, pointer);
                if (planar)
                    InterleaveChroma(source, stride, bytesPerSample, alignTenBit, pointer + lumaBytes);
                else
                    CopyAvPlane(source->data[1], source->linesize[1], stride, _height / 2, pointer + lumaBytes);
            }
            var planes = YuvPlanes(format, stride, lumaBytes);
            var seek = ActiveSeek(epoch);
            Volatile.Write(ref _outputPixelFormat, tenBit ? "p010" : "nv12");
            Volatile.Write(ref _outputZeroCopyLevel, "Z2");
            return new CpuFrameLease
            {
                Width = _width,
                Height = _height,
                PixelFormat = tenBit ? VideoPixelFormat.P010 : VideoPixelFormat.Nv12,
                Color = ColorOf(source),
                Geometry = GeometryOf(source),
                Data = buffer,
                Planes = planes,
                PresentationTimestamp = pts,
                Duration = duration,
                TimeBase = new MediaTimeBase(_videoTimeBase.num, _videoTimeBase.den),
                SessionId = SessionId,
                Epoch = epoch,
                TimelineId = TimelineId,
                FrameSequence = Interlocked.Increment(ref _frameSequence),
                DecodedTimestamp = Stopwatch.GetTimestamp(),
                OpenedTimestamp = _openedTimestamp,
                SeekStartedTimestamp = seek?.StartedTimestamp ?? 0,
                SeekCommandId = seek?.CommandId > 0 ? seek.CommandId : null,
                ZeroCopyLevel = ZeroCopyLevel.Z2,
                ReleaseBuffer = () => _framePool.Return(buffer)
            };
        }
        catch
        {
            _framePool.Return(buffer);
            throw;
        }
    }

    private VideoFramePlane[] YuvPlanes(AVPixelFormat format, int stride, int lumaBytes)
    {
        if (_yuvPlanes != null && _yuvPlanesWidth == _width && _yuvPlanesHeight == _height && _yuvPlanesFormat == format)
            return _yuvPlanes;
        _yuvPlanes = [new VideoFramePlane(0, stride), new VideoFramePlane(lumaBytes, stride)];
        _yuvPlanesWidth = _width;
        _yuvPlanesHeight = _height;
        _yuvPlanesFormat = format;
        return _yuvPlanes;
    }

    private void MarkBgraPipeline()
    {
        Volatile.Write(ref _outputPixelFormat, "bgra");
        Volatile.Write(ref _outputZeroCopyLevel, "Z1");
    }

    private string VideoFilterDescription() =>
        Volatile.Read(ref _outputZeroCopyLevel) switch
        {
            "Z3" => "FFmpeg D3D11VA → same-device texture",
            "Z2" => "FFmpeg video decoder → direct YUV planes",
            _ => "FFmpeg video decoder → swscale BGRA"
        };

    private static void CopyAvPlane(byte* source, int sourceStride, int rowBytes, int rows, byte* destination)
    {
        for (var y = 0; y < rows; y++)
            Buffer.MemoryCopy(source + (long)y * sourceStride, destination + (long)y * rowBytes, rowBytes, rowBytes);
    }

    private static void CopyAvPlaneToP010(byte* source, int sourceStride, int width, int rows, ushort* destination)
    {
        for (var y = 0; y < rows; y++)
        {
            var input = (ushort*)(source + (long)y * sourceStride);
            var output = destination + (long)y * width;
            for (var x = 0; x < width; x++)
                output[x] = (ushort)(input[x] << 6);
        }
    }

    private void InterleaveChroma(AVFrame* source, int destinationStride, int bytesPerSample, bool alignTenBit, byte* destination)
    {
        var width = _width / 2;
        var rows = _height / 2;
        for (var y = 0; y < rows; y++)
        {
            var u = source->data[1] + (long)y * source->linesize[1];
            var v = source->data[2] + (long)y * source->linesize[2];
            var output = destination + (long)y * destinationStride;
            if (bytesPerSample == 1)
            {
                for (var x = 0; x < width; x++)
                {
                    output[x * 2] = u[x];
                    output[x * 2 + 1] = v[x];
                }
            }
            else
            {
                var inputU = (ushort*)u;
                var inputV = (ushort*)v;
                var output16 = (ushort*)output;
                for (var x = 0; x < width; x++)
                {
                    output16[x * 2] = alignTenBit ? (ushort)(inputU[x] << 6) : inputU[x];
                    output16[x * 2 + 1] = alignTenBit ? (ushort)(inputV[x] << 6) : inputV[x];
                }
            }
        }
    }

    private static VideoFrameGeometry GeometryOf(AVFrame* source)
    {
        var sar = source->sample_aspect_ratio;
        return new VideoFrameGeometry(
            sar.num > 0 && sar.den > 0 ? sar.num : 1,
            sar.num > 0 && sar.den > 0 ? sar.den : 1);
    }

    private static VideoColorMetadata ColorOf(AVFrame* source)
    {
        var matrix = source->colorspace switch
        {
            AVColorSpace.AVCOL_SPC_BT709 => VideoColorMatrix.Bt709,
            AVColorSpace.AVCOL_SPC_BT2020_NCL or AVColorSpace.AVCOL_SPC_BT2020_CL => VideoColorMatrix.Bt2020,
            AVColorSpace.AVCOL_SPC_BT470BG or AVColorSpace.AVCOL_SPC_SMPTE170M => VideoColorMatrix.Bt601,
            _ => VideoColorMatrix.Unknown
        };
        var range = source->color_range switch
        {
            AVColorRange.AVCOL_RANGE_JPEG => VideoColorRange.Full,
            AVColorRange.AVCOL_RANGE_MPEG => VideoColorRange.Limited,
            _ => VideoColorRange.Unknown
        };
        var primaries = source->color_primaries switch
        {
            AVColorPrimaries.AVCOL_PRI_BT709 => VideoColorPrimaries.Bt709,
            AVColorPrimaries.AVCOL_PRI_BT2020 => VideoColorPrimaries.Bt2020,
            AVColorPrimaries.AVCOL_PRI_SMPTE432 => VideoColorPrimaries.DciP3,
            _ => VideoColorPrimaries.Unknown
        };
        var transfer = source->color_trc switch
        {
            AVColorTransferCharacteristic.AVCOL_TRC_SMPTE2084 => VideoTransferFunction.Pq,
            AVColorTransferCharacteristic.AVCOL_TRC_ARIB_STD_B67 => VideoTransferFunction.Hlg,
            AVColorTransferCharacteristic.AVCOL_TRC_BT709 or AVColorTransferCharacteristic.AVCOL_TRC_IEC61966_2_1 => VideoTransferFunction.Sdr,
            _ => VideoTransferFunction.Unknown
        };
        var chroma = source->chroma_location switch
        {
            AVChromaLocation.AVCHROMA_LOC_LEFT => VideoChromaLocation.Left,
            AVChromaLocation.AVCHROMA_LOC_CENTER => VideoChromaLocation.Center,
            AVChromaLocation.AVCHROMA_LOC_TOPLEFT => VideoChromaLocation.TopLeft,
            _ => VideoChromaLocation.Unknown
        };
        return new VideoColorMetadata(matrix, range, primaries, transfer, chroma);
    }
}
