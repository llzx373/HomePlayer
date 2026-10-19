using System.Runtime.InteropServices;
using HomePlayer.Core;

namespace HomePlayer.Playback;

public sealed class LavPlaybackEngine : IPlaybackEngine
{
    private readonly StaThread _sta = new("LAV playback");
    private readonly List<object> _com = [];
    private BgraSinkFilter? _sink;
    private IMediaControl? _control;
    private IMediaSeeking? _seeking;
    private IBasicAudio? _audio;
    private IFilterGraph2? _graph;
    private object? _splitter;
    private object? _video;
    private string _path = "";
    private bool _muted;
    private double _volume = 0.8;
    private TimeSpan _duration;
    private TimeSpan _seeked;
    private int _open;

    public string Name => "LAV";
    public bool IsOpen => _open == 1;
    public bool IsPlaying { get; private set; }
    public TimeSpan Duration => _duration;
    public TimeSpan Position => _sta.Invoke(ReadPosition);

    public event Action<VideoFrame>? FrameArrived;
    public event Action? Ended;

    public void Open(string path)
    {
        _sta.Invoke(() => OpenGraph(path));
        _open = 1;
    }

    public void Play() => _sta.Invoke(() =>
    {
        Hr.Throw(_control?.Run() ?? Hr.Fail, "LAV 开始播放");
        IsPlaying = true;
    });

    public void Pause() => _sta.Invoke(() =>
    {
        _control?.Pause();
        IsPlaying = false;
    });

    public void Stop() => _sta.Invoke(StopGraph);

    public void Seek(TimeSpan position) => _sta.Invoke(() =>
    {
        if (position < TimeSpan.Zero)
            position = TimeSpan.Zero;
        _seeked = position;
        _sink?.ClearPresented();
        if (_seeking == null)
            return;
        var current = position.Ticks;
        long stop = 0;
        _seeking.SetPositions(ref current, 1, ref stop, 0);
    });

    public void SetVolume(double linear)
    {
        _volume = Math.Clamp(linear, 0, 1);
        if (!_muted)
            _sta.Invoke(() => _audio?.put_Volume(ToDecibels(_volume)));
    }

    public void SetMuted(bool muted)
    {
        _muted = muted;
        _sta.Invoke(() => _audio?.put_Volume(muted ? -10000 : ToDecibels(_volume)));
    }

    public PipelineInfo GetPipeline() => _sta.Invoke(ReadPipeline);

    public void Dispose()
    {
        try
        {
            Stop();
        }
        catch
        {
        }

        _sta.Dispose();
    }

    private void OpenGraph(string path)
    {
        CloseGraph();
        _path = path;
        _graph = Create<IFilterGraph2>(DsGuid.FilterGraph);
        _control = (IMediaControl)_graph;
        _seeking = (IMediaSeeking)_graph;
        _audio = (IBasicAudio)_graph;
        _splitter = CreateFilter(DsGuid.LavSplitterSource, "未安装 LAV Splitter。");
        Hr.Throw(((IFileSourceFilter)_splitter).Load(path, 0), "LAV Splitter 无法打开该文件");
        Hr.Throw(_graph.AddFilter((IBaseFilter)_splitter, "LAV Splitter Source"), "加入 LAV Splitter");
        _video = CreateFilter(DsGuid.LavVideo, "未安装 LAV Video Decoder。");
        var audioDecoder = CreateFilter(DsGuid.LavAudio, "未安装 LAV Audio Decoder。");
        Hr.Throw(_graph.AddFilter((IBaseFilter)_video, "LAV Video Decoder"), "加入 LAV Video");
        Hr.Throw(_graph.AddFilter((IBaseFilter)audioDecoder, "LAV Audio Decoder"), "加入 LAV Audio");
        ConfigureVideo(_video);
        ConfigureAudio(audioDecoder);

        var splitterVideo = FindPin((IBaseFilter)_splitter, output: true, video: true)
            ?? throw new InvalidOperationException("LAV Splitter 没有视频输出。");
        var videoIn = FindPin((IBaseFilter)_video, output: false, video: false)
            ?? throw new InvalidOperationException("LAV Video 没有输入脚。");
        Connect(_graph, splitterVideo, videoIn, "连接 LAV Video");

        var splitterAudio = FindPin((IBaseFilter)_splitter, output: true, video: false);
        if (splitterAudio != null)
        {
            var audioIn = FindPin((IBaseFilter)audioDecoder, output: false, video: false);
            var audioOut = FindPin((IBaseFilter)audioDecoder, output: true, video: false);
            if (audioIn != null && audioOut != null && !Hr.Failed(_graph.Connect(splitterAudio, audioIn)))
            {
                var sound = CreateFilter(DsGuid.DirectSound, "无法创建 DirectSound 渲染器。");
                if (!Hr.Failed(_graph.AddFilter((IBaseFilter)sound, "DirectSound")))
                {
                    var soundIn = FindPin((IBaseFilter)sound, output: false, video: false);
                    if (soundIn != null)
                        _graph.Connect(audioOut, soundIn);
                }
            }
        }

        _sink = new BgraSinkFilter();
        _sink.FrameArrived += frame => FrameArrived?.Invoke(frame);
        _sink.Ended += () =>
        {
            IsPlaying = false;
            Ended?.Invoke();
        };
        AddSink(_graph, _sink);
        var videoOut = FindPin((IBaseFilter)_video, output: true, video: true)
            ?? throw new InvalidOperationException("LAV Video 没有输出脚。");
        ConnectSink(_graph, videoOut, _sink);
        _graph.SetDefaultSyncSource();
        if (!Hr.Failed(_seeking.GetDuration(out var duration)))
            _duration = TimeSpan.FromTicks(duration);
        _audio.put_Volume(_muted ? -10000 : ToDecibels(_volume));
        var length = new FileInfo(path).Length;
        _ = length;
    }

