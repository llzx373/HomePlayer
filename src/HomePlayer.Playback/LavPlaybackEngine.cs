using System.Globalization;
using System.Runtime.InteropServices;
using HomePlayer.Core;

namespace HomePlayer.Playback;

public sealed class LavPlaybackEngine : IPlaybackEngine, IPlaybackAudioEnhancements
{
    private readonly StaThread _sta = new("LAV playback");
    private bool _normalize;
    private bool _nightMode;
    private readonly List<object> _com = [];
    private BgraSinkFilter? _sink;
    private IMediaControl? _control;
    private IMediaSeeking? _seeking;
    private IBasicAudio? _audio;
    private IFilterGraph2? _graph;
    private readonly CodecPlan _plan;
    private readonly string? _audioOutputId;
    private object? _splitter;
    private object? _video;
    private object? _audioFilter;
    private InstalledCodec? _splitterCodec;
    private InstalledCodec? _videoCodec;
    private InstalledCodec? _audioCodec;
    private string? _audioWarning;
    private bool _audioMissing;
    private string? _audioDevice;
    private string _path = "";
    private bool _muted;
    private double _volume = 0.8;
    private double _rate = 1.0;
    private TimeSpan _duration;
    private long _seekedTicks;
    private volatile bool _isPlaying;
    private int _open;
    private const int StreamSelectEnable = 1;

    public LavPlaybackEngine(CodecPlan plan, string? audioOutputId = null)
    {
        _plan = plan;
        _audioOutputId = audioOutputId;
    }

    public string? AudioWarning => _audioWarning;
    public bool AudioMissing => _audioMissing;
    public string Name => "LAV";
    public bool IsOpen => _open == 1;
    public bool IsPlaying => _isPlaying;
    public TimeSpan Duration => _duration;
    public TimeSpan Position => ReadPosition();
    public double Rate => _rate;

    public event Action<FrameLease>? FrameArrived;
    public event Action? Ended;

    public void Open(string path)
    {
        _sta.Invoke(() => OpenGraph(path));
        _open = 1;
    }

    public void Play() => _sta.Invoke(() =>
    {
        Hr.Throw(_control?.Run() ?? Hr.Fail, "LAV 开始播放");
        _isPlaying = true;
    });

    public void Pause()
    {
        _isPlaying = false;
        _sta.Post(() =>
        {
            try
            {
                _control?.Pause();
                _control?.GetState(400, out _);
            }
            catch
            {
            }

            _isPlaying = false;
        });
    }

    public void Stop()
    {
        _isPlaying = false;
        _sta.Post(StopGraph);
    }

    public void Seek(TimeSpan position) => _sta.Invoke(() => SeekCore(position));

    private void SeekCore(TimeSpan position)
    {
        if (position < TimeSpan.Zero)
            position = TimeSpan.Zero;
        Volatile.Write(ref _seekedTicks, position.Ticks);
        _sink?.ClearPresented();
        if (_seeking == null)
            return;
        var paused = _sink is { Paused: true };
        if (paused)
            _sink?.RequestPausedFrame(position.Ticks);
        var current = position.Ticks;
        long stop = 0;
        _seeking.SetPositions(ref current, 1, ref stop, 0);
        if (paused)
            RenderPausedFrame();
    }

    private void RenderPausedFrame()
    {
        if (_control == null)
            return;
        var muted = _muted;
        if (!muted)
            _audio?.put_Volume(-10000);
        try
        {
            _control.Run();
            _sink?.WaitForPausedFrame(500);
            _control.Pause();
        }
        finally
        {
            if (!muted)
                _audio?.put_Volume(ToDecibels(_volume));
        }
    }

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

    public void SetRate(double rate) => _sta.Invoke(() => ApplyRate(PlaybackRates.Clamp(rate)));

    public void SetAudioDelay(int milliseconds) => _sta.Invoke(() =>
    {
        if (_audioFilter is ILavAudioSettings settings)
            settings.SetAudioDelay(milliseconds == 0 ? 0 : 1, Math.Clamp(milliseconds, -10_000, 10_000));
    });

    public void SetNormalization(bool enabled)
    {
        _normalize = enabled;
        ApplyDynamicRange();
    }

    public void SetNightMode(bool enabled)
    {
        _nightMode = enabled;
        ApplyDynamicRange();
    }

