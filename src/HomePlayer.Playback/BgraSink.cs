using System.Runtime.InteropServices;
using HomePlayer.Core;

namespace HomePlayer.Playback;

[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
public sealed class BgraSinkFilter : IBaseFilterSink
{
    private readonly BgraSinkPin _pin;
    private volatile int _state;
    private IntPtr _graph;
    private string _name = "HomePlayer Video Sink";
    private readonly System.Diagnostics.Stopwatch _playClock = new();
    private IntPtr _clock;
    private long _runStart;
    private long _lastPresentMs;
    private volatile bool _completed;
    private long _segmentMediaStart;
    private long _segmentStreamStart;
    private bool _segmentTimed;
    private bool _useSampleAsMedia;
    private long _presentedTicks = -1;
    private long _pausedFrameTicks = -1;
    private long _pausedFrameStamp;
    private readonly ManualResetEventSlim _pausedFrameShown = new(false);

    public BgraSinkFilter() => _pin = new BgraSinkPin(this);

    public event Action<FrameLease>? FrameArrived;
    public event Action? Ended;
    public bool HasPresented => Interlocked.Read(ref _presentedTicks) >= 0;
    internal bool Paused => _state == RendererGate.Paused;

    public TimeSpan PresentedPosition => TimeSpan.FromTicks(Math.Max(0, Interlocked.Read(ref _presentedTicks)));

    public int Width => _pin.Width;
    public int Height => _pin.Height;
    public bool BottomUp => _pin.BottomUp;
    public string PixelFormat => "RGB32";

    public int GetClassID(out Guid classId)
    {
        classId = new Guid("A6E0B0E1-6B4A-4E0A-9B1E-6C1A0B0E0001");
        return Hr.Ok;
    }

    public int Stop()
    {
        _state = 0;
        _completed = false;
        _playClock.Reset();
        ReplaceClock(0);
        Interlocked.Exchange(ref _pausedFrameTicks, -1);
        return Hr.Ok;
    }

    public int Pause()
    {
        _state = 1;
        if (_playClock.IsRunning)
            _playClock.Stop();
        return Hr.Ok;
    }

    public int Run(long start)
    {
        _state = 2;
        _completed = false;
        _runStart = start;
        _playClock.Restart();
        return Hr.Ok;
    }

    public int GetState(int timeoutMs, out int state)
    {
        state = _state;
        return Hr.Ok;
    }

    public int SetSyncSource(IntPtr clock)
    {
        ReplaceClock(clock);
        return Hr.Ok;
    }

    public int GetSyncSource(out IntPtr clock)
    {
        clock = _clock == 0 ? 0 : AddRef(_clock);
        return Hr.Ok;
    }

    public int EnumPins(out IntPtr enumerator)
    {
        enumerator = Marshal.GetComInterfaceForObject(new PinEnumerator(_pin), typeof(IEnumPinsSink));
        return Hr.Ok;
    }

    public int FindPin(string id, out IntPtr pin)
    {
        if (id == "In")
        {
            pin = Marshal.GetComInterfaceForObject(_pin, typeof(IPinSink));
            return Hr.Ok;
        }

        pin = 0;
        return Hr.Fail;
    }

    public int QueryFilterInfo(out FilterInfo info)
    {
        info = new FilterInfo
        {
            Name = _name,
            Graph = _graph == 0 ? 0 : AddRef(_graph)
        };
        return Hr.Ok;
    }

    public int JoinFilterGraph(IntPtr graph, string name)
    {
        _graph = graph;
        if (!string.IsNullOrEmpty(name))
            _name = name;
        return Hr.Ok;
    }

    public int QueryVendorInfo(out IntPtr vendor)
    {
        vendor = 0;
        return Hr.NotImpl;
    }

    public IntPtr PinPointer => Marshal.GetComInterfaceForObject(_pin, typeof(IPinSink));

    internal bool IsRunning => _state == 2;

    internal void Deliver(FrameLease frame)
    {
        var handler = FrameArrived;
        if (handler == null)
        {
            frame.ReturnBuffer();
            return;
        }

        try
        {
            handler(frame);
        }
        catch
        {
            frame.ReturnBuffer();
            throw;
        }
    }

    internal void NoteSegment(long start)
    {
        _segmentMediaStart = Math.Max(0, start);
        _segmentTimed = false;
    }

    internal long MediaTime(long sampleStream)
    {
        if (!_segmentTimed)
        {
            _segmentStreamStart = sampleStream;
            _useSampleAsMedia = Math.Abs(sampleStream - _segmentMediaStart) < 20_000_000;
            _segmentTimed = true;
        }

        var media = _useSampleAsMedia
            ? sampleStream
            : _segmentMediaStart + (sampleStream - _segmentStreamStart);
        return Math.Max(0, media);
    }

    internal void NotePresented(long media) => Interlocked.Exchange(ref _presentedTicks, Math.Max(0, media));

    internal void ClearPresented() => Interlocked.Exchange(ref _presentedTicks, -1);

    internal void RequestPausedFrame(long ticks)
    {
        _pausedFrameShown.Reset();
        Volatile.Write(ref _pausedFrameStamp, Environment.TickCount64);
        Interlocked.Exchange(ref _pausedFrameTicks, Math.Max(0, ticks));
    }

    internal bool WaitForPausedFrame(int timeoutMs) => _pausedFrameShown.Wait(timeoutMs);

    internal void NotePausedFrame(long media)
    {
        var target = Interlocked.Read(ref _pausedFrameTicks);
        if (target < 0 || media < target)
            return;
        Interlocked.Exchange(ref _pausedFrameTicks, -1);
        _pausedFrameShown.Set();
    }

    private bool? DecidePausedFrame(long mediaStart, long duration)
    {
        var target = Interlocked.Read(ref _pausedFrameTicks);
        if (target < 0)
            return null;
        var reached = mediaStart + Math.Max(0, duration) >= target;
        if (!reached && Environment.TickCount64 - Volatile.Read(ref _pausedFrameStamp) < 1000)
            return false;
        Interlocked.Exchange(ref _pausedFrameTicks, -1);
        _pausedFrameShown.Set();
        return true;
    }

    internal bool WaitUntil(long streamTime, long mediaTime, long duration)
    {
        while (true)
        {
            var decision = RendererGate.Decide(_state, _pin.Flushing, streamTime);
            if (decision == true)
                return true;
            if (decision == false)
                return false;

            if (_state == RendererGate.Paused)
            {
                var paused = DecidePausedFrame(mediaTime, duration);
                if (paused != null)
                    return paused.Value;
                if (_pin.FlushGate.Wait(20))
                    return false;
                continue;
            }

            var delay = TimeSpan.FromTicks(streamTime) - CurrentStreamTime();
            if (delay <= TimeSpan.FromMilliseconds(2))
            {
                var now = Environment.TickCount64;
                if (delay < TimeSpan.FromMilliseconds(-80) && now - _lastPresentMs < 40)
                    return false;
                _lastPresentMs = now;
                return true;
            }

            if (delay > TimeSpan.FromSeconds(5))
                return true;
            var slice = delay > TimeSpan.FromMilliseconds(20) ? 20 : Math.Max(1, (int)delay.TotalMilliseconds);
            if (_pin.FlushGate.Wait(slice))
                return false;
        }
    }

    private unsafe TimeSpan CurrentStreamTime()
    {
        var clock = _clock;
        if (clock != 0)
        {
            var table = *(IntPtr**)clock;
            var getTime = (delegate* unmanaged[Stdcall]<IntPtr, long*, int>)table[3];
            long time;
            if (getTime(clock, &time) >= 0)
                return TimeSpan.FromTicks(Math.Max(0, time - _runStart));
        }

        return _playClock.Elapsed;
    }

    private void ReplaceClock(IntPtr clock)
    {
        var previous = _clock;
        _clock = clock == 0 ? 0 : AddRef(clock);
        if (previous != 0)
            Marshal.Release(previous);
    }

    internal void NotifyEnd()
    {
        if (_completed)
            return;
        _completed = true;
        if (_graph != 0)
        {
            var iid = typeof(IMediaEventSink).GUID;
            if (Marshal.QueryInterface(_graph, in iid, out var sink) >= 0)
            {
                var rcw = (IMediaEventSink)Marshal.GetTypedObjectForIUnknown(sink, typeof(IMediaEventSink));
                rcw.Notify(Hr.EcComplete, 0, 0);
                Marshal.Release(sink);
                Marshal.ReleaseComObject(rcw);
            }
        }

        Ended?.Invoke();
    }

    private static IntPtr AddRef(IntPtr pointer)
    {
        var table = Marshal.ReadIntPtr(pointer);
        var addRef = Marshal.GetDelegateForFunctionPointer<AddRefDelegate>(Marshal.ReadIntPtr(table, IntPtr.Size));
        addRef(pointer);
        return pointer;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int AddRefDelegate(IntPtr self);
}

[ComVisible(true)]
[Guid("56A86895-0AD4-11CE-B03A-0020AF0BA770")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IBaseFilterSink
{
    [PreserveSig] int GetClassID(out Guid classId);
    [PreserveSig] int Stop();
    [PreserveSig] int Pause();
    [PreserveSig] int Run(long start);
    [PreserveSig] int GetState(int timeoutMs, out int state);
    [PreserveSig] int SetSyncSource(IntPtr clock);
    [PreserveSig] int GetSyncSource(out IntPtr clock);
    [PreserveSig] int EnumPins(out IntPtr enumerator);
    [PreserveSig] int FindPin([MarshalAs(UnmanagedType.LPWStr)] string id, out IntPtr pin);
    [PreserveSig] int QueryFilterInfo(out FilterInfo info);
    [PreserveSig] int JoinFilterGraph(IntPtr graph, [MarshalAs(UnmanagedType.LPWStr)] string name);
    [PreserveSig] int QueryVendorInfo(out IntPtr vendor);
}

[ComVisible(true)]
[Guid("56A86891-0AD4-11CE-B03A-0020AF0BA770")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IPinSink
{
    [PreserveSig] int Connect(IntPtr receivePin, IntPtr mediaType);
    [PreserveSig] int ReceiveConnection(IntPtr connector, IntPtr mediaType);
    [PreserveSig] int Disconnect();
    [PreserveSig] int ConnectedTo(out IntPtr pin);
    [PreserveSig] int ConnectionMediaType(IntPtr mediaType);
    [PreserveSig] int QueryPinInfo(out PinInfo info);
    [PreserveSig] int QueryDirection(out int direction);
    [PreserveSig] int QueryId(out IntPtr id);
    [PreserveSig] int QueryAccept(IntPtr mediaType);
    [PreserveSig] int EnumMediaTypes(out IntPtr enumerator);
    [PreserveSig] int QueryInternalConnections(IntPtr pins, IntPtr count);
    [PreserveSig] int EndOfStream();
    [PreserveSig] int BeginFlush();
    [PreserveSig] int EndFlush();
    [PreserveSig] int NewSegment(long start, long stop, double rate);
}

[ComVisible(true)]
[Guid("56A8689D-0AD4-11CE-B03A-0020AF0BA770")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMemInputPinSink
{
    [PreserveSig] int GetAllocator(out IntPtr allocator);
    [PreserveSig] int NotifyAllocator(IntPtr allocator, int readOnly);
    [PreserveSig] int GetAllocatorRequirements(IntPtr properties);
    [PreserveSig] int Receive(IntPtr sample);
    [PreserveSig] int ReceiveMultiple(IntPtr samples, int count, out int processed);
    [PreserveSig] int ReceiveCanBlock();
}

[ComVisible(true)]
[Guid("56A86892-0AD4-11CE-B03A-0020AF0BA770")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IEnumPinsSink
{
    [PreserveSig] int Next(int count, IntPtr pins, IntPtr fetched);
    [PreserveSig] int Skip(int count);
    [PreserveSig] int Reset();
    [PreserveSig] int Clone(out IntPtr clone);
}

[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
public sealed class BgraSinkPin : IPinSink, IMemInputPinSink
{
    private readonly BgraSinkFilter _filter;
    private readonly FrameBufferPool _pool = new(4);
    private IntPtr _connected;

    public BgraSinkPin(BgraSinkFilter filter) => _filter = filter;

    private volatile bool _flushing;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public bool BottomUp { get; private set; }
    public bool Flushing => _flushing;
    public ManualResetEventSlim FlushGate { get; } = new(false);

    public int Connect(IntPtr receivePin, IntPtr mediaType) => Hr.NotImpl;

    public int ReceiveConnection(IntPtr connector, IntPtr mediaType)
    {
        if (QueryAccept(mediaType) != Hr.Ok)
            return Hr.TypeNotAccepted;
        if (_connected != 0)
        {
            Marshal.Release(_connected);
            _connected = 0;
        }

        _connected = AddRef(connector);
        ReadFormat(mediaType);
        return Hr.Ok;
    }

    public int Disconnect()
    {
        if (_connected == 0)
            return Hr.NotConnected;
        Marshal.Release(_connected);
        _connected = 0;
        return Hr.Ok;
    }

    public int ConnectedTo(out IntPtr pin)
    {
        if (_connected == 0)
        {
            pin = 0;
            return Hr.NotConnected;
        }

        pin = AddRef(_connected);
        return Hr.Ok;
    }

    public int ConnectionMediaType(IntPtr mediaType) => Hr.NotImpl;
    public int QueryPinInfo(out PinInfo info)
    {
        info = new PinInfo
        {
            Filter = Marshal.GetComInterfaceForObject(_filter, typeof(IBaseFilterSink)),
            Direction = 0,
            Name = "In"
        };
        return Hr.Ok;
    }

    public int QueryDirection(out int direction)
    {
        direction = 0;
        return Hr.Ok;
    }

    public int QueryId(out IntPtr id)
    {
        id = Marshal.StringToCoTaskMemUni("In");
        return Hr.Ok;
    }

    public int QueryAccept(IntPtr mediaType)
    {
        if (mediaType == 0)
            return Hr.Pointer;
        var type = Marshal.PtrToStructure<AmMediaType>(mediaType);
        return type.MajorType == DsGuid.Video && type.SubType == DsGuid.Rgb32 ? Hr.Ok : Hr.TypeNotAccepted;
    }

    public int EnumMediaTypes(out IntPtr enumerator)
    {
        enumerator = 0;
        return Hr.NotImpl;
    }

    public int QueryInternalConnections(IntPtr pins, IntPtr count) => Hr.NotImpl;

    public int EndOfStream()
    {
        _filter.NotifyEnd();
        return Hr.Ok;
    }

    public int BeginFlush()
    {
        _flushing = true;
        FlushGate.Set();
        return Hr.Ok;
    }

    public int EndFlush()
    {
        _flushing = false;
        FlushGate.Reset();
        return Hr.Ok;
    }

    public int NewSegment(long start, long stop, double rate)
    {
        _filter.NoteSegment(start);
        return Hr.Ok;
    }

    public int GetAllocator(out IntPtr allocator)
    {
        allocator = 0;
        return Hr.NotImpl;
    }

    public int NotifyAllocator(IntPtr allocator, int readOnly) => Hr.Ok;
    public int GetAllocatorRequirements(IntPtr properties) => Hr.NotImpl;

    public int Receive(IntPtr samplePointer)
    {
        if (samplePointer == 0 || Flushing)
            return Hr.False;
        if (SamplePointer(samplePointer, out var buffer) < 0 || buffer == 0)
            return Hr.Ok;

        var length = SampleLength(samplePointer);
        if (length <= 0 || Width <= 0 || Height <= 0)
            return Hr.Ok;
        var hasTime = SampleTime(samplePointer, out var start, out var end) >= 0;
        var media = hasTime ? _filter.MediaTime(start) : -1;
        if (hasTime && !_filter.WaitUntil(start, media, end - start))
            return Hr.Ok;
        if (Flushing)
            return Hr.Ok;
        if (hasTime)
            _filter.NotePresented(media);
        var stride = Width * 4;
        var needed = stride * Height;
        var copyLength = Math.Min(length, needed);
        var data = _pool.Rent(needed);
        var frame = new VideoFrame
        {
            Width = Width,
            Height = Height,
            Stride = stride,
            Data = data,
            BottomUp = BottomUp,
            ReleaseBuffer = () => _pool.Return(data)
        };
        try
        {
            Marshal.Copy(buffer, data, 0, copyLength);
            if (copyLength < needed)
                Array.Clear(data, copyLength, needed - copyLength);
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

    private static unsafe int SamplePointer(IntPtr sample, out IntPtr buffer)
    {
        var table = *(IntPtr**)sample;
        var call = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)table[3];
        IntPtr value;
        var result = call(sample, &value);
        buffer = value;
        return result;
    }

    private static unsafe int SampleLength(IntPtr sample)
    {
        var table = *(IntPtr**)sample;
        return ((delegate* unmanaged[Stdcall]<IntPtr, int>)table[11])(sample);
    }

    private static unsafe int SampleTime(IntPtr sample, out long start, out long end)
    {
        var table = *(IntPtr**)sample;
        var call = (delegate* unmanaged[Stdcall]<IntPtr, long*, long*, int>)table[5];
        long value;
        long stop;
        var result = call(sample, &value, &stop);
        start = value;
        end = stop;
        return result;
    }

    public int ReceiveMultiple(IntPtr samples, int count, out int processed)
    {
        processed = 0;
        for (var i = 0; i < count; i++)
        {
            var sample = Marshal.ReadIntPtr(samples, i * IntPtr.Size);
            var result = Receive(sample);
            if (Hr.Failed(result))
                return result;
            processed++;
        }

        return Hr.Ok;
    }

    public int ReceiveCanBlock() => Hr.Ok;

    private void ReadFormat(IntPtr mediaType)
    {
        var type = Marshal.PtrToStructure<AmMediaType>(mediaType);
        var header = type.FormatType == DsGuid.FormatVideoInfo2 ? 72 : 48;
        if (type.Format == 0 || type.FormatSize < header + 20)
        {
            Width = 0;
            Height = 0;
            return;
        }

        Width = Math.Abs(Marshal.ReadInt32(type.Format, header + 4));
        var height = Marshal.ReadInt32(type.Format, header + 8);
        Height = Math.Abs(height);
        BottomUp = height > 0;
    }

    private static unsafe IntPtr AddRef(IntPtr pointer)
    {
        var table = *(IntPtr**)pointer;
        ((delegate* unmanaged[Stdcall]<IntPtr, int>)table[1])(pointer);
        return pointer;
    }
}

[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
public sealed class PinEnumerator : IEnumPinsSink
{
    private readonly BgraSinkPin _pin;
    private bool _done;

    public PinEnumerator(BgraSinkPin pin) => _pin = pin;

    public int Next(int count, IntPtr pins, IntPtr fetched)
    {
        if (_done || count <= 0)
        {
            if (fetched != 0)
                Marshal.WriteInt32(fetched, 0);
            return Hr.False;
        }

        Marshal.WriteIntPtr(pins, Marshal.GetComInterfaceForObject(_pin, typeof(IPinSink)));
        if (fetched != 0)
            Marshal.WriteInt32(fetched, 1);
        _done = true;
        return Hr.Ok;
    }

    public int Skip(int count)
    {
        if (count > 0)
            _done = true;
        return Hr.Ok;
    }

    public int Reset()
    {
        _done = false;
        return Hr.Ok;
    }

    public int Clone(out IntPtr clone)
    {
        var copy = new PinEnumerator(_pin) { _done = _done };
        clone = Marshal.GetComInterfaceForObject(copy, typeof(IEnumPinsSink));
        return Hr.Ok;
    }
}
