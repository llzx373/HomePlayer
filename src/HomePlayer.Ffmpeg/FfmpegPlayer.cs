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
    private AVRational _videoTimeBase;
    private int _width;
    private int _height;
    private Thread? _thread;
    private CancellationTokenSource? _cancel;
    private readonly ManualResetEventSlim _resume = new(false);
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

    public event Action<VideoFrame>? FrameArrived;
    public event Action? Ended;
    public event Action<byte[], int>? AudioPcm;
    public Func<int>? UnplayedBytes { get; set; }

    public bool IsOpen { get; private set; }
    public bool IsPlaying { get; private set; }
    public TimeSpan Duration { get; private set; }
    public TimeSpan Position { get; private set; }
    public bool HasAudio => _audioIndex >= 0;

    public void Open(string path)
    {
        Close();
        FfmpegNative.Ensure();
        _path = path;
        _format = ffmpeg.avformat_alloc_context();
        var opened = _format;
        FfmpegError.Check(ffmpeg.avformat_open_input(&opened, path, null, null), "FFmpeg 打开文件");
        _format = opened;
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
        {
            _audioCodec = ffmpeg.avcodec_alloc_context3(audioDecoder);
            FfmpegError.Check(ffmpeg.avcodec_parameters_to_context(_audioCodec, _format->streams[_audioIndex]->codecpar), "复制音频参数");
            FfmpegError.Check(ffmpeg.avcodec_open2(_audioCodec, audioDecoder, null), "打开音频解码器");
            _audioCodecName = ffmpeg.avcodec_get_name(audioDecoder->id);
            _channels = _audioCodec->ch_layout.nb_channels;
            _sampleRate = _audioCodec->sample_rate;
            AVChannelLayout outputLayout;
            ffmpeg.av_channel_layout_default(&outputLayout, 2);
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

        IsOpen = true;
        _cancel = new CancellationTokenSource();
        _thread = new Thread(Loop) { IsBackground = true, Name = "FFmpeg playback" };
        _thread.Start();
    }

    public void Play()
    {
        if (!IsOpen)
            return;
        IsPlaying = true;
        _resume.Set();
    }

    public void Pause()
    {
        IsPlaying = false;
        _resume.Reset();
    }

    public void Seek(TimeSpan position)
    {
        Interlocked.Increment(ref _audioEpoch);
        Interlocked.Exchange(ref _audioWritten, 0);
        lock (_gate)
            _seekSeconds = Math.Max(0, position.TotalSeconds);
        if (!IsPlaying)
            _resume.Set();
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
            AudioTracks = _audioCodecName == null ? [] : [$"当前：{_audioCodecName}，{_channels} 声道，{_sampleRate} Hz"],
            SubtitleTracks = [],
            Filters = ["FFmpeg demuxer", "FFmpeg video decoder", "swscale → BGRA", HasAudio ? "FFmpeg audio decoder → WASAPI" : "无音频"],
            Connections = [$"视频 {_videoCodecName} {_width}×{_height}", HasAudio ? $"音频 {_audioCodecName} → 48000 Hz stereo s16" : "无音频"]
        };
    }

    public void Close()
    {
        _cancel?.Cancel();
        _resume.Set();
        _thread?.Join(2000);
        _thread = null;
        _cancel?.Dispose();
        _cancel = null;
        _resume.Reset();
        var resampler = _resampler;
        if (resampler != null)
            ffmpeg.swr_free(&resampler);
        _resampler = null;
        if (_scaler != null)
            ffmpeg.sws_freeContext(_scaler);
        _scaler = null;
        var audioCodec = _audioCodec;
        if (audioCodec != null)
            ffmpeg.avcodec_free_context(&audioCodec);
        _audioCodec = null;
        var videoCodec = _videoCodec;
        if (videoCodec != null)
            ffmpeg.avcodec_free_context(&videoCodec);
        _videoCodec = null;
        var format = _format;
        if (format != null)
            ffmpeg.avformat_close_input(&format);
        _format = null;
        IsOpen = false;
        IsPlaying = false;
        _audioWritten = 0;
        _endedRaised = false;
        _videoIndex = -1;
        _audioIndex = -1;
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
        var clock = new System.Diagnostics.Stopwatch();
        double clockOrigin = 0;
        try
        {
            while (_cancel is { IsCancellationRequested: false })
            {
                _resume.Wait(_cancel.Token);
                if (_cancel.IsCancellationRequested)
                    break;

                double? seek;
                lock (_gate)
                {
                    seek = _seekSeconds;
                    _seekSeconds = null;
                }

                if (seek is double target)
                {
                    SeekTo(target);
                    clockOrigin = target;
                    clock.Restart();
                    Interlocked.Exchange(ref _audioWritten, 0);
                    if (!IsPlaying)
                    {
                        _resume.Reset();
                        PresentNearest(videoFrame);
                        continue;
                    }
                }

                if (!IsPlaying)
                {
                    if (!clock.IsRunning)
                        continue;
                    clock.Stop();
                    continue;
                }

                if (!clock.IsRunning)
                    clock.Start();

                ffmpeg.av_packet_unref(packet);
                var read = ffmpeg.av_read_frame(_format, packet);
                if (FfmpegError.IsEof(read))
                {
                    RaiseEnded();
                    Pause();
                    continue;
                }

                if (read < 0)
                    continue;

                if (packet->stream_index == _videoIndex)
                {
                    ffmpeg.avcodec_send_packet(_videoCodec, packet);
                    while (ffmpeg.avcodec_receive_frame(_videoCodec, videoFrame) >= 0)
                    {
                        var pts = Pts(videoFrame, _videoTimeBase);
                        if (WaitForPresentation(pts, clock, clockOrigin))
                        {
                            Position = TimeSpan.FromSeconds(Math.Max(0, MediaSeconds(clock, clockOrigin)));
                            FrameArrived?.Invoke(ToBgra(videoFrame));
                            _lastPresentMs = Environment.TickCount64;
                        }

                        ffmpeg.av_frame_unref(videoFrame);
                    }
                }
                else if (packet->stream_index == _audioIndex && _audioCodec != null && _resampler != null)
                {
                    ffmpeg.avcodec_send_packet(_audioCodec, packet);
                    while (ffmpeg.avcodec_receive_frame(_audioCodec, audioFrame) >= 0)
                    {
                        EmitAudio(audioFrame);
                        ffmpeg.av_frame_unref(audioFrame);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            RaiseEnded();
        }
        finally
        {
            ffmpeg.av_frame_free(&audioFrame);
            ffmpeg.av_frame_free(&videoFrame);
            ffmpeg.av_packet_free(&packet);
        }
    }

    private void PresentNearest(AVFrame* videoFrame)
    {
        var packet = ffmpeg.av_packet_alloc();
        try
        {
            for (var i = 0; i < 30; i++)
            {
                ffmpeg.av_packet_unref(packet);
                if (ffmpeg.av_read_frame(_format, packet) < 0)
                    return;
                if (packet->stream_index != _videoIndex)
                    continue;
                ffmpeg.avcodec_send_packet(_videoCodec, packet);
                if (ffmpeg.avcodec_receive_frame(_videoCodec, videoFrame) >= 0)
                {
                    FrameArrived?.Invoke(ToBgra(videoFrame));
                    ffmpeg.av_frame_unref(videoFrame);
                    return;
                }
            }
        }
        finally
        {
            ffmpeg.av_packet_free(&packet);
        }
    }

    private void SeekTo(double seconds)
    {
        var ticks = (long)(seconds / Q2d(_videoTimeBase));
        ffmpeg.av_seek_frame(_format, _videoIndex, Math.Max(0, ticks), ffmpeg.AVSEEK_FLAG_BACKWARD);
        ffmpeg.avcodec_flush_buffers(_videoCodec);
        if (_audioCodec != null)
            ffmpeg.avcodec_flush_buffers(_audioCodec);
        Position = TimeSpan.FromSeconds(seconds);
    }

    private bool WaitForPresentation(double pts, System.Diagnostics.Stopwatch clock, double origin)
    {
        while (IsPlaying && _cancel is { IsCancellationRequested: false })
        {
            lock (_gate)
            {
                if (_seekSeconds != null)
                    return false;
            }

            var wait = pts - MediaSeconds(clock, origin);
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

    private double MediaSeconds(System.Diagnostics.Stopwatch clock, double origin)
    {
        if (UnplayedBytes != null)
        {
            var queued = Math.Max(0, UnplayedBytes());
            var played = Math.Max(0, Interlocked.Read(ref _audioWritten) - queued);
            return origin + played / 192000d;
        }

        return origin + clock.Elapsed.TotalSeconds;
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
        var buffer = new byte[Math.Max(bytes, 4)];
        fixed (byte* pointer = buffer)
        {
            var dest0 = pointer;
            var dest = &dest0;
            var source = frame->extended_data;
            var converted = ffmpeg.swr_convert(_resampler, dest, outSamples, source, frame->nb_samples);
            if (converted <= 0)
                return;
            var count = converted * 4;
            if (epoch != Volatile.Read(ref _audioEpoch))
                return;
            Interlocked.Add(ref _audioWritten, count);
            AudioPcm?.Invoke(buffer, count);
        }
    }

    private VideoFrame ToBgra(AVFrame* source)
    {
        var size = ffmpeg.av_image_get_buffer_size(AVPixelFormat.AV_PIX_FMT_BGRA, _width, _height, 1);
        var buffer = new byte[size];
        var stride = _width * 4;
        fixed (byte* pointer = buffer)
        {
            var data = new byte_ptrArray4();
            var lines = new int_array4();
            ffmpeg.av_image_fill_arrays(ref data, ref lines, pointer, AVPixelFormat.AV_PIX_FMT_BGRA, _width, _height, 1);
            ffmpeg.sws_scale(_scaler, source->data, source->linesize, 0, source->height, data, lines);
            stride = lines[0];
        }

        return new VideoFrame { Width = _width, Height = _height, Stride = stride, Data = buffer };
    }

    private void RaiseEnded()
    {
        if (_endedRaised)
            return;
        _endedRaised = true;
        Ended?.Invoke();
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
