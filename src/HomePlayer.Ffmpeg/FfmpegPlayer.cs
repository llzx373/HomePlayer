using System.Buffers;
using System.Globalization;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using HomePlayer.Core;

namespace HomePlayer.Ffmpeg;

public sealed unsafe class FfmpegPlayer : IDisposable
{
    private readonly object _gate = new();
    private AVFormatContext* _format;
    private AVCodecContext* _videoCodec;
    private AVCodecContext* _audioCodec;
    private SwsContext* _scaler;
    private SwrContext* _resampler;
    private int _videoIndex = -1;
    private int _audioIndex = -1;
    private List<MediaTrack> _audioTracks = [];
    private List<MediaTrack> _subtitleTracks = [];
    private int _subtitleIndex = -1;
    private int? _requestedAudioIndex;
    private AVRational _videoTimeBase;
    private int _width;
    private int _height;
    private Thread? _thread;
    private CancellationTokenSource? _cancel;
    private readonly ManualResetEventSlim _resume = new(false);
    private readonly System.Diagnostics.Stopwatch _clock = new();
    private double _clockOrigin;
    private double _rate = 1.0;
    private double? _seekSeconds;
    private long _audioWritten;
    private int _audioEpoch;
    private long _lastPresentMs;
    private string _path = "";
    private string? _container;
    private string? _videoCodecName;
    private string? _audioCodecName;
    private int _channels;
    private int _sampleRate;
    private bool _endedRaised;
    private readonly FrameBufferPool _framePool = new(4);
    private int _frameBytes;

    public event Action<VideoFrame>? FrameArrived;
    public event Action? Ended;
    public event Action<Exception>? Faulted;
    public event Action<byte[], int>? AudioPcm;
    public Func<int>? UnplayedBytes { get; set; }

    private volatile bool _isPlaying;
    private volatile bool _step;
    private long _positionTicks;

    public bool IsOpen { get; private set; }
    public bool IsPlaying => _isPlaying;
    public TimeSpan Duration { get; private set; }
    public TimeSpan Position => TimeSpan.FromTicks(Volatile.Read(ref _positionTicks));
    public bool HasAudio => _audioIndex >= 0;
    public double Rate => Volatile.Read(ref _rate);

    public void Open(string path)
    {
        Close();
        FfmpegNative.Ensure();
        _path = path;
        try
        {
            _format = ffmpeg.avformat_alloc_context();
            var opened = _format;
            var openResult = ffmpeg.avformat_open_input(&opened, path, null, null);
            _format = opened;
            FfmpegError.Check(openResult, "FFmpeg 打开文件");
            FfmpegError.Check(ffmpeg.avformat_find_stream_info(_format, null), "FFmpeg 读取媒体信息");
            _container = _format->iformat == null ? null : Marshal.PtrToStringAnsi((IntPtr)_format->iformat->name);
            Duration = _format->duration > 0
                ? TimeSpan.FromSeconds(_format->duration / (double)ffmpeg.AV_TIME_BASE)
                : TimeSpan.Zero;

            AVCodec* videoDecoder = null;
            _videoIndex = ffmpeg.av_find_best_stream(_format, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, &videoDecoder, 0);
            if (_videoIndex < 0)
                throw new InvalidOperationException("文件里没有可解码的视频流。");
            _videoCodec = ffmpeg.avcodec_alloc_context3(videoDecoder);
            FfmpegError.Check(ffmpeg.avcodec_parameters_to_context(_videoCodec, _format->streams[_videoIndex]->codecpar), "复制视频参数");
            FfmpegError.Check(ffmpeg.avcodec_open2(_videoCodec, videoDecoder, null), "打开视频解码器");
            _videoCodecName = ffmpeg.avcodec_get_name(videoDecoder->id);
            _videoTimeBase = _format->streams[_videoIndex]->time_base;
            _width = _videoCodec->width;
            _height = _videoCodec->height;
            _scaler = ffmpeg.sws_getContext(
                _width, _height, _videoCodec->pix_fmt,
                _width, _height, AVPixelFormat.AV_PIX_FMT_BGRA,
                (int)SwsFlags.SWS_BILINEAR, null, null, null);
            if (_scaler == null)
                throw new InvalidOperationException("无法创建画面转换器。");

            AVCodec* audioDecoder = null;
            _audioIndex = ffmpeg.av_find_best_stream(_format, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, _videoIndex, &audioDecoder, 0);
            if (_audioIndex >= 0 && audioDecoder != null)
                OpenAudioDecoder(_audioIndex);
            LoadAudioTracks();
            LoadSubtitleTracks();

            IsOpen = true;
            _cancel = new CancellationTokenSource();
            _thread = new Thread(Loop) { IsBackground = true, Name = "FFmpeg playback" };
            _thread.Start();
        }
        catch
        {
            Close();
            throw;
        }
    }

