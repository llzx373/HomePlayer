using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using HomePlayer.Core;

namespace HomePlayer.Ffmpeg;

public static unsafe class FfmpegProbe
{
    public static MediaProbe Read(string path)
    {
        FfmpegNative.Ensure();
        AVFormatContext* format = null;
        try
        {
            format = ffmpeg.avformat_alloc_context();
            var opened = format;
            FfmpegError.Check(ffmpeg.avformat_open_input(&opened, path, null, null), "打开文件");
            format = opened;
            FfmpegError.Check(ffmpeg.avformat_find_stream_info(format, null), "读取媒体信息");

            AVCodec* videoCodec = null;
            var videoIndex = ffmpeg.av_find_best_stream(format, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, &videoCodec, 0);
            AVCodec* audioCodec = null;
            var audioIndex = ffmpeg.av_find_best_stream(format, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, &audioCodec, 0);
            var duration = format->duration > 0 ? format->duration / (double)ffmpeg.AV_TIME_BASE : 0;
            var width = 0;
            var height = 0;
            if (videoIndex >= 0)
            {
                var stream = format->streams[videoIndex];
                width = stream->codecpar->width;
                height = stream->codecpar->height;
            }

            return new MediaProbe(
                duration,
                width,
                height,
                videoCodec == null ? null : ffmpeg.avcodec_get_name(videoCodec->id),
                audioCodec == null ? null : ffmpeg.avcodec_get_name(audioCodec->id),
                format->iformat == null ? null : Marshal.PtrToStringAnsi((IntPtr)format->iformat->name));
        }
        finally
        {
            if (format != null)
                ffmpeg.avformat_close_input(&format);
        }
    }
}