    private void StopGraph()
    {
        try
        {
            _control?.Stop();
        }
        catch
        {
        }

        IsPlaying = false;
    }

    private void CloseGraph()
    {
        StopGraph();
        foreach (var com in _com)
        {
            try
            {
                Marshal.ReleaseComObject(com);
            }
            catch
            {
            }
        }

        _com.Clear();
        _graph = null;
        _control = null;
        _seeking = null;
        _audio = null;
        _splitter = null;
        _video = null;
        _sink = null;
        _duration = TimeSpan.Zero;
    }

    private TimeSpan ReadPosition()
    {
        if (_sink is { HasPresented: true } sink)
        {
            var presented = sink.PresentedPosition;
            if (presented >= TimeSpan.Zero && (_duration <= TimeSpan.Zero || presented <= _duration))
                return presented;
        }

        return _seeked;
    }

    private PipelineInfo ReadPipeline()
    {
        var filters = new List<string>();
        var connections = new List<string>();
        string? videoCodec = null;
        string? pixel = _sink?.PixelFormat;
        int? width = _sink?.Width;
        int? height = _sink?.Height;
        double? fps = null;
        string? audioCodec = null;
        int? channels = null;
        int? sampleRate = null;
        var audioTracks = new List<string>();
        var subtitles = new List<string>();
        if (_graph != null && !Hr.Failed(_graph.EnumFilters(out var enumerator)))
        {
            var filterSlot = Marshal.AllocCoTaskMem(IntPtr.Size);
            var fetchedSlot = Marshal.AllocCoTaskMem(sizeof(int));
            try
            {
            while (enumerator.Next(1, filterSlot, fetchedSlot) == Hr.Ok && Marshal.ReadInt32(fetchedSlot) == 1)
            {
                var filterPointer = Marshal.ReadIntPtr(filterSlot);
                var unknown = Marshal.GetObjectForIUnknown(filterPointer);
                Marshal.Release(filterPointer);
                if (unknown is BgraSinkFilter)
                {
                    filters.Add("HomePlayer Video Sink");
                    continue;
                }

                var filter = (IBaseFilter)unknown;
                if (filter.QueryFilterInfo(out var info) >= 0)
                {
                    filters.Add(string.IsNullOrWhiteSpace(info.Name) ? "未命名滤镜" : info.Name);
                    if (info.Graph != 0)
                        Marshal.Release(info.Graph);
                }

                if (!Hr.Failed(filter.EnumPins(out var pins)))
                {
                    var pinSlot = Marshal.AllocCoTaskMem(IntPtr.Size);
                    try
                    {
                    while (pins.Next(1, pinSlot, fetchedSlot) == Hr.Ok && Marshal.ReadInt32(fetchedSlot) == 1)
                    {
                        var pinPointer = Marshal.ReadIntPtr(pinSlot);
                        var pin = (IPin)Marshal.GetObjectForIUnknown(pinPointer);
                        Marshal.Release(pinPointer);
                        if (pin.QueryDirection(out var direction) < 0 || direction != 1)
                        {
                            Marshal.ReleaseComObject(pin);
                            continue;
                        }
                        if (pin.ConnectionMediaType(out var type) < 0)
                        {
                            Marshal.ReleaseComObject(pin);
                            continue;
                        }
                        connections.Add(Describe(type, ref videoCodec, ref pixel, ref width, ref height, ref fps, ref audioCodec, ref channels, ref sampleRate));
                        Free(type);
                        Marshal.ReleaseComObject(pin);
                    }
                    }
                    finally
                    {
                        Marshal.FreeCoTaskMem(pinSlot);
                        Marshal.ReleaseComObject(pins);
                    }
                }

                Marshal.ReleaseComObject(filter);
            }
            }
            finally
            {
                Marshal.FreeCoTaskMem(filterSlot);
                Marshal.FreeCoTaskMem(fetchedSlot);
                Marshal.ReleaseComObject(enumerator);
            }
        }

        filters.Reverse();
        ReadStreams(audioTracks, subtitles);
        var decoder = QueryString(_video, filter =>
        {
            if (filter is not ILavVideoStatus status)
                return null;
            var pointer = status.GetActiveDecoderName();
            return pointer == 0 ? null : Marshal.PtrToStringUni(pointer);
        });
        var device = QueryString(_video, filter =>
        {
            if (filter is not ILavVideoStatus status || status.GetHWAccelActiveDevice(out var name) < 0 || name == 0)
                return null;
            var text = Marshal.PtrToStringBSTR(name);
            NativeMethods.SysFreeString(name);
            return text;
        });
        var audioDecoder = QueryString(_com.OfType<object>().FirstOrDefault(item => item is ILavAudioStatus), filter =>
        {
            if (filter is not ILavAudioStatus status || status.GetDecodeDetails(out var codec, out _, out var decodedChannels, out var rate, out _) < 0)
                return null;
            channels ??= decodedChannels;
            sampleRate ??= rate;
            return codec == 0 ? null : Marshal.PtrToStringAnsi(codec);
        });

        long? size = File.Exists(_path) ? new FileInfo(_path).Length : null;
        double? bitrate = size is long bytes && _duration.TotalSeconds > 0 ? bytes * 8d / _duration.TotalSeconds / 1_000_000d : null;
        return new PipelineInfo
        {
            FilePath = _path,
            Engine = "LAV",
            Container = Path.GetExtension(_path).TrimStart('.').ToUpperInvariant(),
            DurationSeconds = _duration.TotalSeconds,
            FileSize = size,
            BitrateMbps = bitrate,
            VideoCodec = videoCodec,
            VideoDecoder = decoder ?? "LAV Video Decoder",
            Width = width,
            Height = height,
            FrameRate = fps,
            PixelFormat = pixel,
            Hardware = string.IsNullOrWhiteSpace(device) ? "未使用" : device,
            AudioCodec = audioCodec ?? audioDecoder,
            AudioDecoder = audioDecoder == null ? null : "LAV Audio Decoder / " + audioDecoder,
            Channels = channels,
            SampleRate = sampleRate,
            AudioTracks = audioTracks,
            SubtitleTracks = subtitles,
            Filters = filters,
            Connections = connections
        };
    }

