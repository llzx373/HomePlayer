using System.Runtime.InteropServices;

namespace HomePlayer.Playback;

internal static class DsGuid
{
    public static readonly Guid FilterGraph = new("E436EBB3-524F-11CE-9F53-0020AF0BA770");
    public static readonly Guid LavSplitterSource = new("B98D13E7-55DB-4385-A33D-09FD1BA26338");
    public static readonly Guid LavSplitter = new("171252A0-8820-4AFE-9DF8-5C92B2D66B04");
    public static readonly Guid LavVideo = new("EE30215D-164F-4A92-A4EB-9D4C13390F9F");
    public static readonly Guid LavAudio = new("E8E73B6B-4CB3-44A4-BE99-4F7BCB96E491");
    public static readonly Guid AsyncReader = new("E436EBB5-524F-11CE-9F53-0020AF0BA770");
    public static readonly Guid DirectSound = new("79376820-07D0-11CF-A24D-0020AFD79767");
    public static readonly Guid SystemDeviceEnum = new("62BE5D10-60EB-11d0-BD3B-00A0C911CE86");
    public static readonly Guid AudioRendererCategory = new("E0F158E1-CB04-11d0-BD4E-00A0C911CE86");
    public static readonly Guid Video = new("73646976-0000-0010-8000-00AA00389B71");
    public static readonly Guid Audio = new("73647561-0000-0010-8000-00AA00389B71");
    public static readonly Guid Stream = new("E436EB83-524F-11CE-9F53-0020AF0BA770");
    public static readonly Guid Rgb32 = new("E436EB7E-524F-11CE-9F53-0020AF0BA770");
    public static readonly Guid Nv12 = new("3231564E-0000-0010-8000-00AA00389B71");
    public static readonly Guid P010 = new("50303130-0000-0010-8000-00AA00389B71");
    public static readonly Guid FormatVideoInfo = new("05589F80-C356-11CE-BF01-00AA0055595A");
    public static readonly Guid FormatVideoInfo2 = new("F72A76A0-EB0A-11D0-ACE4-0000C0CC16BA");
}

internal static class Hr
{
    public const int Ok = 0;
    public const int False = 1;
    public const int NotImpl = unchecked((int)0x80004001);
    public const int NoInterface = unchecked((int)0x80004002);
    public const int Pointer = unchecked((int)0x80004003);
    public const int Fail = unchecked((int)0x80004005);
    public const int NotConnected = unchecked((int)0x80040209);
    public const int TypeNotAccepted = unchecked((int)0x8004022A);
    public const int SampleTimeNotSet = unchecked((int)0x80040249);
    public const int EcComplete = 0x01;

    public static bool Failed(int code) => code < 0;

