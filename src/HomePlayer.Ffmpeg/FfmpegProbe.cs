using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using HomePlayer.Core;

namespace HomePlayer.Ffmpeg;

public static unsafe class FfmpegProbe
{
    private static readonly AVIOInterruptCB_callback InterruptCallback = OnInterrupt;

    public static MediaProbe Read(string path) => Read(path, CancellationToken.None);

    public static MediaProbe Read(string path, CancellationToken cancellationToken)
    {
        FfmpegNative.Ensure();
        cancellationToken.ThrowIfCancellationRequested();
        var interruptHandle = GCHandle.Alloc(cancellationToken);
        AVFormatContext* format = null;
        try
        {
            format = ffmpeg.avformat_alloc_context();
            if (format == null)
                throw new InvalidOperationException("无法分配媒体探测上下文。");
            format->interrupt_callback = new AVIOInterruptCB
            {
                callback = InterruptCallback,
                opaque = GCHandle.ToIntPtr(interruptHandle).ToPointer()
            };
            var profile = FfmpegInputProfile.ForPath(path);
            AVDictionary* options = null;
            if (profile.IsUnc)
            {
                FfmpegError.Check(ffmpeg.av_dict_set(&options, "probesize", profile.ProbeSize.ToString(), 0), "配置 UNC 探测预算");
                FfmpegError.Check(ffmpeg.av_dict_set(&options, "analyzeduration", profile.AnalyzeDurationMicroseconds.ToString(), 0), "配置 UNC 分析预算");
                FfmpegError.Check(ffmpeg.av_dict_set(&options, "buffer_size", profile.ReadBufferSize.ToString(), 0), "配置 UNC 播放预读");
                FfmpegError.Check(ffmpeg.av_dict_set(&options, "rw_timeout", profile.ReadWriteTimeoutMicroseconds.ToString(), 0), "配置 UNC 读写超时");
            }
            var opened = format;
            int openResult;
            try
            {
                openResult = ffmpeg.avformat_open_input(&opened, path, null, &options);
            }
            finally
            {
                ffmpeg.av_dict_free(&options);
            }
            format = opened;
            if (openResult < 0)
                cancellationToken.ThrowIfCancellationRequested();
            FfmpegError.Check(openResult, "打开文件");
            var infoResult = ffmpeg.avformat_find_stream_info(format, null);
            if (infoResult < 0)
                cancellationToken.ThrowIfCancellationRequested();
            FfmpegError.Check(infoResult, "读取媒体信息");

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

            var audioStreams = 0;
            var subtitleStreams = 0;
            for (var i = 0; i < format->nb_streams; i++)
            {
                var type = format->streams[i]->codecpar->codec_type;
                if (type == AVMediaType.AVMEDIA_TYPE_AUDIO)
                    audioStreams++;
                else if (type == AVMediaType.AVMEDIA_TYPE_SUBTITLE)
                    subtitleStreams++;
            }

            return new MediaProbe(
                duration,
                width,
                height,
                videoCodec == null ? null : ffmpeg.avcodec_get_name(videoCodec->id),
                audioCodec == null ? null : ffmpeg.avcodec_get_name(audioCodec->id),
                format->iformat == null ? null : Marshal.PtrToStringAnsi((IntPtr)format->iformat->name))
            {
                AudioStreamCount = audioStreams,
                SubtitleStreamCount = subtitleStreams
            };
        }
        finally
        {
            if (format != null)
                ffmpeg.avformat_close_input(&format);
            if (interruptHandle.IsAllocated)
                interruptHandle.Free();
        }
    }

    private static int OnInterrupt(void* opaque) =>
        GCHandle.FromIntPtr((IntPtr)opaque).Target is CancellationToken token && token.IsCancellationRequested ? 1 : 0;
}