    private void ReadStreams(List<string> audio, List<string> subtitles)
    {
        if (_splitter is not IamStreamSelect select || select.Count(out var count) < 0)
            return;
        for (var i = 0; i < count; i++)
        {
            var slot = Marshal.AllocCoTaskMem(IntPtr.Size);
            try
            {
                if (select.Info(i, slot, out var flags, out _, out _, out var namePtr, out var obj, out var unk) < 0)
                    continue;
                var name = namePtr == 0 ? $"流 {i + 1}" : Marshal.PtrToStringUni(namePtr) ?? $"流 {i + 1}";
                if (namePtr != 0)
                    NativeMethods.CoTaskMemFree(namePtr);
                var major = Guid.Empty;
                var typePtr = Marshal.ReadIntPtr(slot);
                if (typePtr != 0)
                {
                    major = Marshal.PtrToStructure<AmMediaType>(typePtr).MajorType;
                    FreeType(typePtr);
                }

                var enabled = (flags & 1) != 0 ? "当前" : "未使用";
                var line = $"{enabled}：{name}";
                if (major == DsGuid.Audio)
                    audio.Add(line);
                else if (major != DsGuid.Video)
                    subtitles.Add(line);
                if (obj != 0)
                    Marshal.Release(obj);
                if (unk != 0)
                    Marshal.Release(unk);
            }
            finally
            {
                Marshal.FreeCoTaskMem(slot);
            }
        }
    }

