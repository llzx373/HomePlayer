using FFmpeg.AutoGen;
using HomePlayer.Core;

namespace HomePlayer.Ffmpeg;

public sealed class FfmpegSeekPreviewExtractor : IDisposable
{
    private readonly DecodeSession _session;

    public FfmpegSeekPreviewExtractor(string path, int maxWidth = 176, int maxHeight = 90)
    {
        FfmpegNative.Ensure();
        _session = new DecodeSession(path, maxWidth, maxHeight);
    }

    public VideoFrame? Capture(TimeSpan target, CancellationToken cancellationToken = default) =>
        _session.FrameAtExact(Math.Max(0, target.TotalSeconds), cancellationToken);

    public void Dispose() => _session.Dispose();

    private sealed unsafe class DecodeSession : IDisposable
    {
        private AVFormatContext* _format;
        private AVCodecContext* _codec;
        private readonly int _streamIndex;
        private readonly AVRational _timeBase;
        private readonly double _startSeconds;
        private readonly double _fallbackFrameDuration;
        private SwsContext* _scaler;
        private readonly int _dstWidth;
        private readonly int _dstHeight;
        private bool _drainSent;
        private bool _drained;

        public DecodeSession(string path, int maxWidth, int maxHeight)
        {
            try
            {
                _format = ffmpeg.avformat_alloc_context();
                var opened = _format;
                var openResult = ffmpeg.avformat_open_input(&opened, path, null, null);
                _format = opened;
                FfmpegError.Check(openResult, "打开文件");
                FfmpegError.Check(ffmpeg.avformat_find_stream_info(_format, null), "读取媒体信息");
                AVCodec* codec = null;
                _streamIndex = ffmpeg.av_find_best_stream(_format, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, &codec, 0);
                FfmpegError.Check(_streamIndex, "查找视频流");
                _codec = ffmpeg.avcodec_alloc_context3(codec);
                FfmpegError.Check(ffmpeg.avcodec_parameters_to_context(_codec, _format->streams[_streamIndex]->codecpar), "复制编码参数");
                FfmpegError.Check(ffmpeg.avcodec_open2(_codec, codec, null), "打开解码器");
                var stream = _format->streams[_streamIndex];
                _timeBase = stream->time_base;
                var start = stream->start_time != ffmpeg.AV_NOPTS_VALUE
                    ? stream->start_time
                    : _format->start_time != ffmpeg.AV_NOPTS_VALUE
                        ? (long)(_format->start_time * Q2d(_timeBase) / ffmpeg.AV_TIME_BASE)
                        : 0;
                _startSeconds = start * Q2d(_timeBase);
                _fallbackFrameDuration = FrameDuration(stream->avg_frame_rate, stream->r_frame_rate);
                (_dstWidth, _dstHeight) = Fit(_codec->width, _codec->height, maxWidth, maxHeight);
                _scaler = ffmpeg.sws_getContext(
                    _codec->width, _codec->height, _codec->pix_fmt,
                    _dstWidth, _dstHeight, AVPixelFormat.AV_PIX_FMT_BGRA,
                    (int)SwsFlags.SWS_BILINEAR, null, null, null);
                if (_scaler == null)
                    throw new InvalidOperationException("无法创建画面缩放器。");
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public VideoFrame? FrameAtExact(double seconds, CancellationToken cancellationToken)
        {
            Seek(seconds);
            VideoFrame? previous = null;
            while (TryDecode(out var frame, cancellationToken))
            {
                var position = frame.PresentationTimestamp / (double)TimeSpan.TicksPerSecond;
                var duration = frame.Duration / (double)TimeSpan.TicksPerSecond;
                if (position >= seconds)
                {
                    if (previous != null && seconds >= PreviousPosition(previous) && seconds < position)
                        return previous;
                    if (position <= seconds + duration)
                        return frame;
                    return null;
                }

                previous = frame;
            }

            if (previous != null)
            {
                var position = PreviousPosition(previous);
                var duration = previous.Duration / (double)TimeSpan.TicksPerSecond;
                if (seconds >= position && seconds <= position + duration)
                    return previous;
            }

            return null;
        }

        private void Seek(double seconds)
        {
            var timestamp = (long)((seconds + _startSeconds) / Q2d(_timeBase));
            FfmpegError.Check(
                ffmpeg.av_seek_frame(_format, _streamIndex, timestamp, ffmpeg.AVSEEK_FLAG_BACKWARD),
                "定位预览帧");
            ffmpeg.avcodec_flush_buffers(_codec);
            _drainSent = false;
            _drained = false;
        }

        private bool TryDecode(out VideoFrame frame, CancellationToken cancellationToken)
        {
            frame = null!;
            var packet = ffmpeg.av_packet_alloc();
            var raw = ffmpeg.av_frame_alloc();
            try
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var received = ffmpeg.avcodec_receive_frame(_codec, raw);
                    if (received >= 0)
                    {
                        frame = Convert(raw);
                        return true;
                    }

                    if (received == ffmpeg.AVERROR_EOF || _drained)
                    {
                        _drained = true;
                        return false;
                    }

                    if (received != ffmpeg.AVERROR(ffmpeg.EAGAIN))
                        FfmpegError.Check(received, "取出画面");

                    ffmpeg.av_packet_unref(packet);
                    var read = ffmpeg.av_read_frame(_format, packet);
                    if (FfmpegError.IsEof(read))
                    {
                        if (_drainSent)
                            return false;
                        FfmpegError.Check(ffmpeg.avcodec_send_packet(_codec, null), "结束视频解码");
                        _drainSent = true;
                        continue;
                    }

                    FfmpegError.Check(read, "读取数据包");
                    if (packet->stream_index != _streamIndex)
                        continue;
                    FfmpegError.Check(ffmpeg.avcodec_send_packet(_codec, packet), "送入解码器");
                }
            }
            finally
            {
                ffmpeg.av_frame_free(&raw);
                ffmpeg.av_packet_free(&packet);
            }
        }