    private void ApplyDynamicRange() => _sta.Invoke(() =>
    {
        if (_audioFilter is ILavAudioSettings settings)
            settings.SetDRC(_normalize || _nightMode ? 1 : 0, _nightMode ? 100 : 50);
    });

    public void StepFrame() => _sta.Invoke(() =>
    {
        _control?.Pause();
        _isPlaying = false;
        if (_seeking == null)
            return;
        var fps = ReadPipeline().FrameRate is double rate && rate > 0 ? rate : 24;
        var position = ReadPosition() + TimeSpan.FromSeconds(1 / fps);
        if (_duration > TimeSpan.Zero && position > _duration)
            position = _duration;
        if (position <= ReadPosition())
            return;
        SeekCore(position);
    });

    public PipelineInfo GetPipeline() => _sta.Invoke(ReadPipeline);

    public IReadOnlyList<MediaTrack> GetAudioTracks() => _sta.Invoke(ReadAudioTracks);

    public int AudioTrackIndex => _sta.Invoke(SelectedAudioIndex);

    public void SetAudioTrack(int index) => _sta.Invoke(() => EnableAudio(index));

    public IReadOnlyList<MediaTrack> GetSubtitleTracks() => _sta.Invoke(ReadSubtitleTracks);

    public int SubtitleTrackIndex => -1;

    public void SetSubtitleTrack(int index) => _ = index;

