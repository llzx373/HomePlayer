using System.Runtime.InteropServices;
using HomePlayer.Core;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace HomePlayer.Playback;

public sealed class LavVideoOutputException(string message, Exception innerException) : Exception(message, innerException);

[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
public sealed class D3D11SinkFilter : BgraSinkFilter
{
    public D3D11SinkFilter(D3D11SharedDevice? outputDevice = null) => _pin = new D3D11SinkPin(this, outputDevice);

    public override string PixelFormat => _pin is D3D11SinkPin pin ? pin.PixelFormat : "Unknown";
    public nint DecoderDevice => ((D3D11SinkPin)_pin).Device;
    public nint DecoderContext => ((D3D11SinkPin)_pin).Context;
    public long SharedTextureOpenCount => ((D3D11SinkPin)_pin).SharedTextureOpenCount;
    public void ReleaseResources() => ((D3D11SinkPin)_pin).ReleaseResources();
}

[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
public sealed class D3D11SinkPin : BgraSinkPin, ID3D11DecoderConfiguration
{
    private readonly D3D11SharedDevice? _outputDevice;
    private readonly FrameBufferPool _fallbackPool = new(4);
    private VideoPixelFormat _pixelFormat = VideoPixelFormat.Nv12;
    private nint _device;
    private nint _context;
    private nint _decoderMutex;
    private ID3D11Fence? _decodeFence;
    private nint _sharedDecodeFence;
    private ulong _decodeFenceValue;
    private long _adapterLuid;
    private long _frameSequence;
    private long _droppedSamples;
    private nint _cachedPresenterTexture;
    private nint _cachedSourceTexture;
    private nint _cachedSharedHandle;
    private long _cachedDeviceGeneration = -1;
    private long _sharedTextureOpenCount;

    public D3D11SinkPin(BgraSinkFilter filter, D3D11SharedDevice? outputDevice = null) : base(filter)
    {
        _outputDevice = outputDevice;
    }

    public string PixelFormat => _pixelFormat == VideoPixelFormat.P010 ? "P010" : "NV12";
    public nint Device => _device;
    public nint Context => _context;
    public long SharedTextureOpenCount => Interlocked.Read(ref _sharedTextureOpenCount);

    public override int QueryAccept(IntPtr mediaType)
    {
        if (mediaType == 0)
            return Hr.Pointer;
        var type = Marshal.PtrToStructure<AmMediaType>(mediaType);
        if (type.MajorType != DsGuid.Video || (type.SubType != DsGuid.Nv12 && type.SubType != DsGuid.P010))
            return Hr.TypeNotAccepted;
        _pixelFormat = type.SubType == DsGuid.P010 ? VideoPixelFormat.P010 : VideoPixelFormat.Nv12;
        return Hr.Ok;
    }

    public int ActivateD3D11Decoding(nint device, nint context, nint mutex, uint flags)
    {
        using var deviceWrapper = new ID3D11Device(device);
        deviceWrapper.AddRef();
        using var dxgi = deviceWrapper.QueryInterface<IDXGIDevice>();
        using var adapter = dxgi.GetAdapter();
        _adapterLuid = ToInt64(adapter.Description.Luid);
        if (_outputDevice != null && _adapterLuid != _outputDevice.AdapterLuid)
            return Hr.Fail;
        var previousDevice = _device;
        var previousContext = _context;
        ReleaseDecodeFence();
        _device = AddRef(device);
        _context = AddRef(context);
        _decoderMutex = mutex;
        if (_outputDevice != null && device != _outputDevice.Device)
        {
            using var device5 = deviceWrapper.QueryInterface<ID3D11Device5>();
            _decodeFence = device5.CreateFence(0, FenceFlags.Shared);
            _sharedDecodeFence = _decodeFence.CreateSharedHandle(null, null!);
        }
        if (previousDevice != 0)
            Marshal.Release(previousDevice);
        if (previousContext != 0)
            Marshal.Release(previousContext);
        PlaybackTelemetry.Trace("LavDecoderDeviceActivated", Guid.Empty, 0, data: new Dictionary<string, object?>
        {
            ["adapterLuid"] = _adapterLuid,
            ["outputDeviceGeneration"] = _outputDevice?.DeviceGeneration ?? 0,
            ["crossDeviceFence"] = _sharedDecodeFence != 0
        });
        return Hr.Ok;
    }

    public uint GetD3D11AdapterIndex() => 0;

    public override int Receive(nint samplePointer)
    {
        if (samplePointer == 0 || Flushing)
            return Hr.False;
        nint texture = 0;
        nint presenterTexture = 0;
        var sampleAddRef = false;
        try
        {
            var sample = (IMediaSampleD3D11)Marshal.GetTypedObjectForIUnknown(samplePointer, typeof(IMediaSampleD3D11));
            uint slice;
            try
            {
                if (sample.GetD3D11Texture(0, out texture, out slice) < 0 || texture == 0)
                    return ReceiveCpu(samplePointer);
            }
            finally
            {
                Marshal.ReleaseComObject(sample);
            }

            nint sharedHandle = 0;
            if (_outputDevice != null)
            {
                using var textureWrapper = new ID3D11Texture2D(texture);
                textureWrapper.AddRef();
                using var dxgiResource = textureWrapper.QueryInterface<IDXGIResource>();
                sharedHandle = dxgiResource.SharedHandle;
                if (sharedHandle == 0 && _device != _outputDevice.Device)
                {
                    PlaybackTelemetry.Trace("LavFrameRejected", Guid.Empty, 0,
                        data: new Dictionary<string, object?> { ["reason"] = "Missing shared texture handle" });
                    _filter.RaiseFaulted(new LavVideoOutputException(
                        "LAV D3D11 视频帧缺少共享纹理句柄。",
                        new InvalidOperationException("Missing shared texture handle.")));
                    return Hr.Fail;
                }
                if (_device != _outputDevice.Device)
                    presenterTexture = GetPresenterTexture(texture, sharedHandle);
            }
            var hasTime = SampleTime(samplePointer, out var start, out var end) >= 0;
            var media = hasTime ? _filter.MediaTime(start) : -1;
            if (hasTime && !_filter.WaitUntil(start, media, end - start))
            {
                if (Interlocked.Increment(ref _droppedSamples) % PlaybackTelemetry.FrameSampling == 1)
                {
                    PlaybackTelemetry.Trace("LavSampleDropped", Guid.Empty, 0, data: new Dictionary<string, object?>
                    {
                        ["pts"] = media,
                        ["flushing"] = Flushing,
                        ["droppedSamples"] = _droppedSamples
                    });
                }
                return Hr.Ok;
            }
            if (Flushing)
                return Hr.Ok;
            var decodeFenceValue = SignalDecoderCompleted();
            if (hasTime)
                _filter.NotePresented(media);
            AddRef(samplePointer);
            sampleAddRef = true;
            var textureReference = texture;
            var presenterTextureReference = presenterTexture;
            var sampleReference = samplePointer;
            var frame = new D3D11FrameLease
            {
                Width = Width,
                Height = Height,
                PixelFormat = _pixelFormat,
                PresentationTimestamp = hasTime ? media : 0,
                Duration = hasTime ? Math.Max(0, end - start) : 0,
                TimeBase = MediaTimeBase.TimeSpanTicks,
                FrameSequence = Interlocked.Increment(ref _frameSequence),
                DeviceGeneration = _outputDevice?.DeviceGeneration ?? 0,
                ZeroCopyLevel = ZeroCopyLevel.Z4,
                Resource = new D3D11FrameResource(
                    presenterTexture != 0 ? presenterTexture : texture,
                    (int)slice,
                    _adapterLuid,
                    presenterTexture != 0 ? 0 : sharedHandle,
                    Fence: _sharedDecodeFence,
                    FenceValue: decodeFenceValue,
                    SourceDevice: presenterTexture != 0 ? _outputDevice!.Device : _device),
                ReleaseBuffer = () =>
                {
                    if (presenterTextureReference != 0)
                        Marshal.Release(presenterTextureReference);
                    Marshal.Release(textureReference);
                    Marshal.Release(sampleReference);
                }
            };
            texture = 0;
            presenterTexture = 0;
            sampleAddRef = false;
            _filter.Deliver(frame);
            if (hasTime)
                _filter.NotePausedFrame(media);
            return Hr.Ok;
        }
        catch (Exception ex)
        {
            PlaybackTelemetry.Trace("LavFrameReceiveFailed", Guid.Empty, 0, data: new Dictionary<string, object?>
            {
                ["hresult"] = unchecked((uint)ex.HResult),
                ["error"] = ex.Message,
                ["flushing"] = Flushing,
                ["outputDeviceGeneration"] = _outputDevice?.DeviceGeneration ?? 0,
                ["hasOutputTexture"] = presenterTexture != 0
            });
            _filter.RaiseFaulted(new LavVideoOutputException("LAV D3D11 视频输出失败。", ex));
            if (sampleAddRef)
                Marshal.Release(samplePointer);
            return texture == 0 ? ReceiveCpu(samplePointer) : Hr.Fail;
        }
        finally
        {
            if (presenterTexture != 0)
                Marshal.Release(presenterTexture);
            if (texture != 0)
                Marshal.Release(texture);
        }
    }

    private ulong SignalDecoderCompleted()
    {
        if (_decodeFence == null)
            return 0;
        var locked = false;
        if (_decoderMutex != 0)
        {
            var wait = WaitForSingleObject(_decoderMutex, 5000);
            if (wait is not (0 or 0x80))
                throw new InvalidOperationException($"等待 LAV D3D11 设备锁失败（0x{wait:X8}）。");
            locked = true;
        }
        try
        {
            using var context = new ID3D11DeviceContext(_context);
            context.AddRef();
            using var context4 = context.QueryInterface<ID3D11DeviceContext4>();
            var value = ++_decodeFenceValue;
            context4.Signal(_decodeFence, value);
            return value;
        }
        finally
        {
            if (locked)
                ReleaseMutex(_decoderMutex);
        }
    }

    private nint GetPresenterTexture(nint sourceTexture, nint sharedHandle)
    {
        lock (_outputDevice!.SyncRoot)
        {
            var generation = _outputDevice.DeviceGeneration;
            if (_cachedPresenterTexture == 0
                || _cachedSourceTexture != sourceTexture
                || _cachedSharedHandle != sharedHandle
                || _cachedDeviceGeneration != generation)
            {
                using var outputDevice = new ID3D11Device(_outputDevice.Device);
                outputDevice.AddRef();
                using var opened = outputDevice.OpenSharedResource<ID3D11Texture2D>(sharedHandle);
                var previous = _cachedPresenterTexture;
                _cachedPresenterTexture = opened.NativePointer;
                opened.AddRef();
                _cachedSourceTexture = sourceTexture;
                _cachedSharedHandle = sharedHandle;
                _cachedDeviceGeneration = generation;
                Interlocked.Increment(ref _sharedTextureOpenCount);
                if (previous != 0)
                    Marshal.Release(previous);
                PlaybackTelemetry.Trace("LavSharedTextureOpened", Guid.Empty, 0, data: new Dictionary<string, object?>
                {
                    ["openCount"] = _sharedTextureOpenCount,
                    ["deviceGeneration"] = generation
                });
            }
            return AddRef(_cachedPresenterTexture);
        }
    }

    public void ReleaseResources()
    {
        if (_outputDevice != null)
        {
            lock (_outputDevice.SyncRoot)
                ReleaseCachedPresenterTexture();
        }
        else
        {
            ReleaseCachedPresenterTexture();
        }
        var device = _device;
        var context = _context;
        _device = 0;
        _context = 0;
        _decoderMutex = 0;
        ReleaseDecodeFence();
        if (device != 0)
            Marshal.Release(device);
        if (context != 0)
            Marshal.Release(context);
    }

    private void ReleaseDecodeFence()
    {
        _decodeFence?.Dispose();
        _decodeFence = null;
        _decodeFenceValue = 0;
        if (_sharedDecodeFence != 0)
            CloseHandle(_sharedDecodeFence);
        _sharedDecodeFence = 0;
    }

    private void ReleaseCachedPresenterTexture()
    {
        var texture = _cachedPresenterTexture;
        _cachedPresenterTexture = 0;
        _cachedSourceTexture = 0;
        _cachedSharedHandle = 0;
        _cachedDeviceGeneration = -1;
        if (texture != 0)
            Marshal.Release(texture);
    }

    private int ReceiveCpu(nint samplePointer)
    {
        if (SamplePointer(samplePointer, out var buffer) < 0 || buffer == 0)
            return Hr.Fail;
        var length = SampleLength(samplePointer);
        var bytesPerSample = _pixelFormat == VideoPixelFormat.P010 ? 2 : 1;
        var stride = Width * bytesPerSample;
        var needed = stride * Height * 3 / 2;
        if (length < needed || Width < 2 || Height < 2)
            return Hr.Fail;
        var hasTime = SampleTime(samplePointer, out var start, out var end) >= 0;
        var media = hasTime ? _filter.MediaTime(start) : -1;
        if (hasTime && !_filter.WaitUntil(start, media, end - start))
            return Hr.Ok;
        if (Flushing)
            return Hr.Ok;
        var data = _fallbackPool.Rent(needed);
        var frame = new CpuFrameLease
        {
            Width = Width,
            Height = Height,
            PixelFormat = _pixelFormat,
            Data = data,
            Planes = [new VideoFramePlane(0, stride), new VideoFramePlane(stride * Height, stride)],
            PresentationTimestamp = hasTime ? media : 0,
            Duration = hasTime ? Math.Max(0, end - start) : 0,
            TimeBase = MediaTimeBase.TimeSpanTicks,
            ZeroCopyLevel = ZeroCopyLevel.Z2,
            ReleaseBuffer = () => _fallbackPool.Return(data)
        };
        try
        {
            Marshal.Copy(buffer, data, 0, needed);
            if (hasTime)
                _filter.NotePresented(media);
            _filter.Deliver(frame);
            if (hasTime)
                _filter.NotePausedFrame(media);
            return Hr.Ok;
        }
        catch
        {
            frame.ReturnBuffer();
            return Hr.Fail;
        }
    }

    private static unsafe int SamplePointer(nint sample, out nint buffer)
    {
        var table = *(nint**)sample;
        var call = (delegate* unmanaged[Stdcall]<nint, nint*, int>)table[3];
        nint value;
        var result = call(sample, &value);
        buffer = value;
        return result;
    }

    private static unsafe int SampleLength(nint sample)
    {
        var table = *(nint**)sample;
        return ((delegate* unmanaged[Stdcall]<nint, int>)table[11])(sample);
    }

    private static unsafe int SampleTime(nint sample, out long start, out long end)
    {
        var table = *(nint**)sample;
        var call = (delegate* unmanaged[Stdcall]<nint, long*, long*, int>)table[5];
        long value;
        long stop;
        var result = call(sample, &value, &stop);
        start = value;
        end = stop;
        return result;
    }

    private static long ToInt64(Vortice.Luid value) => (uint)value.LowPart | ((long)value.HighPart << 32);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(nint handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReleaseMutex(nint handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);

    private static unsafe nint AddRef(nint pointer)
    {
        if (pointer == 0)
            return 0;
        var table = *(nint**)pointer;
        ((delegate* unmanaged[Stdcall]<nint, int>)table[1])(pointer);
        return pointer;
    }
}