        private VideoFrame Convert(AVFrame* source)
        {
            var output = new byte[ffmpeg.av_image_get_buffer_size(AVPixelFormat.AV_PIX_FMT_BGRA, _dstWidth, _dstHeight, 1)];
            var stride = _dstWidth * 4;
            fixed (byte* pointer = output)
            {
                var data = new byte_ptrArray4();
                var lines = new int_array4();
                ffmpeg.av_image_fill_arrays(ref data, ref lines, pointer, AVPixelFormat.AV_PIX_FMT_BGRA, _dstWidth, _dstHeight, 1);
                ffmpeg.sws_scale(_scaler, source->data, source->linesize, 0, source->height, data, lines);
                stride = lines[0];
            }

            var position = Timestamp(source) - _startSeconds;
            var duration = source->duration > 0 ? source->duration * Q2d(_timeBase) : _fallbackFrameDuration;
            return new VideoFrame
            {
                Width = _dstWidth,
                Height = _dstHeight,
                Stride = stride,
                Data = output,
                PresentationTimestamp = Math.Max(0, (long)Math.Round(position * TimeSpan.TicksPerSecond)),
                Duration = Math.Max(0, (long)Math.Round(duration * TimeSpan.TicksPerSecond))
            };
        }

        private double Timestamp(AVFrame* frame)
        {
            var ticks = frame->best_effort_timestamp;
            if (ticks == ffmpeg.AV_NOPTS_VALUE)
                ticks = frame->pts;
            return ticks == ffmpeg.AV_NOPTS_VALUE ? _startSeconds : ticks * Q2d(_timeBase);
        }

        public void Dispose()
        {
            if (_scaler != null)
                ffmpeg.sws_freeContext(_scaler);
            _scaler = null;
            var codec = _codec;
            if (codec != null)
                ffmpeg.avcodec_free_context(&codec);
            _codec = null;
            var format = _format;
            if (format != null)
                ffmpeg.avformat_close_input(&format);
            _format = null;
        }

        private static double PreviousPosition(VideoFrame frame) =>
            frame.PresentationTimestamp / (double)TimeSpan.TicksPerSecond;

        private static double FrameDuration(AVRational average, AVRational fallback)
        {
            var rate = Q2d(average);
            if (rate <= 0)
                rate = Q2d(fallback);
            return rate > 0 ? 1 / rate : 0;
        }

        private static (int Width, int Height) Fit(int width, int height, int maxWidth, int maxHeight)
        {
            if (width <= 0 || height <= 0)
                return (16, 16);
            var scale = Math.Min(maxWidth / (double)width, maxHeight / (double)height);
            scale = Math.Min(1, scale);
            var w = Math.Max(2, (int)(width * scale)) & ~1;
            var h = Math.Max(2, (int)(height * scale)) & ~1;
            return (w, h);
        }

        private static double Q2d(AVRational rational) => rational.den == 0 ? 0 : rational.num / (double)rational.den;
    }
}