    public static void Throw(int code, string action)
    {
        if (Failed(code))
            throw new InvalidOperationException($"{action}（0x{code:X8}）");
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct AmMediaType
{
    public Guid MajorType;
    public Guid SubType;
    public int FixedSizeSamples;
    public int TemporalCompression;
    public uint SampleSize;
    public Guid FormatType;
    public IntPtr Unk;
    public uint FormatSize;
    public IntPtr Format;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct FilterInfo
{
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
    public string Name;
    public IntPtr Graph;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct PinInfo
{
    public IntPtr Filter;
    public int Direction;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
    public string Name;
}

[ComImport, Guid("56A868B1-0AD4-11CE-B03A-0020AF0BA770"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface IMediaControl
{
    [PreserveSig] int Run();
    [PreserveSig] int Pause();
    [PreserveSig] int Stop();
    [PreserveSig] int GetState(int timeoutMs, out int state);
}

[ComImport, Guid("36B73880-C2C8-11CF-8B46-00805F6CEF60"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMediaSeeking
{
    [PreserveSig] int GetCapabilities(out uint caps);
    [PreserveSig] int CheckCapabilities(ref uint caps);
    [PreserveSig] int IsFormatSupported(ref Guid format);
    [PreserveSig] int QueryPreferredFormat(out Guid format);
    [PreserveSig] int GetTimeFormat(out Guid format);
    [PreserveSig] int IsUsingTimeFormat(ref Guid format);
    [PreserveSig] int SetTimeFormat(ref Guid format);
    [PreserveSig] int GetDuration(out long duration);
    [PreserveSig] int GetStopPosition(out long stop);
    [PreserveSig] int GetCurrentPosition(out long position);
    [PreserveSig] int ConvertTimeFormat(out long target, ref Guid targetFormat, long source, ref Guid sourceFormat);
    [PreserveSig] int SetPositions(ref long current, uint currentFlags, ref long stop, uint stopFlags);
    [PreserveSig] int GetPositions(out long current, out long stop);
    [PreserveSig] int GetAvailable(out long earliest, out long latest);
    [PreserveSig] int SetRate(double rate);
    [PreserveSig] int GetRate(out double rate);
    [PreserveSig] int GetPreroll(out long preroll);
}

[ComImport, Guid("56A868B3-0AD4-11CE-B03A-0020AF0BA770"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface IBasicAudio
{
    [PreserveSig] int put_Volume(int volume);
    [PreserveSig] int get_Volume(out int volume);
}

[ComImport, Guid("56A868A6-0AD4-11CE-B03A-0020AF0BA770"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IFileSourceFilter
{
    [PreserveSig] int Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, IntPtr mediaType);
    [PreserveSig] int GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string fileName, IntPtr mediaType);
}

[ComImport, Guid("56A86897-0AD4-11CE-B03A-0020AF0BA770"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IReferenceClock
{
    [PreserveSig] int GetTime(out long time);
}

[ComImport, Guid("56A8689A-0AD4-11CE-B03A-0020AF0BA770"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMediaSample
{
    [PreserveSig] int GetPointer(out IntPtr buffer);
    [PreserveSig] int GetSize();
    [PreserveSig] int GetTime(out long start, out long end);
    [PreserveSig] int SetTime(long start, long end);
    [PreserveSig] int IsSyncPoint();
    [PreserveSig] int SetSyncPoint(int isSync);
    [PreserveSig] int IsPreroll();
    [PreserveSig] int SetPreroll(int isPreroll);
    [PreserveSig] int GetActualDataLength();
}

[ComImport, Guid("56A868A2-0AD4-11CE-B03A-0020AF0BA770"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMediaEventSink
{
    [PreserveSig] int Notify(int eventCode, IntPtr param1, IntPtr param2);
}

[ComImport, Guid("C1960960-17F5-11D1-ABE1-00A0C905F375"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IamStreamSelect
{
    [PreserveSig] int Count(out int count);
    [PreserveSig] int Info(int index, IntPtr mediaType, out int flags, out int lcid, out int group, out IntPtr name, out IntPtr obj, out IntPtr unk);
    [PreserveSig] int Enable(int index, int flags);
}

[ComImport, Guid("89C31040-846B-11CE-97D3-00AA0055595A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IEnumMediaTypes
{
    [PreserveSig] int Next(int count, IntPtr mediaTypes, IntPtr fetched);
    [PreserveSig] int Skip(int count);
    [PreserveSig] int Reset();
    [PreserveSig] int Clone(out IntPtr clone);
}

[ComImport, Guid("56A86892-0AD4-11CE-B03A-0020AF0BA770"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IEnumPins
{
    [PreserveSig] int Next(int count, IntPtr pins, IntPtr fetched);
    [PreserveSig] int Skip(int count);
    [PreserveSig] int Reset();
    [PreserveSig] int Clone(out IEnumPins clone);
}

[ComImport, Guid("56A86891-0AD4-11CE-B03A-0020AF0BA770"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPin
{
    [PreserveSig] int Connect(IPin receivePin, IntPtr mediaType);
    [PreserveSig] int ReceiveConnection(IPin connector, IntPtr mediaType);
    [PreserveSig] int Disconnect();
    [PreserveSig] int ConnectedTo(out IPin pin);
    [PreserveSig] int ConnectionMediaType(out AmMediaType mediaType);
    [PreserveSig] int QueryPinInfo(out PinInfo info);
    [PreserveSig] int QueryDirection(out int direction);
    [PreserveSig] int QueryId(out IntPtr id);
    [PreserveSig] int QueryAccept(IntPtr mediaType);
    [PreserveSig] int EnumMediaTypes(out IEnumMediaTypes enumerator);
    [PreserveSig] int QueryInternalConnections(IntPtr pins, IntPtr count);
    [PreserveSig] int EndOfStream();
    [PreserveSig] int BeginFlush();
    [PreserveSig] int EndFlush();
    [PreserveSig] int NewSegment(long start, long stop, double rate);
}

[ComImport, Guid("56A86895-0AD4-11CE-B03A-0020AF0BA770"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IBaseFilter
{
    [PreserveSig] int GetClassID(out Guid classId);
    [PreserveSig] int Stop();
    [PreserveSig] int Pause();
    [PreserveSig] int Run(long start);
    [PreserveSig] int GetState(int timeoutMs, out int state);
    [PreserveSig] int SetSyncSource(IntPtr clock);
    [PreserveSig] int GetSyncSource(out IntPtr clock);
    [PreserveSig] int EnumPins(out IEnumPins enumerator);
    [PreserveSig] int FindPin([MarshalAs(UnmanagedType.LPWStr)] string id, out IPin pin);
    [PreserveSig] int QueryFilterInfo(out FilterInfo info);
    [PreserveSig] int JoinFilterGraph(IntPtr graph, [MarshalAs(UnmanagedType.LPWStr)] string name);
    [PreserveSig] int QueryVendorInfo(out IntPtr vendor);
}

[ComImport, Guid("56A86893-0AD4-11CE-B03A-0020AF0BA770"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IEnumFilters
{
    [PreserveSig] int Next(int count, IntPtr filters, IntPtr fetched);
    [PreserveSig] int Skip(int count);
    [PreserveSig] int Reset();
    [PreserveSig] int Clone(out IEnumFilters clone);
}

[ComImport, Guid("36B73882-C2C8-11CF-8B46-00805F6CEF60"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IFilterGraph2
{
    [PreserveSig] int AddFilter(IBaseFilter filter, [MarshalAs(UnmanagedType.LPWStr)] string name);
    [PreserveSig] int RemoveFilter(IBaseFilter filter);
    [PreserveSig] int EnumFilters(out IEnumFilters enumerator);
    [PreserveSig] int FindFilterByName([MarshalAs(UnmanagedType.LPWStr)] string name, out IBaseFilter filter);
    [PreserveSig] int ConnectDirect(IPin pinOut, IPin pinIn, IntPtr mediaType);
    [PreserveSig] int Reconnect(IPin pin);
    [PreserveSig] int Disconnect(IPin pin);
    [PreserveSig] int SetDefaultSyncSource();
    [PreserveSig] int Connect(IPin pinOut, IPin pinIn);
    [PreserveSig] int Render(IPin pinOut);
}

[ComImport, Guid("FA40D6E9-4D38-4761-ADD2-71A9EC5FD32F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ILavVideoSettings
{
    [PreserveSig] int SetRuntimeConfig(int enabled);
    [PreserveSig] int GetFormatConfiguration(int codec);
    [PreserveSig] int SetFormatConfiguration(int codec, int enabled);
    [PreserveSig] int SetNumThreads(uint count);
    [PreserveSig] uint GetNumThreads();
    [PreserveSig] int SetStreamAR(uint mode);
    [PreserveSig] uint GetStreamAR();
    [PreserveSig] int GetPixelFormat(int format);
    [PreserveSig] int SetPixelFormat(int format, int enabled);
    [PreserveSig] int SetRGBOutputRange(uint range);
    [PreserveSig] uint GetRGBOutputRange();
    [PreserveSig] int SetDeintFieldOrder(uint order);
    [PreserveSig] uint GetDeintFieldOrder();
    [PreserveSig] int SetDeintAggressive(int enabled);
    [PreserveSig] int GetDeintAggressive();
    [PreserveSig] int SetDeintForce(int enabled);
    [PreserveSig] int GetDeintForce();
    [PreserveSig] uint CheckHWAccelSupport(uint hwAccel);
    [PreserveSig] int SetHWAccel(uint hwAccel);
    [PreserveSig] uint GetHWAccel();
    [PreserveSig] int SetHWAccelCodec(uint codec, int enabled);
    [PreserveSig] int GetHWAccelCodec(uint codec);
}

[ComImport, Guid("1CC2385F-36FA-41B1-9942-5024CE0235DC"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ILavVideoStatus
{
    [PreserveSig] IntPtr GetActiveDecoderName();
    [PreserveSig] int GetHWAccelActiveDevice(out IntPtr deviceName);
}

[ComImport, Guid("BC8753F5-0AC8-4806-8E5F-A12B2AFE153E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMediaSampleD3D11
{
    [PreserveSig] int GetD3D11Texture(int view, out IntPtr texture, out uint arraySlice);
}

[ComImport, Guid("2BB66002-46B7-4F13-9036-7053328742BE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ID3D11DecoderConfiguration
{
    [PreserveSig] int ActivateD3D11Decoding(IntPtr device, IntPtr context, IntPtr mutex, uint flags);
    [PreserveSig] uint GetD3D11AdapterIndex();
}

[ComImport, Guid("4158A22B-6553-45D0-8069-24716F8FF171"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ILavAudioSettings
{
    [PreserveSig] int SetRuntimeConfig(int enabled);
    [PreserveSig] int GetDRC(out int enabled, out int level);
    [PreserveSig] int SetDRC(int enabled, int level);
    [PreserveSig] int GetFormatConfiguration(int codec);
    [PreserveSig] int SetFormatConfiguration(int codec, int enabled);
    [PreserveSig] int GetBitstreamConfig(int codec);
    [PreserveSig] int SetBitstreamConfig(int codec, int enabled);
    [PreserveSig] int GetDTSHDFraming();
    [PreserveSig] int SetDTSHDFraming(int enabled);
    [PreserveSig] int GetAutoAVSync();
    [PreserveSig] int SetAutoAVSync(int enabled);
    [PreserveSig] int GetOutputStandardLayout();
    [PreserveSig] int SetOutputStandardLayout(int enabled);
    [PreserveSig] int GetExpandMono();
    [PreserveSig] int SetExpandMono(int enabled);
    [PreserveSig] int GetExpand61();
    [PreserveSig] int SetExpand61(int enabled);
    [PreserveSig] int GetAllowRawSPDIFInput();
    [PreserveSig] int SetAllowRawSPDIFInput(int enabled);
    [PreserveSig] int GetSampleFormat(int format);
    [PreserveSig] int SetSampleFormat(int format, int enabled);
    [PreserveSig] int GetAudioDelay(out int enabled, out int delay);
    [PreserveSig] int SetAudioDelay(int enabled, int delay);
    [PreserveSig] int SetMixingEnabled(int enabled);
    [PreserveSig] int GetMixingEnabled();
    [PreserveSig] int SetMixingLayout(uint layout);
    [PreserveSig] uint GetMixingLayout();
    [PreserveSig] int SetMixingFlags(uint flags);
    [PreserveSig] uint GetMixingFlags();
}

[ComImport, Guid("29840822-5B84-11D0-BD3B-00A0C911CE86"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ICreateDevEnum
{
    [PreserveSig] int CreateClassEnumerator(in Guid type, out IEnumMoniker enumerator, int flags);
}

[ComImport, Guid("00000102-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IEnumMoniker
{
    [PreserveSig] int Next(int count, IntPtr monikers, IntPtr fetched);
    [PreserveSig] int Skip(int count);
    [PreserveSig] int Reset();
    [PreserveSig] int Clone(out IEnumMoniker clone);
}

[ComImport, Guid("0000000F-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMonikerDs
{
    [PreserveSig] int GetClassID(out Guid classId);
    [PreserveSig] int IsDirty();
    [PreserveSig] int Load(IntPtr stream);
    [PreserveSig] int Save(IntPtr stream, int clearDirty);
    [PreserveSig] int GetSizeMax(out long size);
    [PreserveSig] int BindToObject(IntPtr bindContext, IntPtr left, in Guid iid, [MarshalAs(UnmanagedType.Interface)] out object result);
    [PreserveSig] int BindToStorage(IntPtr bindContext, IntPtr left, in Guid iid, [MarshalAs(UnmanagedType.Interface)] out object result);
}

[ComImport, Guid("55272A00-42CB-11CE-8135-00AA004BB851"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPropertyBag
{
    [PreserveSig] int Read([MarshalAs(UnmanagedType.LPWStr)] string name, [In, Out, MarshalAs(UnmanagedType.Struct)] ref object value, IntPtr errorLog);
    [PreserveSig] int Write([MarshalAs(UnmanagedType.LPWStr)] string name, ref object value);
}

[ComImport, Guid("A668B8F2-BA87-4F63-9D41-768F7DE9C50E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ILavAudioStatus
{
    [PreserveSig] int IsSampleFormatSupported(int format);
    [PreserveSig] int GetDecodeDetails(out IntPtr codec, out IntPtr decodeFormat, out int channels, out int sampleRate, out uint channelMask);
    [PreserveSig] int GetOutputDetails(out IntPtr outputFormat, out int channels, out int sampleRate, out uint channelMask);
}

internal static partial class NativeMethods
{
    [LibraryImport("ole32")]
    public static partial int CoInitializeEx(IntPtr reserved, uint coInit);

    [LibraryImport("ole32")]
    public static partial void CoTaskMemFree(IntPtr pointer);

    [LibraryImport("oleaut32")]
    public static partial void SysFreeString(IntPtr bstr);

    [LibraryImport("user32", StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr CreateWindowExW(uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [LibraryImport("user32")]
    public static partial int GetMessageW(out Msg message, IntPtr window, uint min, uint max);

    [LibraryImport("user32")]
    public static partial int TranslateMessage(ref Msg message);

    [LibraryImport("user32")]
    public static partial IntPtr DispatchMessageW(ref Msg message);

    [LibraryImport("user32")]
    public static partial int PostThreadMessageW(uint threadId, uint message, IntPtr wParam, IntPtr lParam);

    [LibraryImport("kernel32")]
    public static partial uint GetCurrentThreadId();

    [LibraryImport("user32")]
    public static partial int PeekMessageW(out Msg message, IntPtr window, uint min, uint max, uint remove);

    public const uint CoinitApartmentThreaded = 2;
    public const uint WmApp = 0x8000;
    public const uint WmQuit = 0x0012;
}

[StructLayout(LayoutKind.Sequential)]
internal struct Msg
{
    public IntPtr Window;
    public uint Message;
    public IntPtr WParam;
    public IntPtr LParam;
    public uint Time;
    public int X;
    public int Y;
}