    public void Play()
    {
        if (!IsOpen)
            return;
        _isPlaying = true;
        _resume.Set();
    }

    public void Pause()
    {
        _isPlaying = false;
        _resume.Reset();
        lock (_gate)
        {
            if (_clock.IsRunning)
                _clock.Stop();
        }
    }

    public void StepFrame()
    {
        if (!IsOpen)
            return;
        _step = true;
        Pause();
        _resume.Set();
    }

    public void Seek(TimeSpan position)
    {
        var seconds = Math.Max(0, position.TotalSeconds);
        Interlocked.Increment(ref _audioEpoch);
        Interlocked.Exchange(ref _audioWritten, 0);
        lock (_gate)
            _seekSeconds = seconds;
        Volatile.Write(ref _positionTicks, TimeSpan.FromSeconds(seconds).Ticks);
        if (!IsPlaying)
            _resume.Set();
    }

    public IReadOnlyList<MediaTrack> GetAudioTracks()
    {
        lock (_gate)
            return [.. _audioTracks];
    }

    public int AudioTrackIndex => Volatile.Read(ref _audioIndex);

    public IReadOnlyList<MediaTrack> GetSubtitleTracks()
    {
        lock (_gate)
            return [.. _subtitleTracks];
    }

    public int SubtitleTrackIndex => Volatile.Read(ref _subtitleIndex);

    public void SetSubtitleTrack(int index)
    {
        if (!IsOpen)
            return;
        lock (_gate)
        {
            if (index >= 0 && _subtitleTracks.All(track => track.Index != index))
                return;
            _subtitleIndex = index;
            for (var i = 0; i < _subtitleTracks.Count; i++)
            {
                var track = _subtitleTracks[i];
                _subtitleTracks[i] = track with { Selected = track.Index == index };
            }
        }
    }

    public void SetAudioTrack(int index)
    {
        if (!IsOpen)
            return;
        lock (_gate)
        {
            if (_audioTracks.All(track => track.Index != index))
                return;
            if (index == _audioIndex && _requestedAudioIndex == null)
                return;
            _requestedAudioIndex = index;
        }

        Interlocked.Increment(ref _audioEpoch);
        Interlocked.Exchange(ref _audioWritten, 0);
        _resume.Set();
    }

    public void SetRate(double rate)
    {
        rate = PlaybackRates.Clamp(rate);
        lock (_gate)
        {
            var current = MediaSeconds();
            Volatile.Write(ref _rate, rate);
            _clockOrigin = current;
            if (_clock.IsRunning)
                _clock.Restart();
            else
                _clock.Reset();
            Interlocked.Exchange(ref _audioWritten, 0);
        }

        Interlocked.Increment(ref _audioEpoch);
    }

    public PipelineInfo GetPipeline()
    {
        long? size = File.Exists(_path) ? new FileInfo(_path).Length : null;
        double? bitrate = size is long bytes && Duration.TotalSeconds > 0
            ? bytes * 8d / Duration.TotalSeconds / 1_000_000d
            : null;
        return new PipelineInfo
        {
            FilePath = _path,
            Engine = "FFmpeg",
            Container = _container,
            DurationSeconds = Duration.TotalSeconds,
            FileSize = size,
            BitrateMbps = bitrate,
            VideoCodec = _videoCodecName,
            VideoDecoder = $"{_videoCodecName}（FFmpeg 软件解码）",
            Width = _width,
            Height = _height,
            PixelFormat = "bgra",
            Hardware = "未使用",
            AudioCodec = _audioCodecName,
            AudioDecoder = _audioCodecName == null ? null : $"{_audioCodecName}（FFmpeg 软件解码）",
            Channels = HasAudio ? 2 : null,
            SampleRate = HasAudio ? 48000 : null,
            AudioTracks = FormatAudioTracks(),
            SubtitleTracks = FormatSubtitleTracks(),
            Filters = ["FFmpeg demuxer", "FFmpeg video decoder", "swscale → BGRA", HasAudio ? "FFmpeg audio decoder → WASAPI" : "无音频"],
            Connections = [$"视频 {_videoCodecName} {_width}×{_height}", HasAudio ? $"音频 {_audioCodecName} → 48000 Hz stereo s16" : "无音频"]
        };
    }

