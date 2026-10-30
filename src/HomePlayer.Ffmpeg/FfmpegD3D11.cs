using System.Diagnostics;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using HomePlayer.Core;

namespace HomePlayer.Ffmpeg;

public sealed unsafe partial class FfmpegPlayer
{
    private static readonly AVCodecContext_get_format GetFormatCallback = OnGetFormat;
    private static readonly AVD3D11VADeviceContext_lock LockCallback = OnLock;
    private static readonly AVD3D11VADeviceContext_unlock UnlockCallback = OnUnlock;

    private AVBufferRef* _hwDeviceRef;
    private GCHandle _hardwareHandle;
    private bool _usingD3D11;

    public bool EnableD3D11Va { get; set; } = true;
    public D3D11SharedDevice? D3D11Device { get; set; }

    private bool TryConfigureD3D11(AVCodecContext* context)
    {
        _usingD3D11 = false;
        if (!EnableD3D11Va || D3D11Device == null)
            return false;
        try
        {
            if (!_hardwareHandle.IsAllocated)
                _hardwareHandle = GCHandle.Alloc(this);
            if (_hwDeviceRef == null)
            {
                _hwDeviceRef = ffmpeg.av_hwdevice_ctx_alloc(AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA);
                if (_hwDeviceRef == null)
                    return false;
                var hardware = (AVD3D11VADeviceContext*)((AVHWDeviceContext*)_hwDeviceRef->data)->hwctx;
                ComAddRef(D3D11Device.Device);
                ComAddRef(D3D11Device.DeviceContext);
                hardware->device = (ID3D11Device*)D3D11Device.Device;
                hardware->device_context = (ID3D11DeviceContext*)D3D11Device.DeviceContext;
                hardware->BindFlags = 0x208;
                hardware->@lock = new AVD3D11VADeviceContext_lock_func { Pointer = Marshal.GetFunctionPointerForDelegate(LockCallback) };
                hardware->unlock = new AVD3D11VADeviceContext_unlock_func { Pointer = Marshal.GetFunctionPointerForDelegate(UnlockCallback) };
                hardware->lock_ctx = (void*)GCHandle.ToIntPtr(_hardwareHandle);
                FfmpegError.Check(ffmpeg.av_hwdevice_ctx_init(_hwDeviceRef), "初始化 D3D11VA 设备");
            }
            context->opaque = (void*)GCHandle.ToIntPtr(_hardwareHandle);
            context->hw_device_ctx = ffmpeg.av_buffer_ref(_hwDeviceRef);
            context->get_format = GetFormatCallback;
            return true;
        }
        catch
        {
            ReleaseD3D11Device();
            return false;
        }
    }

    private static AVPixelFormat OnGetFormat(AVCodecContext* context, AVPixelFormat* formats)
    {
        var player = (FfmpegPlayer?)GCHandle.FromIntPtr((IntPtr)context->opaque).Target;
        if (player == null)
            return ffmpeg.avcodec_default_get_format(context, formats);
        for (var i = 0; formats[i] != AVPixelFormat.AV_PIX_FMT_NONE; i++)
        {
            if (formats[i] == AVPixelFormat.AV_PIX_FMT_D3D11 && player.TryInitializeD3D11Frames(context))
            {
                player._usingD3D11 = true;
                return AVPixelFormat.AV_PIX_FMT_D3D11;
            }
        }
        player._usingD3D11 = false;
        return ffmpeg.avcodec_default_get_format(context, formats);
    }

    private bool TryInitializeD3D11Frames(AVCodecContext* context)
    {
        AVBufferRef* frames = null;
        var result = ffmpeg.avcodec_get_hw_frames_parameters(context, _hwDeviceRef, AVPixelFormat.AV_PIX_FMT_D3D11, &frames);
        if (result < 0 || frames == null)
            return false;
        result = ffmpeg.av_hwframe_ctx_init(frames);
        if (result < 0)
        {
            ffmpeg.av_buffer_unref(&frames);
            return false;
        }
        if (context->hw_frames_ctx != null)
            ffmpeg.av_buffer_unref(&context->hw_frames_ctx);
        context->hw_frames_ctx = frames;
        return true;
    }

    private FrameLease ToD3D11(AVFrame* source, long pts, long duration, long epoch)
    {
        var softwareFormat = source->hw_frames_ctx == null
            ? AVPixelFormat.AV_PIX_FMT_NV12
            : ((AVHWFramesContext*)source->hw_frames_ctx->data)->sw_format;
        var tenBit = softwareFormat == AVPixelFormat.AV_PIX_FMT_P010LE;
        if (source->data[0] == null || (!tenBit && softwareFormat != AVPixelFormat.AV_PIX_FMT_NV12))
            throw new InvalidOperationException("D3D11VA 输出了不支持的纹理格式。");
        var leaseFrame = ffmpeg.av_frame_alloc();
        if (leaseFrame == null)
            throw new InvalidOperationException("无法保留 D3D11VA 帧。");
        var leasePointer = (IntPtr)leaseFrame;
        try
        {
            FfmpegError.Check(ffmpeg.av_frame_ref(leaseFrame, source), "保留 D3D11VA 帧");
            var seek = ActiveSeek(epoch);
            Volatile.Write(ref _outputPixelFormat, tenBit ? "p010" : "nv12");
            Volatile.Write(ref _outputZeroCopyLevel, "Z3");
            return new D3D11FrameLease
            {
                Width = _width,
                Height = _height,
                PixelFormat = tenBit ? VideoPixelFormat.P010 : VideoPixelFormat.Nv12,
                Color = ColorOf(source),
                Geometry = GeometryOf(source),
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
                DeviceGeneration = D3D11Device?.DeviceGeneration ?? 0,
                ZeroCopyLevel = ZeroCopyLevel.Z3,
                Resource = new D3D11FrameResource(
                    (nint)source->data[0],
                    (int)(nint)source->data[1],
                    D3D11Device?.AdapterLuid ?? 0),
                ReleaseBuffer = () => ReleaseD3D11Frame(leasePointer)
            };
        }
        catch
        {
            ReleaseD3D11Frame(leasePointer);
            throw;
        }
    }

    private static void ReleaseD3D11Frame(IntPtr pointer)
    {
        var frame = (AVFrame*)pointer;
        ffmpeg.av_frame_free(&frame);
    }

    private void ReleaseD3D11Device()
    {
        _usingD3D11 = false;
        var device = _hwDeviceRef;
        if (device != null)
            ffmpeg.av_buffer_unref(&device);
        _hwDeviceRef = null;
        if (_hardwareHandle.IsAllocated)
            _hardwareHandle.Free();
    }

    private static void OnLock(void* context)
    {
        if (GCHandle.FromIntPtr((IntPtr)context).Target is FfmpegPlayer { D3D11Device: not null } player)
            Monitor.Enter(player.D3D11Device.SyncRoot);
    }

    private static void OnUnlock(void* context)
    {
        if (GCHandle.FromIntPtr((IntPtr)context).Target is FfmpegPlayer { D3D11Device: not null } player)
            Monitor.Exit(player.D3D11Device.SyncRoot);
    }

    private static void ComAddRef(nint pointer)
    {
        if (pointer == 0)
            return;
        var table = Marshal.ReadIntPtr(pointer);
        var addRef = Marshal.GetDelegateForFunctionPointer<ComAddRefDelegate>(Marshal.ReadIntPtr(table, IntPtr.Size));
        addRef(pointer);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint ComAddRefDelegate(nint self);
}
