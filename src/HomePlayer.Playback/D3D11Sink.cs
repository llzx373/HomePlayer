using System.Runtime.InteropServices;
using HomePlayer.Core;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace HomePlayer.Playback;

[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
public sealed class D3D11SinkFilter : BgraSinkFilter
{
    public D3D11SinkFilter(D3D11SharedDevice? outputDevice = null) => _pin = new D3D11SinkPin(this, outputDevice);

    public override string PixelFormat => _pin is D3D11SinkPin pin ? pin.PixelFormat : "Unknown";
    public nint DecoderDevice => ((D3D11SinkPin)_pin).Device;
    public nint DecoderContext => ((D3D11SinkPin)_pin).Context;
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
    private long _adapterLuid;

    public D3D11SinkPin(BgraSinkFilter filter, D3D11SharedDevice? outputDevice = null) : base(filter)
    {
        _outputDevice = outputDevice;
    }

    public string PixelFormat => _pixelFormat == VideoPixelFormat.P010 ? "P010" : "NV12";
    public nint Device => _device;
    public nint Context => _context;

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
        _device = AddRef(device);
        _context = AddRef(context);
        if (previousDevice != 0)
            Marshal.Release(previousDevice);
        if (previousContext != 0)
            Marshal.Release(previousContext);
        return Hr.Ok;
    }

    public uint GetD3D11AdapterIndex() => 0;

    public override int Receive(nint samplePointer)
    {
        if (samplePointer == 0 || Flushing)
            return Hr.False;
        nint texture = 0;
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
                    return Hr.Fail;
            }
            var hasTime = SampleTime(samplePointer, out var start, out var end) >= 0;
            var media = hasTime ? _filter.MediaTime(start) : -1;
            if (hasTime && !_filter.WaitUntil(start, media, end - start))
                return Hr.Ok;
            if (Flushing)
                return Hr.Ok;
            if (hasTime)
                _filter.NotePresented(media);
            AddRef(samplePointer);
            sampleAddRef = true;
            var textureReference = texture;
            var sampleReference = samplePointer;
            var frame = new D3D11FrameLease
            {
                Width = Width,
                Height = Height,
                PixelFormat = _pixelFormat,
                PresentationTimestamp = hasTime ? media : 0,
                Duration = hasTime ? Math.Max(0, end - start) : 0,
                TimeBase = MediaTimeBase.TimeSpanTicks,
                DeviceGeneration = _outputDevice?.DeviceGeneration ?? 0,
                ZeroCopyLevel = ZeroCopyLevel.Z4,
                Resource = new D3D11FrameResource(texture, (int)slice, _adapterLuid, sharedHandle, SourceDevice: _device),
                ReleaseBuffer = () =>
                {
                    Marshal.Release(textureReference);
                    Marshal.Release(sampleReference);
                }
            };
            texture = 0;
            sampleAddRef = false;
            _filter.Deliver(frame);
            if (hasTime)
                _filter.NotePausedFrame(media);
            return Hr.Ok;
        }
        catch
        {
            if (texture != 0)
                Marshal.Release(texture);
            if (sampleAddRef)
                Marshal.Release(samplePointer);
            return texture == 0 ? ReceiveCpu(samplePointer) : Hr.Fail;
        }
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

    private static unsafe nint AddRef(nint pointer)
    {
        if (pointer == 0)
            return 0;
        var table = *(nint**)pointer;
        ((delegate* unmanaged[Stdcall]<nint, int>)table[1])(pointer);
        return pointer;
    }
}