    public void Close()
    {
        _cancel?.Cancel();
        _resume.Set();
        if (_thread is { IsAlive: true })
            _thread.Join(2000);
        _thread = null;
        _cancel?.Dispose();
        _cancel = null;
        _resume.Reset();
        CloseAudioDecoder();
        if (_scaler != null)
            ffmpeg.sws_freeContext(_scaler);
        _scaler = null;
        var videoCodec = _videoCodec;
        if (videoCodec != null)
            ffmpeg.avcodec_free_context(&videoCodec);
        _videoCodec = null;
        var format = _format;
        if (format != null)
            ffmpeg.avformat_close_input(&format);
        _format = null;
        IsOpen = false;
        _isPlaying = false;
        _step = false;
        Volatile.Write(ref _positionTicks, 0);
        _audioWritten = 0;
        _clock.Reset();
        _clockOrigin = 0;
        Volatile.Write(ref _rate, 1.0);
        _endedRaised = false;
        _frameBytes = 0;
        _videoIndex = -1;
        Volatile.Write(ref _audioIndex, -1);
        Volatile.Write(ref _subtitleIndex, -1);
        lock (_gate)
        {
            _audioTracks = [];
            _subtitleTracks = [];
            _requestedAudioIndex = null;
        }
    }

    public void Dispose()
    {
        Close();
        _resume.Dispose();
    }

    public void AddPlayedPcm(int bytes) { }

    public long TakeAudioClockBytes() => Interlocked.Read(ref _audioWritten);

