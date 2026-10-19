using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using HomePlayer.Core;

namespace HomePlayer.Ffmpeg;

public static unsafe class FfmpegImages
{
    public static VideoFrame?[] ExtractNine(string path)
    {
        FfmpegNative.Ensure();
        using var session = new DecodeSession(path);
        if (session.DurationSeconds >= 1)
        {
            // 长视频的相邻预览点间隔很大，直接用 seek 到的关键帧，避免每个点都解码几十帧。
            var keyframeOnly = session.DurationSeconds >= 300;
            var frames = new VideoFrame?[9];
            for (var k = 0; k < 9; k++)
            {
                var target = session.DurationSeconds * (k + 0.5) / 9.0;
                frames[k] = session.FrameAt(target, keyframeOnly);
            }

            return frames;
        }

        var grabbed = session.TakeEvenly(9);
        while (grabbed.Count < 9 && grabbed.Count > 0)
            grabbed.Add(grabbed[^1]);
        var result = new VideoFrame?[9];
        for (var i = 0; i < grabbed.Count && i < 9; i++)
            result[i] = grabbed[i];
        return result;
    }

    private sealed unsafe class DecodeSession : IDisposable
    {
        private AVFormatContext* _format;
        private AVCodecContext* _codec;
        private readonly int _streamIndex;
        private readonly AVRational _timeBase;
        private readonly SwsContext* _scaler;
        private readonly int _dstWidth;
        private readonly int _dstHeight;

        public double DurationSeconds { get; }

        public DecodeSession(string path)
        {
            try
            {
                _format = ffmpeg.avformat_alloc_context();
                var opened = _format;
                var openResult = ffmpeg.avformat_open_input(&opened, path, null, null);
                _format = opened;
                FfmpegError.Check(openResult, "打开文件");
                FfmpegError.Check(ffmpeg.avformat_find_stream_info(_format, null), "读取媒体信息");
                DurationSeconds = _format->duration > 0 ? _format->duration / (double)ffmpeg.AV_TIME_BASE : 0;
                AVCodec* codec = null;
                _streamIndex = ffmpeg.av_find_best_stream(_format, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, &codec, 0);
                FfmpegError.Check(_streamIndex, "查找视频流");
                _codec = ffmpeg.avcodec_alloc_context3(codec);
                FfmpegError.Check(ffmpeg.avcodec_parameters_to_context(_codec, _format->streams[_streamIndex]->codecpar), "复制编码参数");
                FfmpegError.Check(ffmpeg.avcodec_open2(_codec, codec, null), "打开解码器");
                _timeBase = _format->streams[_streamIndex]->time_base;
                (_dstWidth, _dstHeight) = Fit(_codec->width, _codec->height, 480, 270);
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

        public VideoFrame? FrameAt(double seconds, bool keyframeOnly)
        {
            if (SeekAndDecode(seconds, out var first, out var firstPts))
            {
                if (keyframeOnly || firstPts + 0.05 >= seconds)
                    return first;

                var best = first;
                for (var i = 1; i < 40; i++)
                {
                    if (!TryDecode(out var frame, out var pts))
                        break;
                    best = frame;
                    if (pts + 0.05 >= seconds)
                        break;
                }

                return best;
            }

            foreach (var delta in new[] { -0.02, 0.02 })
            {
                var nudged = Math.Clamp(seconds + DurationSeconds * delta, 0, Math.Max(0, DurationSeconds - 0.05));
                if (Math.Abs(nudged - seconds) < 0.01)
                    continue;
                if (SeekAndDecode(nudged, out var retry, out _))
                    return retry;
            }

            return null;
        }

        private bool SeekAndDecode(double seconds, out VideoFrame? frame, out double pts)
        {
            var timestamp = (long)(seconds / Q2d(_timeBase));
            ffmpeg.av_seek_frame(_format, _streamIndex, Math.Max(0, timestamp), ffmpeg.AVSEEK_FLAG_BACKWARD);
            ffmpeg.avcodec_flush_buffers(_codec);
            if (!TryDecode(out var decoded, out pts))
            {
                frame = null;
                return false;
            }

            frame = decoded;
            return true;
        }

        public List<VideoFrame> TakeEvenly(int count)
        {
            var all = new List<VideoFrame>();
            while (all.Count < 90 && TryDecode(out var frame, out _))
                all.Add(frame);
            if (all.Count == 0)
                return all;
            if (all.Count <= count)
                return all;
            var picked = new List<VideoFrame>(count);
            for (var i = 0; i < count; i++)
            {
                var index = (int)Math.Round((i + 0.5) * all.Count / count - 0.5);
                picked.Add(all[Math.Clamp(index, 0, all.Count - 1)]);
            }

            return picked;
        }

        private bool TryDecode(out VideoFrame frame, out double pts)
        {
            frame = null!;
            pts = 0;
            var packet = ffmpeg.av_packet_alloc();
            var raw = ffmpeg.av_frame_alloc();
            try
            {
                while (true)
                {
                    ffmpeg.av_packet_unref(packet);
                    var read = ffmpeg.av_read_frame(_format, packet);
                    if (FfmpegError.IsEof(read))
                        return false;
                    FfmpegError.Check(read, "读取数据包");
                    if (packet->stream_index != _streamIndex)
                        continue;
                    FfmpegError.Check(ffmpeg.avcodec_send_packet(_codec, packet), "送入解码器");
                    var received = ffmpeg.avcodec_receive_frame(_codec, raw);
                    if (received == ffmpeg.AVERROR(ffmpeg.EAGAIN))
                        continue;
                    FfmpegError.Check(received, "取出画面");
                    pts = Timestamp(raw);
                    frame = Convert(raw);
                    return true;
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

            return new VideoFrame
            {
                Width = _dstWidth,
                Height = _dstHeight,
                Stride = stride,
                Data = output
            };
        }

        private double Timestamp(AVFrame* frame)
        {
            var ticks = frame->best_effort_timestamp;
            if (ticks == ffmpeg.AV_NOPTS_VALUE)
                ticks = frame->pts;
            return ticks == ffmpeg.AV_NOPTS_VALUE ? 0 : ticks * Q2d(_timeBase);
        }

        public void Dispose()
        {
            if (_scaler != null)
                ffmpeg.sws_freeContext(_scaler);
            var codec = _codec;
            if (codec != null)
                ffmpeg.avcodec_free_context(&codec);
            _codec = null;
            var format = _format;
            if (format != null)
                ffmpeg.avformat_close_input(&format);
            _format = null;
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