    private static string Describe(AmMediaType type, ref string? videoCodec, ref string? pixel, ref int? width, ref int? height, ref double? fps, ref string? audioCodec, ref int? channels, ref int? sampleRate)
    {
        if (type.MajorType == DsGuid.Video)
        {
            videoCodec ??= FourCC(type.SubType);
            pixel ??= FourCC(type.SubType);
            var header = type.FormatType == DsGuid.FormatVideoInfo2 ? 72 : 48;
            if (type.Format != 0 && type.FormatSize >= header + 16)
            {
                width = Math.Abs(Marshal.ReadInt32(type.Format, header + 4));
                height = Math.Abs(Marshal.ReadInt32(type.Format, header + 8));
                if (header >= 48)
                {
                    var frame = Marshal.ReadInt64(type.Format, 40);
                    if (frame > 0)
                        fps = 10_000_000d / frame;
                }
            }

            return $"视频 {videoCodec} {width}×{height} {(fps is double rate ? rate.ToString("0.###") + " fps" : "")}".Trim();
        }

        if (type.MajorType == DsGuid.Audio && type.Format != 0 && type.FormatSize >= 16)
        {
            channels = Marshal.ReadInt16(type.Format, 2);
            sampleRate = Marshal.ReadInt32(type.Format, 4);
            audioCodec ??= "0x" + Marshal.ReadInt16(type.Format, 0).ToString("X");
            return $"音频 {audioCodec} {channels} 声道 {sampleRate} Hz";
        }

        return FourCC(type.SubType);
    }

    private static string FourCC(Guid subtype)
    {
        var bytes = subtype.ToByteArray();
        if (bytes[4] == 0 && bytes[5] == 0 && bytes[6] == 0x10)
        {
            var text = System.Text.Encoding.ASCII.GetString(bytes, 0, 4).Trim('\0', ' ');
            if (text.All(ch => ch >= 32 && ch < 127))
                return text;
        }

        return subtype == DsGuid.Rgb32 ? "RGB32" : subtype.ToString();
    }

    private static void Free(AmMediaType type)
    {
        if (type.Format != 0)
            NativeMethods.CoTaskMemFree(type.Format);
        if (type.Unk != 0)
            Marshal.Release(type.Unk);
    }

    private static void FreeType(IntPtr type)
    {
        var media = Marshal.PtrToStructure<AmMediaType>(type);
        Free(media);
        NativeMethods.CoTaskMemFree(type);
    }

    private T Create<T>(Guid clsid) where T : class
    {
        var instance = CreateRaw(clsid, "无法创建 DirectShow 对象。");
        return (T)instance;
    }

    private object CreateFilter(Guid clsid, string error)
    {
        try
        {
            return CreateRaw(clsid, error);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or FileNotFoundException)
        {
            throw new InvalidOperationException(error, ex);
        }
    }

    private object CreateRaw(Guid clsid, string error)
    {
        var type = Type.GetTypeFromCLSID(clsid, throwOnError: false) ?? throw new InvalidOperationException(error);
        var instance = Activator.CreateInstance(type) ?? throw new InvalidOperationException(error);
        _com.Add(instance);
        return instance;
    }

    private static void ConfigureVideo(object filter)
    {
        if (filter is not ILavVideoSettings settings)
            return;
        settings.SetRuntimeConfig(1);
        const int rgb32 = 11;
        for (var format = 0; format < 24; format++)
            settings.SetPixelFormat(format, format == rgb32 ? 1 : 0);
    }

    private static void ConfigureAudio(object filter)
    {
        if (filter is not ILavAudioSettings settings)
            return;
        settings.SetRuntimeConfig(1);
        for (var codec = 0; codec < 5; codec++)
            settings.SetBitstreamConfig(codec, 0);
    }