    private void Loop()
    {
        var packet = ffmpeg.av_packet_alloc();
        var videoFrame = ffmpeg.av_frame_alloc();
        var audioFrame = ffmpeg.av_frame_alloc();
        var readErrors = 0;
        try
        {
            while (_cancel is { IsCancellationRequested: false })
            {
                _resume.Wait(_cancel.Token);
                if (_cancel.IsCancellationRequested)
                    break;

                double? seek;
                int? audioSwitch;
                lock (_gate)
                {
                    seek = _seekSeconds;
                    _seekSeconds = null;
                    audioSwitch = _requestedAudioIndex;
                    _requestedAudioIndex = null;
                }

                if (audioSwitch is int audioIndex && audioIndex != Volatile.Read(ref _audioIndex))
                {
                    if (SwitchAudio(audioIndex))
                        seek ??= Position.TotalSeconds;
                }

                if (seek is double target)
                {
                    SeekTo(target);
                    lock (_gate)
                    {
                        _clockOrigin = target;
                        _clock.Restart();
                    }
                    Interlocked.Exchange(ref _audioWritten, 0);
                    if (!IsPlaying)
                    {
                        _resume.Reset();
                        PresentNearest(videoFrame, target);
                        continue;
                    }
                }

                if (!IsPlaying && !_step)
                {
                    _resume.Reset();
                    if (_clock.IsRunning)
                        _clock.Stop();
                    continue;
                }

                if (_step)
                {
                    if (_clock.IsRunning)
                        _clock.Stop();
                    if (ffmpeg.avcodec_receive_frame(_videoCodec, videoFrame) >= 0)
                    {
                        PresentDecoded(videoFrame);
                        ffmpeg.av_frame_unref(videoFrame);
                        FinishStep();
                        continue;
                    }
                }
                else if (!_clock.IsRunning)
                    _clock.Start();

                ffmpeg.av_packet_unref(packet);
                var read = ffmpeg.av_read_frame(_format, packet);
                if (FfmpegError.IsEof(read))
                {
                    if (_step)
                    {
                        FinishStep();
                        continue;
                    }

                    RaiseEnded();
                    Pause();
                    continue;
                }

                if (read < 0)
                {
                    if (++readErrors >= 10)
                    {
                        if (_step)
                            FinishStep();
                        else
                        {
                            RaiseFaulted(new InvalidOperationException($"读取媒体失败，FFmpeg 错误码 {read}。"));
                            Pause();
                        }
                    }

                    continue;
                }

                readErrors = 0;

                if (packet->stream_index == _videoIndex)
                {
                    ffmpeg.avcodec_send_packet(_videoCodec, packet);
                    while (ffmpeg.avcodec_receive_frame(_videoCodec, videoFrame) >= 0)
                    {
                        var pts = Pts(videoFrame, _videoTimeBase);
                        if (WaitForPresentation(pts))
                        {
                            PresentDecoded(videoFrame, pts);
                            if (_step)
                            {
                                ffmpeg.av_frame_unref(videoFrame);
                                FinishStep();
                                break;
                            }
                        }

                        ffmpeg.av_frame_unref(videoFrame);
                    }
                }
                else if (packet->stream_index == _audioIndex && _audioCodec != null && _resampler != null)
                {
                    ffmpeg.avcodec_send_packet(_audioCodec, packet);
                    while (ffmpeg.avcodec_receive_frame(_audioCodec, audioFrame) >= 0)
                    {
                        if (!_step)
                            EmitAudio(audioFrame);
                        ffmpeg.av_frame_unref(audioFrame);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            RaiseFaulted(ex);
            Pause();
        }
        finally
        {
            ffmpeg.av_frame_free(&audioFrame);
            ffmpeg.av_frame_free(&videoFrame);
            ffmpeg.av_packet_free(&packet);
        }
    }

    private void PresentNearest(AVFrame* videoFrame, double target)
    {
        const int maxPackets = 600;
        var packet = ffmpeg.av_packet_alloc();
        var previous = ffmpeg.av_frame_alloc();
        try
        {
            for (var i = 0; i < maxPackets; i++)
            {
                ffmpeg.av_packet_unref(packet);
                if (ffmpeg.av_read_frame(_format, packet) < 0)
                    break;
                if (packet->stream_index != _videoIndex)
                    continue;
                ffmpeg.avcodec_send_packet(_videoCodec, packet);
                while (ffmpeg.avcodec_receive_frame(_videoCodec, videoFrame) >= 0)
                {
                    var pic = videoFrame->best_effort_timestamp;
                    if (pic == ffmpeg.AV_NOPTS_VALUE)
                        pic = videoFrame->pts;
                    if (pic == ffmpeg.AV_NOPTS_VALUE || pic * Q2d(_videoTimeBase) >= target - 0.0005)
                    {
                        Deliver(ToBgra(videoFrame));
                        ffmpeg.av_frame_unref(videoFrame);
                        return;
                    }

                    ffmpeg.av_frame_unref(previous);
                    ffmpeg.av_frame_ref(previous, videoFrame);
                    ffmpeg.av_frame_unref(videoFrame);
                }
            }

            if (previous->data[0] != null)
                Deliver(ToBgra(previous));
        }
        finally
        {
            ffmpeg.av_frame_free(&previous);
            ffmpeg.av_packet_free(&packet);
        }
    }

    private bool SwitchAudio(int index)
    {
        var previous = Volatile.Read(ref _audioIndex);
        try
        {
            OpenAudioDecoder(index);
            MarkSelected();
            if (_videoCodec != null)
                ffmpeg.avcodec_flush_buffers(_videoCodec);
            if (_audioCodec != null)
                ffmpeg.avcodec_flush_buffers(_audioCodec);
            Interlocked.Increment(ref _audioEpoch);
            Interlocked.Exchange(ref _audioWritten, 0);
            return true;
        }
        catch
        {
            try
            {
                if (previous >= 0)
                    OpenAudioDecoder(previous);
            }
            catch
            {
                Volatile.Write(ref _audioIndex, -1);
            }

            MarkSelected();
            return false;
        }
    }

    private void OpenAudioDecoder(int streamIndex)
    {
        CloseAudioDecoder();
        if (_format == null || streamIndex < 0 || streamIndex >= (int)_format->nb_streams)
            throw new InvalidOperationException("没有可解码的音频流。");
        var stream = _format->streams[streamIndex];
        if (stream->codecpar == null || stream->codecpar->codec_type != AVMediaType.AVMEDIA_TYPE_AUDIO)
            throw new InvalidOperationException("没有可解码的音频流。");
        var decoder = ffmpeg.avcodec_find_decoder(stream->codecpar->codec_id);
        if (decoder == null)
            throw new InvalidOperationException("没有可解码的音频流。");
        try
        {
            _audioCodec = ffmpeg.avcodec_alloc_context3(decoder);
            FfmpegError.Check(ffmpeg.avcodec_parameters_to_context(_audioCodec, stream->codecpar), "复制音频参数");
            FfmpegError.Check(ffmpeg.avcodec_open2(_audioCodec, decoder, null), "打开音频解码器");
            _audioCodecName = ffmpeg.avcodec_get_name(decoder->id);
            _channels = _audioCodec->ch_layout.nb_channels;
            _sampleRate = _audioCodec->sample_rate;
            AVChannelLayout outputLayout;
            ffmpeg.av_channel_layout_default(&outputLayout, 2);
            try
            {
                var inputLayout = _audioCodec->ch_layout;
                SwrContext* resampler = null;
                FfmpegError.Check(ffmpeg.swr_alloc_set_opts2(
                    &resampler,
                    &outputLayout,
                    AVSampleFormat.AV_SAMPLE_FMT_S16,
                    48000,
                    &inputLayout,
                    _audioCodec->sample_fmt,
                    _audioCodec->sample_rate,
                    0,
                    null), "创建音频重采样");
                _resampler = resampler;
                FfmpegError.Check(ffmpeg.swr_init(_resampler), "初始化音频重采样");
            }
            finally
            {
                ffmpeg.av_channel_layout_uninit(&outputLayout);
            }

            Volatile.Write(ref _audioIndex, streamIndex);
        }
        catch
        {
            CloseAudioDecoder();
            throw;
        }
    }

    private void CloseAudioDecoder()
    {
        var resampler = _resampler;
        if (resampler != null)
            ffmpeg.swr_free(&resampler);
        _resampler = null;
        var audioCodec = _audioCodec;
        if (audioCodec != null)
            ffmpeg.avcodec_free_context(&audioCodec);
        _audioCodec = null;
        _audioCodecName = null;
        _channels = 0;
        _sampleRate = 0;
    }

    private void LoadAudioTracks()
    {
        var tracks = new List<MediaTrack>();
        if (_format != null)
        {
            for (var i = 0; i < (int)_format->nb_streams; i++)
            {
                var stream = _format->streams[i];
                if (stream->codecpar == null || stream->codecpar->codec_type != AVMediaType.AVMEDIA_TYPE_AUDIO)
                    continue;
                tracks.Add(new MediaTrack(
                    i,
                    MediaTrackKind.Audio,
                    DictGet(stream->metadata, "title") ?? "",
                    DictLanguage(stream->metadata),
                    i == Volatile.Read(ref _audioIndex)));
            }
        }

        lock (_gate)
            _audioTracks = tracks;
    }

    private void LoadSubtitleTracks()
    {
        var tracks = new List<MediaTrack>();
        if (_format != null)
        {
            for (var i = 0; i < (int)_format->nb_streams; i++)
            {
                var stream = _format->streams[i];
                if (stream->codecpar == null || stream->codecpar->codec_type != AVMediaType.AVMEDIA_TYPE_SUBTITLE)
                    continue;
                tracks.Add(new MediaTrack(
                    i,
                    MediaTrackKind.Subtitle,
                    DictGet(stream->metadata, "title") ?? "",
                    DictLanguage(stream->metadata),
                    i == Volatile.Read(ref _subtitleIndex)));
            }
        }

        lock (_gate)
            _subtitleTracks = tracks;
    }

    private void MarkSelected()
    {
        var selected = Volatile.Read(ref _audioIndex);
        lock (_gate)
        {
            for (var i = 0; i < _audioTracks.Count; i++)
            {
                var track = _audioTracks[i];
                _audioTracks[i] = track with { Selected = track.Index == selected };
            }
        }
    }

    private IReadOnlyList<string> FormatAudioTracks()
    {
        lock (_gate)
        {
            if (_audioTracks.Count == 0)
                return _audioCodecName == null ? [] : [$"当前：{_audioCodecName}，{_channels} 声道，{_sampleRate} Hz"];
            return _audioTracks.Select(track =>
            {
                var mark = track.Selected ? "当前" : "未使用";
                var name = string.IsNullOrWhiteSpace(track.Title) ? $"流 {track.Index}" : track.Title;
                var language = string.IsNullOrWhiteSpace(track.Language) ? "" : $"，{track.Language}";
                return $"{mark}：{name}{language}";
            }).ToList();
        }
    }

    private IReadOnlyList<string> FormatSubtitleTracks()
    {
        lock (_gate)
        {
            return _subtitleTracks.Select(track =>
            {
                var name = string.IsNullOrWhiteSpace(track.Title) ? $"流 {track.Index}" : track.Title;
                var language = string.IsNullOrWhiteSpace(track.Language) ? "" : $"，{track.Language}";
                return $"{name}{language}";
            }).ToList();
        }
    }

    private static string? DictGet(AVDictionary* dict, string key)
    {
        if (dict == null)
            return null;
        var entry = ffmpeg.av_dict_get(dict, key, null, 0);
        if (entry == null)
            return null;
        var text = Marshal.PtrToStringUTF8((IntPtr)entry->value);
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static string? DictLanguage(AVDictionary* metadata)
    {
        var code = DictGet(metadata, "language");
        if (code == null || code.Equals("und", StringComparison.OrdinalIgnoreCase) || code.Equals("unk", StringComparison.OrdinalIgnoreCase))
            return null;
        try
        {
            foreach (var culture in CultureInfo.GetCultures(CultureTypes.NeutralCultures))
            {
                if (culture.TwoLetterISOLanguageName.Equals(code, StringComparison.OrdinalIgnoreCase)
                    || culture.ThreeLetterISOLanguageName.Equals(code, StringComparison.OrdinalIgnoreCase)
                    || culture.ThreeLetterWindowsLanguageName.Equals(code, StringComparison.OrdinalIgnoreCase))
                    return culture.DisplayName;
            }
        }
        catch (CultureNotFoundException)
        {
        }

        return code;
    }

    private void SeekTo(double seconds)
    {
        var ticks = (long)(seconds / Q2d(_videoTimeBase));
        if (ffmpeg.av_seek_frame(_format, _videoIndex, Math.Max(0, ticks), ffmpeg.AVSEEK_FLAG_BACKWARD) >= 0)
            _endedRaised = false;
        ffmpeg.avcodec_flush_buffers(_videoCodec);
        if (_audioCodec != null)
            ffmpeg.avcodec_flush_buffers(_audioCodec);
        Volatile.Write(ref _positionTicks, TimeSpan.FromSeconds(seconds).Ticks);
    }

    private void PresentDecoded(AVFrame* videoFrame, double? pts = null)
    {
        pts ??= Pts(videoFrame, _videoTimeBase);
        var shown = double.IsFinite(pts.Value) && pts.Value >= 0 ? pts.Value : MediaSeconds();
        Volatile.Write(ref _positionTicks, TimeSpan.FromSeconds(Math.Max(0, shown)).Ticks);
        Deliver(ToBgra(videoFrame));
        _lastPresentMs = Environment.TickCount64;
    }

    private void FinishStep()
    {
        _step = false;
        Pause();
        lock (_gate)
        {
            _clockOrigin = Position.TotalSeconds;
            _clock.Reset();
        }

        Interlocked.Exchange(ref _audioWritten, 0);
    }

    private bool WaitForPresentation(double pts)
    {
        while (_cancel is { IsCancellationRequested: false })
        {
            if (_step)
                return true;
            if (!IsPlaying)
                return false;
            lock (_gate)
            {
                if (_seekSeconds != null || _requestedAudioIndex != null)
                    return false;
            }

            var wait = pts - MediaSeconds();
            if (wait <= 0.02)
            {
                if (wait < -0.08 && Environment.TickCount64 - _lastPresentMs < 40)
                    return false;
                return true;
            }

            if (wait > 5)
                return true;
            Thread.Sleep(TimeSpan.FromMilliseconds(Math.Min(20, Math.Max(1, wait * 1000))));
        }

        return false;
    }

    private double MediaSeconds()
    {
        var rate = Volatile.Read(ref _rate);
        if (Math.Abs(rate - 1) < 0.0001 && UnplayedBytes != null)
        {
            var queued = Math.Max(0, UnplayedBytes());
            var played = Math.Max(0, Interlocked.Read(ref _audioWritten) - queued);
            return _clockOrigin + played / 192000d;
        }

        return _clockOrigin + _clock.Elapsed.TotalSeconds * rate;
    }

    private void Deliver(VideoFrame frame)
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

    private void EmitAudio(AVFrame* frame)
    {
        var epoch = Volatile.Read(ref _audioEpoch);
        var outSamples = (int)ffmpeg.av_rescale_rnd(
            ffmpeg.swr_get_delay(_resampler, frame->sample_rate) + frame->nb_samples,
            48000,
            frame->sample_rate,
            AVRounding.AV_ROUND_UP);
        var bytes = outSamples * 2 * 2;
        var buffer = ArrayPool<byte>.Shared.Rent(Math.Max(bytes * 2, 4));
        try
        {
            int count;
            fixed (byte* pointer = buffer)
            {
                var dest0 = pointer;
                var dest = &dest0;
                var source = frame->extended_data;
                var converted = ffmpeg.swr_convert(_resampler, dest, outSamples, source, frame->nb_samples);
                if (converted <= 0)
                    return;
                count = converted * 4;
            }

            if (epoch != Volatile.Read(ref _audioEpoch))
                return;
            count = StretchPcm(buffer, count, Volatile.Read(ref _rate));
            if (count <= 0)
                return;
            Interlocked.Add(ref _audioWritten, count);
            AudioPcm?.Invoke(buffer, count);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static int StretchPcm(byte[] buffer, int count, double rate)
    {
        if (count < 4 || Math.Abs(rate - 1) < 0.0001)
            return count;
        var inFrames = count / 4;
        var outFrames = Math.Max(0, (int)Math.Round(inFrames / rate));
        if (outFrames == 0)
            return 0;
        outFrames = Math.Min(outFrames, buffer.Length / 4);
        var outCount = outFrames * 4;
        var output = ArrayPool<byte>.Shared.Rent(outCount);
        try
        {
            for (var j = 0; j < outFrames; j++)
            {
                var i = Math.Min(inFrames - 1, (int)(j * rate));
                Buffer.BlockCopy(buffer, i * 4, output, j * 4, 4);
            }

            Buffer.BlockCopy(output, 0, buffer, 0, outCount);
            return outCount;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(output);
        }
    }

    private VideoFrame ToBgra(AVFrame* source)
    {
        if (_frameBytes <= 0)
            _frameBytes = Math.Max(1, ffmpeg.av_image_get_buffer_size(AVPixelFormat.AV_PIX_FMT_BGRA, _width, _height, 1));
        var size = _frameBytes;
        var buffer = _framePool.Rent(size);
        try
        {
            var stride = _width * 4;
            fixed (byte* pointer = buffer)
            {
                var data = new byte_ptrArray4();
                var lines = new int_array4();
                ffmpeg.av_image_fill_arrays(ref data, ref lines, pointer, AVPixelFormat.AV_PIX_FMT_BGRA, _width, _height, 1);
                ffmpeg.sws_scale(_scaler, source->data, source->linesize, 0, source->height, data, lines);
                stride = lines[0];
            }

            return new VideoFrame
            {
                Width = _width,
                Height = _height,
                Stride = stride,
                Data = buffer,
                ReleaseBuffer = () => _framePool.Return(buffer)
            };
        }
        catch
        {
            _framePool.Return(buffer);
            throw;
        }
    }

    private void RaiseEnded()
    {
        if (_endedRaised)
            return;
        _endedRaised = true;
        Ended?.Invoke();
    }

    private void RaiseFaulted(Exception error)
    {
        if (_endedRaised)
            return;
        _endedRaised = true;
        Faulted?.Invoke(error);
    }

    private static double Pts(AVFrame* frame, AVRational timeBase)
    {
        var ticks = frame->best_effort_timestamp;
        if (ticks == ffmpeg.AV_NOPTS_VALUE)
            ticks = frame->pts;
        return ticks == ffmpeg.AV_NOPTS_VALUE ? 0 : ticks * Q2d(timeBase);
    }

    private static double Q2d(AVRational rational) => rational.den == 0 ? 0 : rational.num / (double)rational.den;
}
