using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using HomePlayer.Core;

namespace HomePlayer.Ffmpeg;

public sealed unsafe partial class FfmpegPlayer : IDisposable
{
    private sealed record SeekRequest(TimeSpan Position, long Epoch, long CommandId, long StartedTimestamp);

    private sealed class VideoQueueItem(FrameLease? frame, long epoch, bool endOfStream = false)
    {
        public FrameLease? Frame { get; } = frame;
        public long Epoch { get; } = epoch;
        public bool EndOfStream { get; } = endOfStream;
        public long Bytes => Frame switch
        {
            CpuFrameLease cpu => cpu.Data.Length,
            D3D11FrameLease gpu => (long)gpu.Width * gpu.Height * 2,
            _ => 0
        };
    }

    private sealed class PacketLease(FfmpegPlayer owner, IntPtr packet, long epoch, bool endOfStream = false) : IDisposable
    {
        private int _disposed;
        public AVPacket* Packet => (AVPacket*)packet;
        public long Epoch { get; } = epoch;
        public bool EndOfStream { get; } = endOfStream;
        public int Bytes => packet == IntPtr.Zero ? 0 : Packet->size;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0 && packet != IntPtr.Zero)
                owner.ReturnPacket(packet);
        }
    }

    private readonly object _gate = new();
    private readonly object _videoCodecGate = new();
    private readonly object _audioCodecGate = new();
    private readonly ConcurrentBag<IntPtr> _packetPool = new();
    private readonly PcmTimeStretcher _timeStretcher = new();
    private readonly FrameBufferPool _framePool = new(6);
    private readonly Stopwatch _clock = new();
    private AVFormatContext* _format;
    private AVCodecContext* _videoCodec;
    private AVCodecContext* _audioCodec;
    private SwsContext* _scaler;
    private SwrContext* _resampler;
    private BoundedPipelineQueue<PacketLease>? _videoPackets;
    private BoundedPipelineQueue<PacketLease>? _audioPackets;
    private BoundedPipelineQueue<VideoQueueItem>? _videoFrames;
    private Thread? _demuxThread;
    private Thread? _videoThread;
    private Thread? _audioThread;
    private Thread? _schedulerThread;
    private CancellationTokenSource? _cancel;
    private GCHandle _interruptHandle;
    private SeekRequest? _seekRequest;
    private SeekRequest? _activeSeek;
    private int? _requestedAudioIndex;
    private List<MediaTrack> _audioTracks = [];
    private List<MediaTrack> _subtitleTracks = [];
    private int _videoIndex = -1;
    private int _audioIndex = -1;
    private int _subtitleIndex = -1;
    private AVRational _videoTimeBase;
    private AVRational _audioTimeBase;
    private string _path = "";
    private string? _container;
    private string? _videoCodecName;
    private string? _audioCodecName;
    private int _width;
    private int _height;
    private int _channels;
    private int _outputChannels;
    private int _sampleRate;
    private int _frameBytes;
    private int _videoDecoderThreads = 1;
    private long _epoch;
    private long _audioEpoch;
    private long _positionTicks;
    private long _clockOriginTicks;
    private long _audioWritten;
    private long _frameSequence;
    private long _openedTimestamp;
    private int _ioInterrupt;
    private int _demuxEof;
    private int _videoDone;
    private int _audioDone;
    private int _droppedFrames;
    private int _videoQueueDepth;
    private int _readErrors;
    private bool _endedRaised;
    private bool _disposed;
    private double _rate = 1.0;
    private volatile bool _isPlaying;
    private bool _playIntent;
    private volatile bool _step;

    private static readonly AVIOInterruptCB_callback InterruptCallback = OnInterrupt;

    public event Action<FrameLease>? FrameArrived;
    public event Action? Ended;
    public event Action<Exception>? Faulted;
    public event Action<byte[], int>? AudioPcm;
    public Func<int>? UnplayedBytes { get; set; }
    public Func<bool>? AudioOutputDrained { get; set; }
    public Func<double?>? AudioDevicePosition { get; set; }

    public Guid SessionId { get; private set; }
    public string TimelineId { get; private set; } = "";
    public long Epoch => Interlocked.Read(ref _epoch);
    public long PcmEpoch => Interlocked.Read(ref _audioEpoch);
    public bool IsOpen { get; private set; }
    public bool IsPlaying => _isPlaying;
    public TimeSpan Duration { get; private set; }
    public TimeSpan Position => TimeSpan.FromTicks(Volatile.Read(ref _positionTicks));
    public bool HasAudio => Volatile.Read(ref _audioIndex) >= 0;
    public int OutputChannels => Math.Max(1, _outputChannels);
    public double Rate => Volatile.Read(ref _rate);
    public int DroppedFrames => Volatile.Read(ref _droppedFrames);
    public int VideoQueueDepth => Volatile.Read(ref _videoQueueDepth);

    public void Open(string path)
    {
        Close();
        FfmpegNative.Ensure();
        _path = path;
        SessionId = Guid.NewGuid();
        TimelineId = SessionId.ToString("N");
        Volatile.Write(ref _outputPixelFormat, "unknown");
        Volatile.Write(ref _outputZeroCopyLevel, "Unknown");
        _yuvPlanes = null;
        _openedTimestamp = Stopwatch.GetTimestamp();
        Interlocked.Exchange(ref _ioInterrupt, 0);
        _cancel = new CancellationTokenSource();
        _interruptHandle = GCHandle.Alloc(this);
        try
        {
            _cancel.CancelAfter(TimeSpan.FromSeconds(15));
            _format = ffmpeg.avformat_alloc_context();
            if (_format == null)
                throw new InvalidOperationException("无法分配 FFmpeg 输入上下文。");
            _format->interrupt_callback = new AVIOInterruptCB
            {
                callback = InterruptCallback,
                opaque = GCHandle.ToIntPtr(_interruptHandle).ToPointer()
            };
            var opened = _format;
            var openResult = ffmpeg.avformat_open_input(&opened, path, null, null);
            _format = opened;
            FfmpegError.Check(openResult, "FFmpeg 打开文件");
            FfmpegError.Check(ffmpeg.avformat_find_stream_info(_format, null), "FFmpeg 读取媒体信息");
            _cancel.CancelAfter(Timeout.InfiniteTimeSpan);
            _container = _format->iformat == null ? null : Marshal.PtrToStringAnsi((IntPtr)_format->iformat->name);
            Duration = _format->duration > 0
                ? TimeSpan.FromTicks((long)Math.Round(_format->duration / (double)ffmpeg.AV_TIME_BASE * TimeSpan.TicksPerSecond))
                : TimeSpan.Zero;

            AVCodec* videoDecoder = null;
            _videoIndex = ffmpeg.av_find_best_stream(_format, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, &videoDecoder, 0);
            if (_videoIndex < 0)
                throw new InvalidOperationException("文件里没有可解码的视频流。");
            OpenVideoDecoder(videoDecoder, 1);
            _videoTimeBase = _format->streams[_videoIndex]->time_base;
            _width = _videoCodec->width;
            _height = _videoCodec->height;
            _scaler = ffmpeg.sws_getContext(
                _width, _height, _videoCodec->pix_fmt,
                _width, _height, AVPixelFormat.AV_PIX_FMT_BGRA,
                (int)SwsFlags.SWS_BILINEAR, null, null, null);
            if (_scaler == null)
                throw new InvalidOperationException("无法创建画面转换器。");
            _decodedSourceFormat = _videoCodec->pix_fmt;

            AVCodec* audioDecoder = null;
            _audioIndex = ffmpeg.av_find_best_stream(_format, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, _videoIndex, &audioDecoder, 0);
            if (_audioIndex >= 0 && audioDecoder != null)
                OpenAudioDecoder(_audioIndex);
            LoadAudioTracks();
            LoadSubtitleTracks();

            _videoPackets = new BoundedPipelineQueue<PacketLease>(96, 16 * 1024 * 1024);
            _audioPackets = new BoundedPipelineQueue<PacketLease>(192, 8 * 1024 * 1024);
            _videoFrames = new BoundedPipelineQueue<VideoQueueItem>(4, 96 * 1024 * 1024);
            IsOpen = true;
            _videoThread = StartThread(VideoDecodeLoop, "FFmpeg video decode");
            if (HasAudio)
                _audioThread = StartThread(AudioDecodeLoop, "FFmpeg audio decode");
            _schedulerThread = StartThread(VideoSchedulerLoop, "HomePlayer video scheduler");
            _demuxThread = StartThread(DemuxLoop, "FFmpeg demux");
        }
        catch
        {
            Close();
            throw;
        }
    }

    public void Play()
    {
        if (!IsOpen || _disposed)
            return;
        lock (_gate)
        {
            _playIntent = true;
            _step = false;
            if (!_isPlaying)
            {
                _clockOriginTicks = Position.Ticks;
                _clock.Restart();
            }
            _isPlaying = true;
            Interlocked.Exchange(ref _videoDone, 0);
            Interlocked.Exchange(ref _audioDone, 0);
        }
    }

    public void Pause()
    {
        if (!IsOpen)
            return;
        var current = MediaSeconds();
        _playIntent = false;
        _isPlaying = false;
        _step = false;
        lock (_gate)
        {
            _clockOriginTicks = TimeSpan.FromSeconds(Math.Max(0, current)).Ticks;
            _clock.Stop();
            Volatile.Write(ref _positionTicks, _clockOriginTicks);
        }
    }

    public void StepFrame()
    {
        if (!IsOpen)
            return;
        if (_isPlaying)
            Pause();
        _step = true;
    }

    public void Seek(TimeSpan position) => Seek(position, Epoch + 1, 0, Stopwatch.GetTimestamp());

    public void Seek(TimeSpan position, long epoch, long commandId, long startedTimestamp)
    {
        if (!IsOpen)
            return;
        if (position < TimeSpan.Zero)
            position = TimeSpan.Zero;
        if (Duration > TimeSpan.Zero && position > Duration)
            position = Duration;
        lock (_gate)
        {
            if (epoch <= Epoch)
                epoch = Interlocked.Increment(ref _epoch);
            else
                Interlocked.Exchange(ref _epoch, epoch);
            var request = new SeekRequest(position, epoch, commandId, startedTimestamp);
            _seekRequest = request;
            _activeSeek = request;
            _clockOriginTicks = position.Ticks;
            _isPlaying = _playIntent;
            _step = !_playIntent;
            if (_isPlaying)
                _clock.Restart();
            else
                _clock.Stop();
            Interlocked.Increment(ref _audioEpoch);
            Interlocked.Exchange(ref _audioWritten, 0);
            Interlocked.Exchange(ref _videoDone, 0);
            Interlocked.Exchange(ref _audioDone, 0);
            Interlocked.Exchange(ref _demuxEof, 0);
            Interlocked.Exchange(ref _ioInterrupt, 1);
            Volatile.Write(ref _positionTicks, position.Ticks);
            _endedRaised = false;
        }
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
                _subtitleTracks[i] = _subtitleTracks[i] with { Selected = _subtitleTracks[i].Index == index };
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
        Seek(Position);
    }

    public void SetRate(double rate)
    {
        rate = PlaybackRates.Clamp(rate);
        lock (_gate)
        {
            var current = MediaSeconds();
            Volatile.Write(ref _rate, rate);
            _clockOriginTicks = TimeSpan.FromSeconds(Math.Max(0, current)).Ticks;
            if (_isPlaying)
                _clock.Restart();
            else
                _clock.Reset();
        }
        Interlocked.Increment(ref _audioEpoch);
        Interlocked.Exchange(ref _audioWritten, 0);
    }

    public IReadOnlyList<MediaChapter> GetChapters()
    {
        if (_format == null || _format->nb_chapters == 0)
            return [];
        var chapters = new List<MediaChapter>((int)_format->nb_chapters);
        for (var i = 0; i < (int)_format->nb_chapters; i++)
        {
            var chapter = _format->chapters[i];
            if (chapter == null)
                continue;
            var timeBase = new MediaTimeBase(chapter->time_base.num, chapter->time_base.den);
            var title = DictGet(chapter->metadata, "title");
            chapters.Add(new MediaChapter(string.IsNullOrWhiteSpace(title) ? $"章节 {i + 1}" : title,
                TimeSpan.FromTicks(Math.Max(0, timeBase.ToTimeSpanTicks(chapter->start)))));
        }
        return chapters;
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
            VideoDecoder = _usingD3D11 ? $"{_videoCodecName}（FFmpeg D3D11VA）" : $"{_videoCodecName}（FFmpeg 软件解码）",
            Width = _width,
            Height = _height,
            PixelFormat = Volatile.Read(ref _outputPixelFormat),
            Hardware = _usingD3D11 ? "D3D11VA（同设备纹理）" : "未使用",
            AudioCodec = _audioCodecName,
            AudioDecoder = _audioCodecName == null ? null : $"{_audioCodecName}（FFmpeg 软件解码）",
            Channels = HasAudio ? OutputChannels : null,
            SampleRate = HasAudio ? 48000 : null,
            AudioTracks = FormatAudioTracks(),
            SubtitleTracks = FormatSubtitleTracks(),
            Filters = ["FFmpeg demuxer → bounded packet queues", VideoFilterDescription(), "HomePlayer video scheduler", HasAudio ? "FFmpeg audio decoder → bounded WASAPI output" : "无音频"],
            Connections = [$"视频 {_videoCodecName} {_width}×{_height} · {Volatile.Read(ref _outputZeroCopyLevel)}", HasAudio ? $"音频 {_audioCodecName} → 48000 Hz {OutputChannels}ch s16" : "无音频"],
            DroppedFrames = DroppedFrames,
            VideoQueueDepth = VideoQueueDepth,
            ClockSource = AudioDevicePosition == null ? "Monotonic" : "WasapiDeviceWithMonotonicFallback",
            ZeroCopyLevel = Volatile.Read(ref _outputZeroCopyLevel)
        };
    }

    public void Close()
    {
        if (_disposed)
            return;
        Interlocked.Exchange(ref _ioInterrupt, 1);
        _cancel?.Cancel();
        _videoPackets?.Complete();
        _audioPackets?.Complete();
        _videoFrames?.Complete();
        var threads = new[] { _demuxThread, _videoThread, _audioThread, _schedulerThread };
        var exited = true;
        foreach (var thread in threads)
        {
            if (thread is { IsAlive: true } && thread != Thread.CurrentThread)
                exited &= thread.Join(2000);
        }
        _demuxThread = null;
        _videoThread = null;
        _audioThread = null;
        _schedulerThread = null;
        _videoPackets?.Clear(packet => packet.Dispose());
        _audioPackets?.Clear(packet => packet.Dispose());
        _videoFrames?.Clear(ReleaseVideoItem);
        if (!exited)
            return;

        CloseAudioDecoder();
        if (_scaler != null)
            ffmpeg.sws_freeContext(_scaler);
        _scaler = null;
        var videoCodec = _videoCodec;
        if (videoCodec != null)
            ffmpeg.avcodec_free_context(&videoCodec);
        _videoCodec = null;
        ReleaseD3D11Device();
        var format = _format;
        if (format != null)
            ffmpeg.avformat_close_input(&format);
        _format = null;
        while (_packetPool.TryTake(out var packet))
        {
            var native = (AVPacket*)packet;
            ffmpeg.av_packet_free(&native);
        }
        if (_interruptHandle.IsAllocated)
            _interruptHandle.Free();
        _cancel?.Dispose();
        _cancel = null;
        IsOpen = false;
        _playIntent = false;
        _isPlaying = false;
        _step = false;
        Volatile.Write(ref _positionTicks, 0);
        Interlocked.Exchange(ref _audioWritten, 0);
        _clock.Reset();
        _clockOriginTicks = 0;
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
            _seekRequest = null;
            _activeSeek = null;
        }
    }

    public void Dispose()
    {
        Close();
        _disposed = true;
    }

    public long TakeAudioClockBytes() => Interlocked.Read(ref _audioWritten);

    private Thread StartThread(ThreadStart start, string name)
    {
        var thread = new Thread(start) { IsBackground = true, Name = name };
        thread.Start();
        return thread;
    }

    private void DemuxLoop()
    {
        try
        {
            while (_cancel is { IsCancellationRequested: false })
            {
                ProcessTimelineRequests();
                if (Volatile.Read(ref _demuxEof) == 1)
                {
                    Thread.Sleep(10);
                    continue;
                }

                var packet = RentPacket();
                var native = (AVPacket*)packet;
                var read = ffmpeg.av_read_frame(_format, native);
                if (read == ffmpeg.AVERROR_EXIT)
                {
                    ReturnPacket(packet);
                    Interlocked.Exchange(ref _ioInterrupt, 0);
                    Interlocked.Exchange(ref _readErrors, 0);
                    continue;
                }
                if (FfmpegError.IsEof(read))
                {
                    ReturnPacket(packet);
                    Interlocked.Exchange(ref _demuxEof, 1);
                    _videoPackets!.Write(new PacketLease(this, IntPtr.Zero, Epoch, true), 0, _cancel.Token);
                    if (HasAudio)
                        _audioPackets!.Write(new PacketLease(this, IntPtr.Zero, Epoch, true), 0, _cancel.Token);
                    continue;
                }
                if (read < 0)
                {
                    ReturnPacket(packet);
                    if (HasTimelineRequest())
                    {
                        Interlocked.Exchange(ref _readErrors, 0);
                        continue;
                    }
                    if (Interlocked.Increment(ref _readErrors) >= 10)
                        throw new InvalidOperationException($"读取媒体失败，FFmpeg 错误码 {read}。");
                    continue;
                }
                Interlocked.Exchange(ref _readErrors, 0);
                var lease = new PacketLease(this, packet, Epoch);
                if (native->stream_index == _videoIndex)
                    _videoPackets!.Write(lease, lease.Bytes, _cancel.Token);
                else if (native->stream_index == Volatile.Read(ref _audioIndex))
                    _audioPackets!.Write(lease, lease.Bytes, _cancel.Token);
                else
                    lease.Dispose();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            RaiseFaulted(ex);
        }
    }

    private void ProcessTimelineRequests()
    {
        SeekRequest? seek;
        int? audioSwitch;
        lock (_gate)
        {
            seek = _seekRequest;
            _seekRequest = null;
            audioSwitch = _requestedAudioIndex;
            _requestedAudioIndex = null;
        }
        if (audioSwitch is int index && index != Volatile.Read(ref _audioIndex))
        {
            lock (_audioCodecGate)
            {
                if (SwitchAudio(index))
                    seek ??= new SeekRequest(Position, Interlocked.Increment(ref _epoch), 0, Stopwatch.GetTimestamp());
            }
        }
        if (seek == null)
            return;

        _videoPackets!.Clear(packet => packet.Dispose());
        _audioPackets!.Clear(packet => packet.Dispose());
        _videoFrames!.Clear(ReleaseVideoItem);
        lock (_videoCodecGate)
        {
            if (_videoDecoderThreads == 1)
            {
                var decoder = ffmpeg.avcodec_find_decoder(_format->streams[_videoIndex]->codecpar->codec_id);
                OpenVideoDecoder(decoder, 0);
            }
            ffmpeg.avcodec_flush_buffers(_videoCodec);
        }
        lock (_audioCodecGate)
        {
            if (_audioCodec != null)
                ffmpeg.avcodec_flush_buffers(_audioCodec);
            _timeStretcher.Reset();
        }
        Interlocked.Exchange(ref _demuxEof, 0);
        Interlocked.Exchange(ref _videoDone, 0);
        Interlocked.Exchange(ref _audioDone, 0);
        Interlocked.Exchange(ref _ioInterrupt, 0);
        var ticks = new MediaTimeBase(_videoTimeBase.num, _videoTimeBase.den).FromTimeSpanTicks(seek.Position.Ticks);
        var result = ffmpeg.av_seek_frame(_format, _videoIndex, Math.Max(0, ticks), ffmpeg.AVSEEK_FLAG_BACKWARD);
        if (result == ffmpeg.AVERROR_EXIT)
        {
            lock (_gate)
            {
                if (_seekRequest == null)
                    _seekRequest = seek;
            }
            Interlocked.Exchange(ref _ioInterrupt, 0);
            return;
        }
        if (result < 0)
            throw new InvalidOperationException($"FFmpeg 无法定位到目标时间，错误码 {result}。");
    }

    private void VideoDecodeLoop()
    {
        var frame = ffmpeg.av_frame_alloc();
        try
        {
            while (_cancel is { IsCancellationRequested: false })
            {
                if (!_videoPackets!.Read(out var item, _cancel.Token))
                    break;
                _videoPackets.Release(item.Bytes);
                using (item)
                {
                    if (!item.EndOfStream && item.Epoch != Epoch)
                        continue;
                    lock (_videoCodecGate)
                    {
                        var send = ffmpeg.avcodec_send_packet(_videoCodec, item.EndOfStream ? null : item.Packet);
                        if (send < 0 && send != ffmpeg.AVERROR(ffmpeg.EAGAIN))
                            throw new InvalidOperationException($"视频解码器接收压缩包失败，错误码 {send}。");
                        ReceiveFrameLeases(frame, item.Epoch);
                    }
                    if (item.EndOfStream)
                        _videoFrames!.Write(new VideoQueueItem(null, item.Epoch, true), 0, _cancel.Token);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            RaiseFaulted(ex);
        }
        finally
        {
            ffmpeg.av_frame_free(&frame);
        }
    }

    private void ReceiveFrameLeases(AVFrame* frame, long epoch)
    {
        while (true)
        {
            var received = ffmpeg.avcodec_receive_frame(_videoCodec, frame);
            if (received == ffmpeg.AVERROR(ffmpeg.EAGAIN) || FfmpegError.IsEof(received))
                return;
            if (received < 0)
                throw new InvalidOperationException($"视频解码失败，FFmpeg 错误码 {received}。");
            EnsureD3D11DeviceCurrent();
            EnsureVideoFrameFormat(frame);
            if (epoch != Epoch)
            {
                ffmpeg.av_frame_unref(frame);
                continue;
            }
            var pts = Timestamp(frame);
            var duration = Math.Max(0, frame->duration);
            var seek = ActiveSeek(epoch);
            if (seek != null && pts >= 0)
            {
                var target = new MediaTimeBase(_videoTimeBase.num, _videoTimeBase.den).FromTimeSpanTicks(seek.Position.Ticks);
                if (pts + duration < target)
                {
                    ffmpeg.av_frame_unref(frame);
                    continue;
                }
            }
            var decoded = ToFrame(frame, pts, duration, epoch);
            ffmpeg.av_frame_unref(frame);
            try
            {
                var item = new VideoQueueItem(decoded, epoch);
                Volatile.Write(ref _videoQueueDepth, _videoFrames!.Count + 1);
                _videoFrames.Write(item, item.Bytes, _cancel!.Token);
            }
            catch
            {
                decoded.ReturnBuffer();
                throw;
            }
        }
    }

    private void AudioDecodeLoop()
    {
        var frame = ffmpeg.av_frame_alloc();
        try
        {
            while (_cancel is { IsCancellationRequested: false })
            {
                if (!_audioPackets!.Read(out var item, _cancel.Token))
                    break;
                _audioPackets.Release(item.Bytes);
                using (item)
                {
                    if (!item.EndOfStream && item.Epoch != Epoch)
                        continue;
                    lock (_audioCodecGate)
                    {
                        if (_audioCodec == null || _resampler == null)
                            continue;
                        var send = ffmpeg.avcodec_send_packet(_audioCodec, item.EndOfStream ? null : item.Packet);
                        if (send < 0 && send != ffmpeg.AVERROR(ffmpeg.EAGAIN))
                            throw new InvalidOperationException($"音频解码器接收压缩包失败，错误码 {send}。");
                        while (true)
                        {
                            var received = ffmpeg.avcodec_receive_frame(_audioCodec, frame);
                            if (received == ffmpeg.AVERROR(ffmpeg.EAGAIN) || FfmpegError.IsEof(received))
                                break;
                            if (received < 0)
                                throw new InvalidOperationException($"音频解码失败，FFmpeg 错误码 {received}。");
                            if (item.Epoch == Epoch)
                                EmitAudio(frame, item.Epoch);
                            ffmpeg.av_frame_unref(frame);
                        }
                        if (item.EndOfStream)
                        {
                            DrainResampler(item.Epoch);
                            var tail = _timeStretcher.Flush(OutputChannels, Rate);
                            if (tail.Length > 0)
                                EmitPcm(tail, tail.Length, Interlocked.Read(ref _audioEpoch));
                        }
                    }
                    if (item.EndOfStream)
                        Interlocked.Exchange(ref _audioDone, 1);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            RaiseFaulted(ex);
        }
        finally
        {
            ffmpeg.av_frame_free(&frame);
        }
    }

    private void VideoSchedulerLoop()
    {
        var presentedSeekEpoch = -1L;
        try
        {
            while (_cancel is { IsCancellationRequested: false })
            {
                if (!_videoFrames!.Read(out var item, _cancel.Token))
                    break;
                _videoFrames.Release(item.Bytes);
                Volatile.Write(ref _videoQueueDepth, _videoFrames.Count);
                if (item.EndOfStream)
                {
                    if (item.Epoch == Epoch)
                    {
                        Interlocked.Exchange(ref _videoDone, 1);
                        CompleteEndOfStream(item.Epoch);
                    }
                    continue;
                }
                var frame = item.Frame!;
                if (item.Epoch != Epoch)
                {
                    frame.ReturnBuffer();
                    continue;
                }
                var targetFrame = frame.SeekStartedTimestamp > 0 && presentedSeekEpoch != frame.Epoch;
                if (targetFrame)
                    presentedSeekEpoch = frame.Epoch;
                if (!WaitForPresentation(frame, targetFrame))
                {
                    frame.ReturnBuffer();
                    continue;
                }
                Volatile.Write(ref _positionTicks, Math.Max(0, frame.TimeBase.ToTimeSpanTicks(frame.PresentationTimestamp)));
                Deliver(frame);
                if (_step)
                {
                    _step = false;
                    lock (_gate)
                    {
                        _clockOriginTicks = Position.Ticks;
                        _clock.Reset();
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
        }
    }

    private bool WaitForPresentation(FrameLease frame, bool targetFrame)
    {
        while (_cancel is { IsCancellationRequested: false })
        {
            if (frame.Epoch != Epoch)
                return false;
            if (!_isPlaying && !_step)
            {
                Thread.Sleep(5);
                continue;
            }
            if (_step)
                return true;
            var targetSeconds = frame.TimeBase.ToTimeSpanTicks(frame.PresentationTimestamp) / (double)TimeSpan.TicksPerSecond;
            var wait = targetSeconds - MediaSeconds();
            if (wait <= 0.005)
            {
                var frameDuration = Math.Max(0.001, frame.TimeBase.ToTimeSpanTicks(frame.Duration) / (double)TimeSpan.TicksPerSecond);
                if (!targetFrame && wait < -Math.Max(0.08, frameDuration * 2))
                {
                    Interlocked.Increment(ref _droppedFrames);
                    return false;
                }
                return true;
            }
            Thread.Sleep(TimeSpan.FromMilliseconds(Math.Min(10, Math.Max(1, wait * 500))));
        }
        return false;
    }

    private void CompleteEndOfStream(long epoch)
    {
        while (_cancel is { IsCancellationRequested: false } && epoch == Epoch)
        {
            lock (_gate)
            {
                if (epoch == Epoch
                    && Volatile.Read(ref _demuxEof) == 1
                    && Volatile.Read(ref _videoDone) == 1
                    && (!HasAudio || Volatile.Read(ref _audioDone) == 1)
                    && (AudioOutputDrained?.Invoke() ?? (UnplayedBytes?.Invoke() ?? 0) == 0)
                    && _isPlaying)
                {
                    Volatile.Write(ref _positionTicks, Duration.Ticks);
                    _isPlaying = false;
                    _clock.Stop();
                    RaiseEnded();
                    return;
                }
            }
            Thread.Sleep(5);
        }
    }

    private bool HasTimelineRequest()
    {
        lock (_gate)
            return _seekRequest != null || _requestedAudioIndex != null;
    }

    private SeekRequest? ActiveSeek(long epoch)
    {
        lock (_gate)
            return _activeSeek?.Epoch == epoch ? _activeSeek : null;
    }

    private IntPtr RentPacket()
    {
        if (_packetPool.TryTake(out var pooled))
            return pooled;
        return (IntPtr)ffmpeg.av_packet_alloc();
    }

    private void ReturnPacket(IntPtr packet)
    {
        var native = (AVPacket*)packet;
        ffmpeg.av_packet_unref(native);
        _packetPool.Add(packet);
    }

    private void ReleaseVideoItem(VideoQueueItem item) => item.Frame?.ReturnBuffer();

    private bool SwitchAudio(int index)
    {
        var previous = Volatile.Read(ref _audioIndex);
        try
        {
            OpenAudioDecoder(index);
            MarkSelected();
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

    private void OpenVideoDecoder(AVCodec* decoder, int threadCount)
    {
        if (decoder == null)
            throw new InvalidOperationException("没有可解码的视频流。");
        var context = ffmpeg.avcodec_alloc_context3(decoder);
        try
        {
            FfmpegError.Check(ffmpeg.avcodec_parameters_to_context(context, _format->streams[_videoIndex]->codecpar), "复制视频参数");
            context->thread_count = threadCount;
            var hardware = TryConfigureD3D11(context);
            try
            {
                FfmpegError.Check(ffmpeg.avcodec_open2(context, decoder, null), "打开视频解码器");
            }
            catch when (hardware)
            {
                ffmpeg.avcodec_free_context(&context);
                context = ffmpeg.avcodec_alloc_context3(decoder);
                FfmpegError.Check(ffmpeg.avcodec_parameters_to_context(context, _format->streams[_videoIndex]->codecpar), "复制视频参数");
                context->thread_count = threadCount;
                _usingD3D11 = false;
                FfmpegError.Check(ffmpeg.avcodec_open2(context, decoder, null), "回退软件视频解码器");
            }
        }
        catch
        {
            ffmpeg.avcodec_free_context(&context);
            throw;
        }
        var previous = _videoCodec;
        _videoCodec = context;
        _videoDecoderThreads = threadCount;
        _videoCodecName = ffmpeg.avcodec_get_name(decoder->id);
        if (previous != null)
            ffmpeg.avcodec_free_context(&previous);
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
            _audioTimeBase = stream->time_base;
            _channels = _audioCodec->ch_layout.nb_channels;
            _outputChannels = _channels > 0 ? _channels : 2;
            _sampleRate = _audioCodec->sample_rate;
            _timeStretcher.Reset();
            AVChannelLayout outputLayout;
            ffmpeg.av_channel_layout_default(&outputLayout, _outputChannels);
            try
            {
                var inputLayout = _audioCodec->ch_layout;
                SwrContext* resampler = null;
                FfmpegError.Check(ffmpeg.swr_alloc_set_opts2(
                    &resampler, &outputLayout, AVSampleFormat.AV_SAMPLE_FMT_S16, 48000,
                    &inputLayout, _audioCodec->sample_fmt, _audioCodec->sample_rate, 0, null), "创建音频重采样");
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
        _outputChannels = 0;
        _sampleRate = 0;
        _timeStretcher.Reset();
    }

    private void EmitAudio(AVFrame* frame, long epoch)
    {
        var pts = Timestamp(frame);
        var seek = ActiveSeek(epoch);
        if (seek != null && pts >= 0)
        {
            var target = new MediaTimeBase(_audioTimeBase.num, _audioTimeBase.den).FromTimeSpanTicks(seek.Position.Ticks);
            if (pts < target)
                return;
        }
        var outSamples = (int)ffmpeg.av_rescale_rnd(
            ffmpeg.swr_get_delay(_resampler, frame->sample_rate) + frame->nb_samples,
            48000, frame->sample_rate, AVRounding.AV_ROUND_UP);
        ConvertAudio(frame->extended_data, frame->nb_samples, outSamples, epoch);
    }

    private void DrainResampler(long epoch)
    {
        while (true)
        {
            var delay = ffmpeg.swr_get_delay(_resampler, 48000);
            if (delay <= 0)
                break;
            var outSamples = (int)Math.Min(4096, ffmpeg.av_rescale_rnd(delay, 48000, 48000, AVRounding.AV_ROUND_UP));
            var buffer = ArrayPool<byte>.Shared.Rent(Math.Max(4, outSamples * OutputChannels * 2));
            try
            {
                int converted;
                fixed (byte* pointer = buffer)
                {
                    var dest0 = pointer;
                    var dest = &dest0;
                    converted = ffmpeg.swr_convert(_resampler, dest, outSamples, null, 0);
                }
                if (converted <= 0)
                    break;
                EmitConverted(buffer, converted * OutputChannels * 2, epoch);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    private void ConvertAudio(byte** source, int inputSamples, int outSamples, long epoch)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(Math.Max(outSamples * OutputChannels * 2, 4));
        try
        {
            int converted;
            fixed (byte* pointer = buffer)
            {
                var dest0 = pointer;
                var dest = &dest0;
                converted = ffmpeg.swr_convert(_resampler, dest, outSamples, source, inputSamples);
            }
            if (converted > 0)
                EmitConverted(buffer, converted * OutputChannels * 2, epoch);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void EmitConverted(byte[] buffer, int count, long epoch)
    {
        var pcmEpoch = Interlocked.Read(ref _audioEpoch);
        var output = _timeStretcher.Process(buffer, count, OutputChannels, Rate);
        if (output.Length > 0 && epoch == Epoch)
            EmitPcm(output, output.Length, pcmEpoch);
    }

    private void EmitPcm(byte[] output, int count, long pcmEpoch)
    {
        if (pcmEpoch != Interlocked.Read(ref _audioEpoch))
            return;
        Interlocked.Add(ref _audioWritten, count);
        AudioPcm?.Invoke(output, count);
    }

    private FrameLease ToBgra(AVFrame* source, long pts, long duration, long epoch)
    {
        if (_frameBytes <= 0)
            _frameBytes = Math.Max(1, ffmpeg.av_image_get_buffer_size(AVPixelFormat.AV_PIX_FMT_BGRA, _width, _height, 1));
        var buffer = _framePool.Rent(_frameBytes);
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
            var seek = ActiveSeek(epoch);
            MarkBgraPipeline();
            return new VideoFrame
            {
                Width = _width,
                Height = _height,
                Stride = stride,
                Data = buffer,
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
                ZeroCopyLevel = ZeroCopyLevel.Z1,
                ReleaseBuffer = () => _framePool.Return(buffer)
            };
        }
        catch
        {
            _framePool.Return(buffer);
            throw;
        }
    }

    private double MediaSeconds()
    {
        var device = AudioDevicePosition?.Invoke();
        if (device is double seconds && double.IsFinite(seconds) && seconds >= 0)
            return seconds;
        return _clockOriginTicks / (double)TimeSpan.TicksPerSecond + _clock.Elapsed.TotalSeconds * Rate;
    }

    private void Deliver(FrameLease frame)
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
        _isPlaying = false;
        Faulted?.Invoke(error);
    }

    private static int OnInterrupt(void* opaque)
    {
        try
        {
            if (opaque == null)
                return 1;
            var state = GCHandle.FromIntPtr((IntPtr)opaque).Target as FfmpegPlayer;
            if (state?._cancel?.IsCancellationRequested != false)
                return 1;
            return Volatile.Read(ref state._ioInterrupt);
        }
        catch
        {
            return 1;
        }
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
                tracks.Add(new MediaTrack(i, MediaTrackKind.Audio, DictGet(stream->metadata, "title") ?? "", DictLanguage(stream->metadata),
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
                tracks.Add(new MediaTrack(i, MediaTrackKind.Subtitle, DictGet(stream->metadata, "title") ?? "", DictLanguage(stream->metadata),
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
                _audioTracks[i] = _audioTracks[i] with { Selected = _audioTracks[i].Index == selected };
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
            return _subtitleTracks.Select(track =>
            {
                var name = string.IsNullOrWhiteSpace(track.Title) ? $"流 {track.Index}" : track.Title;
                var language = string.IsNullOrWhiteSpace(track.Language) ? "" : $"，{track.Language}";
                return $"{name}{language}";
            }).ToList();
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

    private static long Timestamp(AVFrame* frame)
    {
        var timestamp = frame->best_effort_timestamp;
        if (timestamp == ffmpeg.AV_NOPTS_VALUE)
            timestamp = frame->pts;
        return timestamp == ffmpeg.AV_NOPTS_VALUE ? 0 : timestamp;
    }
}