    private static IPin? FindPin(IBaseFilter filter, bool output, bool video)
    {
        if (Hr.Failed(filter.EnumPins(out var enumerator)))
            return null;
        var slot = Marshal.AllocCoTaskMem(IntPtr.Size);
        var fetched = Marshal.AllocCoTaskMem(sizeof(int));
        try
        {
            while (enumerator.Next(1, slot, fetched) == Hr.Ok && Marshal.ReadInt32(fetched) == 1)
            {
                var pointer = Marshal.ReadIntPtr(slot);
                var pin = (IPin)Marshal.GetObjectForIUnknown(pointer);
                Marshal.Release(pointer);
                if (pin.QueryDirection(out var direction) < 0 || (direction == 1) != output)
                {
                    Marshal.ReleaseComObject(pin);
                    continue;
                }

                if (!output)
                    return pin;
                if (Major(pin) == (video ? DsGuid.Video : DsGuid.Audio))
                    return pin;
                Marshal.ReleaseComObject(pin);
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(slot);
            Marshal.FreeCoTaskMem(fetched);
            Marshal.ReleaseComObject(enumerator);
        }

        return null;
    }

    private static Guid Major(IPin pin)
    {
        if (Hr.Failed(pin.EnumMediaTypes(out var enumerator)))
            return Guid.Empty;
        var slot = Marshal.AllocCoTaskMem(IntPtr.Size);
        try
        {
            if (enumerator.Next(1, slot, 0) != Hr.Ok)
                return Guid.Empty;
            var type = Marshal.ReadIntPtr(slot);
            if (type == 0)
                return Guid.Empty;
            var major = Marshal.PtrToStructure<AmMediaType>(type).MajorType;
            FreeType(type);
            return major;
        }
        finally
        {
            Marshal.FreeCoTaskMem(slot);
            Marshal.ReleaseComObject(enumerator);
        }
    }

    private static void Connect(IFilterGraph2 graph, IPin output, IPin input, string action)
    {
        var direct = graph.ConnectDirect(output, input, 0);
        if (!Hr.Failed(direct))
            return;
        Hr.Throw(graph.Connect(output, input), action);
    }

    private static void AddSink(IFilterGraph2 graph, BgraSinkFilter sink)
    {
        var graphPointer = Marshal.GetComInterfaceForObject(graph, typeof(IFilterGraph2));
        var filterPointer = Marshal.GetComInterfaceForObject(sink, typeof(IBaseFilterSink));
        try
        {
            var function = Vtable<AddFilterDelegate>(graphPointer, 3);
            Hr.Throw(function(graphPointer, filterPointer, "HomePlayer Video Sink"), "加入视频渲染");
        }
        finally
        {
            Marshal.Release(graphPointer);
        }
    }

    private static void ConnectSink(IFilterGraph2 graph, IPin output, BgraSinkFilter sink)
    {
        var graphPointer = Marshal.GetComInterfaceForObject(graph, typeof(IFilterGraph2));
        var outputPointer = Marshal.GetComInterfaceForObject(output, typeof(IPin));
        var inputPointer = sink.PinPointer;
        try
        {
            var direct = Vtable<ConnectDirectDelegate>(graphPointer, 7);
            if (!Hr.Failed(direct(graphPointer, outputPointer, inputPointer, 0)))
                return;
            var connect = Vtable<ConnectDelegate>(graphPointer, 11);
            Hr.Throw(connect(graphPointer, outputPointer, inputPointer), "连接视频渲染");
        }
        finally
        {
            Marshal.Release(outputPointer);
            Marshal.Release(graphPointer);
        }
    }

    private static T Vtable<T>(IntPtr instance, int slot) where T : Delegate
    {
        var table = Marshal.ReadIntPtr(instance);
        return Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(table, slot * IntPtr.Size));
    }

    private static string? QueryString(object? filter, Func<object, string?> read)
    {
        try
        {
            return filter == null ? null : read(filter);
        }
        catch
        {
            return null;
        }
    }

    private static int ToDecibels(double linear)
    {
        if (linear <= 0.0001)
            return -10000;
        return Math.Clamp((int)(Math.Log10(linear) * 2000), -10000, 0);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private delegate int AddFilterDelegate(IntPtr self, IntPtr filter, string name);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ConnectDirectDelegate(IntPtr self, IntPtr output, IntPtr input, IntPtr mediaType);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ConnectDelegate(IntPtr self, IntPtr output, IntPtr input);
}