    public void Dispose()
    {
        try
        {
            _sta.TryInvoke(CloseGraph, 800);
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
        _audioWarning = null;
        _audioMissing = false;
        _audioDevice = AudioOutputs.Describe(_audioOutputId);
        _graph = Create<IFilterGraph2>(DsGuid.FilterGraph);
        _control = (IMediaControl)_graph;
        _seeking = (IMediaSeeking)_graph;
        _audio = (IBasicAudio)_graph;
        _splitter = OpenSplitter(path);
        var splitterVideo = FindPin((IBaseFilter)_splitter, output: true, video: true)
            ?? throw new InvalidOperationException("分离器没有视频输出。");
        try
        {
            _video = ConnectVideo(splitterVideo);
        }
        finally
        {
            ReleasePin(splitterVideo);
        }
        ConnectAudio();
        _graph.SetDefaultSyncSource();
        if (!Hr.Failed(_seeking.GetDuration(out var duration)))
            _duration = TimeSpan.FromTicks(duration);
        _audio.put_Volume(_muted ? -10000 : ToDecibels(_volume));
        ApplyRate(_rate);
    }

    private object OpenSplitter(string path)
    {
        var errors = new List<string>();
        foreach (var codec in _plan.Splitters)
        {
            object filter;
            try
            {
                filter = CreateFilter(codec.Clsid, codec.Name);
            }
            catch (Exception ex)
            {
                errors.Add($"{codec.Name}：{ex.Message}");
                continue;
            }

            if (Hr.Failed(_graph!.AddFilter((IBaseFilter)filter, Trim(codec.Name))))
            {
                errors.Add($"{codec.Name}：无法加入滤镜图");
                continue;
            }

            if (filter is IFileSourceFilter source)
            {
                var loaded = source.Load(path, 0);
                if (!Hr.Failed(loaded) && HasVideoPin(filter))
                {
                    _splitterCodec = codec;
                    return filter;
                }

                errors.Add(Hr.Failed(loaded)
                    ? $"{codec.Name}：打不开文件（0x{loaded:X8}）"
                    : $"{codec.Name}：打开了文件，但没有视频输出");
                Remove(filter);
                continue;
            }

            object? reader = null;
            try
            {
                reader = CreateFilter(DsGuid.AsyncReader, "File Source");
                if (Hr.Failed(_graph.AddFilter((IBaseFilter)reader, "File Source (Async.)"))
                    || reader is not IFileSourceFilter file)
                {
                    errors.Add($"{codec.Name}：无法创建文件源");
                    Remove(filter);
                    if (reader != null)
                        Remove(reader);
                    continue;
                }

                var opened = file.Load(path, 0);
                var readerOut = FindPin((IBaseFilter)reader, output: true, video: false, anyOutput: true);
                var splitterIn = FindPin((IBaseFilter)filter, output: false, video: false);
                var linked = readerOut != null && splitterIn != null ? _graph.Connect(readerOut, splitterIn) : Hr.Fail;
                ReleasePin(readerOut);
                ReleasePin(splitterIn);
                if (!Hr.Failed(opened) && !Hr.Failed(linked) && HasVideoPin(filter))
                {
                    _splitterCodec = codec;
                    return filter;
                }

                errors.Add($"{codec.Name}：文件源没有接到分离器（0x{linked:X8}）");
            }
            catch (Exception ex)
            {
                errors.Add($"{codec.Name}：{ex.Message}");
            }

            Remove(filter);
            if (reader != null)
                Remove(reader);
        }

        var detail = errors.Count == 0 ? "没有启用的 64 位分离器。" : string.Join("；", errors);
        throw new InvalidOperationException("分离器都无法打开该文件。" + detail);
    }

    private object ConnectVideo(IPin splitterVideo)
    {
        var errors = new List<string>();
        foreach (var codec in _plan.VideoDecoders)
        {
            object filter;
            try
            {
                filter = CreateFilter(codec.Clsid, codec.Name);
            }
            catch (Exception ex)
            {
                errors.Add($"{codec.Name}：{ex.Message}");
                continue;
            }

            if (Hr.Failed(_graph!.AddFilter((IBaseFilter)filter, Trim(codec.Name))))
            {
                errors.Add($"{codec.Name}：无法加入滤镜图");
                continue;
            }

            if (codec.Clsid == DsGuid.LavVideo)
                ConfigureVideo(filter);
            var input = FindPin((IBaseFilter)filter, output: false, video: false);
            var connected = input == null ? Hr.Fail : _graph.Connect(splitterVideo, input);
            ReleasePin(input);
            if (Hr.Failed(connected))
            {
                Disconnect(splitterVideo);
                Remove(filter);
                errors.Add($"{codec.Name}：接不上视频（0x{connected:X8}）");
                continue;
            }

            var output = FindPin((IBaseFilter)filter, output: true, video: true, anyOutput: true);
            if (output == null)
            {
                Disconnect(splitterVideo);
                Remove(filter);
                errors.Add($"{codec.Name}：没有视频输出脚");
                continue;
            }

            try
            {
                EnsureSink();
                ConnectSink(_graph, output, _sink!);
                ReleasePin(output);
                _videoCodec = codec;
                return filter;
            }
            catch (Exception ex)
            {
                ReleasePin(output);
                Disconnect(splitterVideo);
                Remove(filter);
                errors.Add($"{codec.Name}：{ex.Message}");
            }
        }

        var detail = errors.Count == 0 ? "没有启用的 64 位视频解码器。" : string.Join("；", errors);
        throw new InvalidOperationException("视频解码器都无法连接。" + detail);
    }

    private void ConnectAudio()
    {
        var splitterAudio = _splitter == null ? null : FindPin((IBaseFilter)_splitter, output: true, video: false);
        if (splitterAudio == null)
        {
            _audioWarning = "这条视频没有音频输出。";
            _audioMissing = true;
            return;
        }

        var errors = new List<string>();
        foreach (var codec in _plan.AudioDecoders)
        {
            object filter;
            try
            {
                filter = CreateFilter(codec.Clsid, codec.Name);
            }
            catch (Exception ex)
            {
                errors.Add($"{codec.Name}：{ex.Message}");
                continue;
            }

            if (Hr.Failed(_graph!.AddFilter((IBaseFilter)filter, Trim(codec.Name))))
            {
                errors.Add($"{codec.Name}：无法加入滤镜图");
                continue;
            }

            if (codec.Clsid == DsGuid.LavAudio)
                ConfigureAudio(filter);
            var input = FindPin((IBaseFilter)filter, output: false, video: false);
            var connected = input == null ? Hr.Fail : _graph.Connect(splitterAudio, input);
            ReleasePin(input);
            if (Hr.Failed(connected))
            {
                Disconnect(splitterAudio);
                Remove(filter);
                errors.Add($"{codec.Name}：接不上音频（0x{connected:X8}）");
                continue;
            }

            var output = FindPin((IBaseFilter)filter, output: true, video: false, anyOutput: true);
            var rendered = output == null ? Hr.Fail : ConnectSound(output);
            var linked = output != null && IsConnected(output);
            ReleasePin(output);
            if (!Hr.Failed(rendered) && linked)
            {
                _audioFilter = filter;
                _audioCodec = codec;
                ReleasePin(splitterAudio);
                return;
            }

            Disconnect(splitterAudio);
            Remove(filter);
            errors.Add($"{codec.Name}：无法接到音频设备（0x{rendered:X8}）");
        }

        var fallback = ConnectSound(splitterAudio);
        ReleasePin(splitterAudio);
        var detail = errors.Count == 0 ? "没有启用音频解码器。" : string.Join("；", errors);
        if (Hr.Failed(fallback))
        {
            _audioMissing = true;
            _audioWarning = "音频没有接上。" + detail;
            return;
        }

        _audioWarning = "选用的音频解码器都没接上，已改用系统默认音频链路。" + detail;
    }

    private int ConnectSound(IPin output)
    {
        var renderer = AudioOutputs.CreateRenderer(_audioOutputId, out var name);
        if (renderer != null)
        {
            _com.Add(renderer);
            if (!Hr.Failed(_graph!.AddFilter((IBaseFilter)renderer, Trim(name))))
            {
                var input = FindPin((IBaseFilter)renderer, output: false, video: false);
                var connected = input == null ? Hr.Fail : _graph.Connect(output, input);
                ReleasePin(input);
                if (!Hr.Failed(connected))
                {
                    _audioDevice = name;
                    return connected;
                }

                Remove(renderer);
            }
        }

        var rendered = _graph!.Render(output);
        if (!Hr.Failed(rendered) && !string.IsNullOrWhiteSpace(_audioOutputId))
            _audioDevice = AudioOutputs.Describe(null) + "（所选设备没接上）";
        else if (!Hr.Failed(rendered))
            _audioDevice = AudioOutputs.Describe(null);
        return rendered;
    }

    private void EnsureSink()
    {
        if (_sink != null)
            return;
        _sink = new BgraSinkFilter();
        _sink.FrameArrived += frame =>
        {
            var handler = FrameArrived;
            if (handler == null)
            {
                frame.ReturnBuffer();
                return;
            }

            handler(frame);
        };
        _sink.Ended += () =>
        {
            _isPlaying = false;
            Ended?.Invoke();
        };
        AddSink(_graph!, _sink);
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

        _isPlaying = false;
    }

    private void CloseGraph()
    {
        StopGraph();
        Volatile.Write(ref _seekedTicks, 0);
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
        _audioFilter = null;
        _splitterCodec = null;
        _videoCodec = null;
        _audioCodec = null;
        _audioMissing = false;
        _audioDevice = null;
        _sink = null;
        _duration = TimeSpan.Zero;
        _open = 0;
    }

    private void ApplyRate(double rate)
    {
        rate = PlaybackRates.Clamp(rate);
        if (_seeking == null)
        {
            _rate = rate;
            return;
        }

        if (Hr.Failed(_seeking.SetRate(rate)))
        {
            _rate = 1.0;
            return;
        }

        _rate = !Hr.Failed(_seeking.GetRate(out var actual)) && actual > 0 ? actual : rate;
    }

    private TimeSpan ReadPosition()
    {
        if (_sink is { HasPresented: true } sink)
        {
            var presented = sink.PresentedPosition;
            if (presented >= TimeSpan.Zero && (_duration <= TimeSpan.Zero || presented <= _duration))
                return presented;
        }

        return TimeSpan.FromTicks(Volatile.Read(ref _seekedTicks));
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
                    object unknown;
                    try
                    {
                        unknown = Marshal.GetObjectForIUnknown(filterPointer);
                    }
                    finally
                    {
                        Marshal.Release(filterPointer);
                    }

                    try
                    {
                        CollectFilter(unknown, filters, connections, fetchedSlot, ref videoCodec, ref pixel, ref width, ref height, ref fps, ref audioCodec, ref channels, ref sampleRate);
                    }
                    catch
                    {
                    }
                }
            }
            finally
            {
                Marshal.FreeCoTaskMem(filterSlot);
                Marshal.FreeCoTaskMem(fetchedSlot);
                ReleaseCom(enumerator);
            }
        }

        filters.Reverse();
        try
        {
            ReadStreams(audioTracks, subtitles);
        }
        catch
        {
        }
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
        var audioDecoder = QueryString(_audioFilter, filter =>
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
            VideoDecoder = DescribeDecoder(decoder, _videoCodec),
            Width = width,
            Height = height,
            FrameRate = fps,
            PixelFormat = pixel,
            Hardware = string.IsNullOrWhiteSpace(device) ? "未使用" : device,
            AudioCodec = audioDecoder ?? audioCodec,
            AudioDecoder = _audioCodec == null
                ? audioDecoder
                : DescribeDecoder(string.IsNullOrWhiteSpace(audioDecoder) ? _audioCodec.Name : $"{_audioCodec.Name} / {audioDecoder}", _audioCodec),
            AudioDevice = _audioDevice,
            Channels = channels,
            SampleRate = sampleRate,
            AudioTracks = audioTracks,
            SubtitleTracks = subtitles,
            Filters = filters,
            Connections = connections,
            Usage = TryBuildUsage()
        };
    }

    private static void CollectFilter(
        object unknown,
        List<string> filters,
        List<string> connections,
        IntPtr fetchedSlot,
        ref string? videoCodec,
        ref string? pixel,
        ref int? width,
        ref int? height,
        ref double? fps,
        ref string? audioCodec,
        ref int? channels,
        ref int? sampleRate)
    {
        if (unknown is IBaseFilter filter)
        {
            CollectNativeFilter(filter, filters, connections, fetchedSlot, ref videoCodec, ref pixel, ref width, ref height, ref fps, ref audioCodec, ref channels, ref sampleRate);
            return;
        }

        if (unknown is IBaseFilterSink sink)
            CollectManagedFilter(sink, filters);
    }

    private static void CollectManagedFilter(IBaseFilterSink sink, List<string> filters)
    {
        if (sink.QueryFilterInfo(out var info) < 0)
        {
            filters.Add("HomePlayer Video Sink");
            return;
        }

        filters.Add(string.IsNullOrWhiteSpace(info.Name) ? "HomePlayer Video Sink" : info.Name);
        if (info.Graph != 0)
            Marshal.Release(info.Graph);
    }

    private static void CollectNativeFilter(
        IBaseFilter filter,
        List<string> filters,
        List<string> connections,
        IntPtr fetchedSlot,
        ref string? videoCodec,
        ref string? pixel,
        ref int? width,
        ref int? height,
        ref double? fps,
        ref string? audioCodec,
        ref int? channels,
        ref int? sampleRate)
    {
        var filterName = "未命名滤镜";
        if (filter.QueryFilterInfo(out var info) >= 0)
        {
            filterName = string.IsNullOrWhiteSpace(info.Name) ? "未命名滤镜" : info.Name;
            filters.Add(filterName);
            if (info.Graph != 0)
                Marshal.Release(info.Graph);
        }

        if (Hr.Failed(filter.EnumPins(out var pins)))
            return;

        var pinSlot = Marshal.AllocCoTaskMem(IntPtr.Size);
        try
        {
            while (pins.Next(1, pinSlot, fetchedSlot) == Hr.Ok && Marshal.ReadInt32(fetchedSlot) == 1)
            {
                var pinPointer = Marshal.ReadIntPtr(pinSlot);
                object pinObject;
                try
                {
                    pinObject = Marshal.GetObjectForIUnknown(pinPointer);
                }
                finally
                {
                    Marshal.Release(pinPointer);
                }

                if (pinObject is not IPin pin)
                {
                    ReleaseCom(pinObject);
                    continue;
                }

                try
                {
                    if (pin.QueryDirection(out var direction) < 0 || direction != 1)
                        continue;
                    if (pin.ConnectionMediaType(out var type) < 0)
                        continue;
                    try
                    {
                        connections.Add($"{filterName} → {PeerName(pin)}：{Describe(type, ref videoCodec, ref pixel, ref width, ref height, ref fps, ref audioCodec, ref channels, ref sampleRate)}");
                    }
                    finally
                    {
                        Free(type);
                    }
                }
                finally
                {
                    ReleaseCom(pin);
                }
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(pinSlot);
            ReleaseCom(pins);
        }
    }

    private List<string> TryBuildUsage()
    {
        try
        {
            return BuildUsage();
        }
        catch
        {
            return [];
        }
    }

    private List<string> BuildUsage()
    {
        var lines = new List<string>
        {
            Environment.Is64BitProcess
                ? "进程是 64 位，只会加载 x64 滤镜。x86 滤镜即使已安装也不会进入这个进程。"
                : "进程是 32 位，只会加载 x86 滤镜。"
        };
        lines.Add(UsageOf("分离器", _splitterCodec, _splitter));
        lines.Add(UsageOf("视频解码", _videoCodec, _video));
        lines.Add(UsageOf("音频解码", _audioCodec, _audioFilter));
        lines.Add("声音出口：" + (_audioDevice ?? AudioOutputs.Describe(_audioOutputId)));
        if (!string.IsNullOrWhiteSpace(_audioWarning))
            lines.Add("音频：" + _audioWarning);
        return lines;
    }

    private static string UsageOf(string role, InstalledCodec? codec, object? filter)
    {
        if (codec == null || filter is not IBaseFilter instance)
            return $"{role}：没有接上";
        var path = CodecCatalog.LoadedModule(codec.Path) ?? codec.Path ?? "未知文件";
        return $"{role}：{codec.Name} {codec.Version ?? "未知版本"} {codec.BitnessText}，{Links(instance)}\n  {path}";
    }

    private static string DescribeDecoder(string? active, InstalledCodec? codec)
    {
        var name = string.IsNullOrWhiteSpace(active) ? codec?.Name : active;
        if (codec == null)
            return name ?? "未知";
        var path = CodecCatalog.LoadedModule(codec.Path) ?? codec.Path;
        return $"{name} · {codec.BitnessText} · {codec.Version ?? "未知版本"} · {path}";
    }

    private static string Links(IBaseFilter filter)
    {
        var parts = new List<string>();
        foreach (var pin in EnumPins(filter, output: true))
        {
            try
            {
                var label = PinLabel(pin);
                var downstream = Downstream(pin);
                parts.Add(downstream == null ? $"{label} 未连接" : $"{label} 已连接到 {downstream}");
            }
            finally
            {
                ReleasePin(pin);
            }
        }

        return parts.Count == 0 ? "没有输出脚" : string.Join("，", parts);
    }

    private static string PeerName(IPin pin) => Downstream(pin) ?? "未连接";

    private static string? Downstream(IPin pin)
    {
        var self = Marshal.GetComInterfaceForObject(pin, typeof(IPin));
        try
        {
            var connected = Vtable<ConnectedToRaw>(self, 6);
            if (connected(self, out var peer) < 0 || peer == 0)
                return null;
            try
            {
                var query = Vtable<QueryPinInfoRaw>(peer, 8);
                if (query(peer, out var info) < 0)
                    return "下游";
                var name = info.Filter == 0 ? "下游" : FilterObjectName(info.Filter);
                if (info.Filter != 0)
                    Marshal.Release(info.Filter);
                return string.IsNullOrWhiteSpace(name) ? "下游" : name;
            }
            finally
            {
                Marshal.Release(peer);
            }
        }
        finally
        {
            Marshal.Release(self);
        }
    }

    private static string FilterObjectName(IntPtr filter)
    {
        var query = Vtable<QueryFilterInfoRaw>(filter, 12);
        if (query(filter, out var info) < 0)
            return "下游";
        if (info.Graph != 0)
            Marshal.Release(info.Graph);
        return info.Name;
    }

    private static string PinLabel(IPin pin)
    {
        if (pin.QueryPinInfo(out var info) < 0)
            return "输出";
        if (info.Filter != 0)
            Marshal.Release(info.Filter);
        return string.IsNullOrWhiteSpace(info.Name) ? "输出" : info.Name;
    }

    private static List<IPin> EnumPins(IBaseFilter filter, bool output)
    {
        var pins = new List<IPin>();
        if (Hr.Failed(filter.EnumPins(out var enumerator)))
            return pins;
        var slot = Marshal.AllocCoTaskMem(IntPtr.Size);
        var fetched = Marshal.AllocCoTaskMem(sizeof(int));
        try
        {
            while (enumerator.Next(1, slot, fetched) == Hr.Ok && Marshal.ReadInt32(fetched) == 1)
            {
                var pointer = Marshal.ReadIntPtr(slot);
                var pin = (IPin)Marshal.GetObjectForIUnknown(pointer);
                Marshal.Release(pointer);
                if (pin.QueryDirection(out var direction) >= 0 && (direction == 1) == output)
                    pins.Add(pin);
                else
                    ReleasePin(pin);
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(slot);
            Marshal.FreeCoTaskMem(fetched);
            Marshal.ReleaseComObject(enumerator);
        }

        return pins;
    }

    private void Remove(object filter)
    {
        try
        {
            _graph?.RemoveFilter((IBaseFilter)filter);
        }
        catch
        {
        }
    }

    private void Disconnect(IPin pin)
    {
        try
        {
            _graph?.Disconnect(pin);
        }
        catch
        {
        }
    }

    private static void ReleasePin(IPin? pin) => ReleaseCom(pin);

    private static void ReleaseCom(object? com)
    {
        if (com == null || !Marshal.IsComObject(com))
            return;
        try
        {
            Marshal.ReleaseComObject(com);
        }
        catch
        {
        }
    }

    private static bool HasVideoPin(object filter)
    {
        var pin = FindPin((IBaseFilter)filter, output: true, video: true);
        if (pin == null)
            return false;
        ReleasePin(pin);
        return true;
    }

    private static bool IsConnected(IPin pin) => Downstream(pin) != null;

    private static string Trim(string name) => name.Length <= 100 ? name : name[..100];

    private List<MediaTrack> ReadAudioTracks()
    {
        var tracks = new List<MediaTrack>();
        ForEachStream((index, flags, lcid, name, major) =>
        {
            if (major != DsGuid.Audio)
                return;
            tracks.Add(new MediaTrack(index, MediaTrackKind.Audio, name, LanguageFromLcid(lcid), (flags & 1) != 0));
        });
        return tracks;
    }

    private List<MediaTrack> ReadSubtitleTracks()
    {
        var tracks = new List<MediaTrack>();
        ForEachStream((index, flags, lcid, name, major) =>
        {
            if (major == DsGuid.Audio || major == DsGuid.Video)
                return;
            tracks.Add(new MediaTrack(index, MediaTrackKind.Subtitle, name, LanguageFromLcid(lcid), false));
        });
        return tracks;
    }

    private int SelectedAudioIndex()
    {
        foreach (var track in ReadAudioTracks())
        {
            if (track.Selected)
                return track.Index;
        }

        return -1;
    }

    private void EnableAudio(int index)
    {
        if (_splitter is not IamStreamSelect select)
            return;
        var tracks = ReadAudioTracks();
        if (tracks.All(track => track.Index != index))
            return;
        if (tracks.Find(track => track.Selected)?.Index == index)
            return;
        if (Hr.Failed(select.Enable(index, StreamSelectEnable)))
            return;
        var position = ReadPosition();
        if (position < TimeSpan.Zero)
            position = TimeSpan.Zero;
        var current = position.Ticks + 10_000;
        if (_duration > TimeSpan.Zero && current > _duration.Ticks)
            current = _duration.Ticks;
        Volatile.Write(ref _seekedTicks, current);
        _sink?.ClearPresented();
        if (_seeking == null)
            return;
        long stop = 0;
        _seeking.SetPositions(ref current, 1, ref stop, 0);
    }

    private void ReadStreams(List<string> audio, List<string> subtitles)
    {
        ForEachStream((index, flags, _, name, major) =>
        {
            var label = string.IsNullOrWhiteSpace(name) ? $"流 {index + 1}" : name;
            var enabled = (flags & 1) != 0 ? "当前" : "未使用";
            var line = $"{enabled}：{label}";
            if (major == DsGuid.Audio)
                audio.Add(line);
            else if (major != DsGuid.Video)
                subtitles.Add(line);
        });
    }

    private void ForEachStream(Action<int, int, int, string, Guid> visit)
    {
        if (_splitter is not IamStreamSelect select || select.Count(out var count) < 0)
            return;
        for (var i = 0; i < count; i++)
        {
            var slot = Marshal.AllocCoTaskMem(IntPtr.Size);
            try
            {
                if (select.Info(i, slot, out var flags, out var lcid, out _, out var namePtr, out var obj, out var unk) < 0)
                    continue;
                var name = namePtr == 0 ? "" : Marshal.PtrToStringUni(namePtr) ?? "";
                if (namePtr != 0)
                    NativeMethods.CoTaskMemFree(namePtr);
                var major = Guid.Empty;
                var typePtr = Marshal.ReadIntPtr(slot);
                if (typePtr != 0)
                {
                    major = Marshal.PtrToStructure<AmMediaType>(typePtr).MajorType;
                    FreeType(typePtr);
                }

                visit(i, flags, lcid, name, major);
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

    private static string? LanguageFromLcid(int lcid)
    {
        if (lcid <= 0)
            return null;
        try
        {
            var culture = CultureInfo.GetCultureInfo(lcid);
            return culture.LCID == CultureInfo.InvariantCulture.LCID ? null : culture.DisplayName;
        }
        catch (CultureNotFoundException)
        {
            return null;
        }
    }

    private static string Describe(AmMediaType type, ref string? videoCodec, ref string? pixel, ref int? width, ref int? height, ref double? fps, ref string? audioCodec, ref int? channels, ref int? sampleRate)
    {
        if (type.MajorType == DsGuid.Video)
        {
            videoCodec ??= FourCC(type.SubType);
            pixel ??= FourCC(type.SubType);
            int? lineWidth = null;
            int? lineHeight = null;
            double? lineFps = null;
            var header = type.FormatType == DsGuid.FormatVideoInfo2 ? 72 : 48;
            if (type.Format != 0 && type.FormatSize >= header + 16)
            {
                var nextWidth = Math.Abs(Marshal.ReadInt32(type.Format, header + 4));
                var nextHeight = Math.Abs(Marshal.ReadInt32(type.Format, header + 8));
                if (nextWidth > 0 && nextHeight > 0)
                {
                    lineWidth = nextWidth;
                    lineHeight = nextHeight;
                    if (width is not > 0)
                    {
                        width = nextWidth;
                        height = nextHeight;
                    }
                }

                if (header >= 48)
                {
                    var frame = Marshal.ReadInt64(type.Format, 40);
                    if (frame > 0)
                    {
                        lineFps = 10_000_000d / frame;
                        if (fps is not > 0)
                            fps = lineFps;
                    }
                }
            }

            var size = lineWidth is int w && lineHeight is int h ? $"{w}×{h}" : "";
            return $"视频 {videoCodec} {size} {(lineFps is double rate ? rate.ToString("0.###") + " fps" : "")}".Trim();
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
        settings.SetSampleFormat(0, 1);
        for (var format = 1; format <= 4; format++)
            settings.SetSampleFormat(format, 0);
        settings.SetMixingEnabled(0);
    }

    private static IPin? FindPin(IBaseFilter filter, bool output, bool video, bool anyOutput = false)
    {
        if (Hr.Failed(filter.EnumPins(out var enumerator)))
            return null;
        var slot = Marshal.AllocCoTaskMem(IntPtr.Size);
        var fetched = Marshal.AllocCoTaskMem(sizeof(int));
        IPin? fallback = null;
        try
        {
            while (enumerator.Next(1, slot, fetched) == Hr.Ok && Marshal.ReadInt32(fetched) == 1)
            {
                var pointer = Marshal.ReadIntPtr(slot);
                var pin = (IPin)Marshal.GetObjectForIUnknown(pointer);
                Marshal.Release(pointer);
                if (pin.QueryDirection(out var direction) < 0 || (direction == 1) != output)
                {
                    ReleasePin(pin);
                    continue;
                }

                if (!output)
                    return pin;
                if (Major(pin) == (video ? DsGuid.Video : DsGuid.Audio))
                {
                    ReleasePin(fallback);
                    return pin;
                }

                if (anyOutput && fallback == null)
                    fallback = pin;
                else
                    ReleasePin(pin);
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(slot);
            Marshal.FreeCoTaskMem(fetched);
            Marshal.ReleaseComObject(enumerator);
        }

        return fallback;
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
            Marshal.Release(filterPointer);
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
            Marshal.Release(inputPointer);
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

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ConnectedToRaw(IntPtr self, out IntPtr peer);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int QueryPinInfoRaw(IntPtr self, out PinInfo info);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int QueryFilterInfoRaw(IntPtr self, out FilterInfo info);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private delegate int AddFilterDelegate(IntPtr self, IntPtr filter, string name);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ConnectDirectDelegate(IntPtr self, IntPtr output, IntPtr input, IntPtr mediaType);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ConnectDelegate(IntPtr self, IntPtr output, IntPtr input);
}
