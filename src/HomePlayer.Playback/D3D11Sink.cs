using System.Runtime.InteropServices;
using HomePlayer.Core;

namespace HomePlayer.Playback;

[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
public sealed class D3D11SinkFilter : BgraSinkFilter
{
    public D3D11SinkFilter() => _pin = new D3D11SinkPin(this);

    public override string PixelFormat => _pin is D3D11SinkPin pin ? pin.PixelFormat : "Unknown";
    public nint DecoderDevice => ((D3D11SinkPin)_pin).Device;
    public nint DecoderContext => ((D3D11SinkPin)_pin).Context;
}

[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
public sealed class D3D11SinkPin : BgraSinkPin, ID3D11DecoderConfiguration
{
    private VideoPixelFormat _pixelFormat = VideoPixelFormat.Nv12;
    private nint _device;
    private nint _context;

    public D3D11SinkPin(BgraSinkFilter filter) : base(filter)
    {
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
                    return Hr.Fail;
            }
            finally
            {
                Marshal.ReleaseComObject(sample);
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
                ZeroCopyLevel = ZeroCopyLevel.Z4,
                Resource = new D3D11FrameResource(texture, (int)slice, 0),
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
            return Hr.Fail;
        }
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

    private static unsafe nint AddRef(nint pointer)
    {
        if (pointer == 0)
            return 0;
        var table = *(nint**)pointer;
        ((delegate* unmanaged[Stdcall]<nint, int>)table[1])(pointer);
        return pointer;
    }
}
