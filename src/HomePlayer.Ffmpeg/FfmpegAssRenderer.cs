using FFmpeg.AutoGen;
using HomePlayer.Core;

namespace HomePlayer.Ffmpeg;

public sealed unsafe class FfmpegAssRenderer : IDisposable
{
    private readonly object _gate = new();
    private readonly AVFilterGraph* _graph;
    private readonly AVFilterContext* _source;
    private readonly AVFilterContext* _sink;
    private readonly int _width;
    private readonly int _height;

    public FfmpegAssRenderer(string assPath, int width, int height, string? fontDirectory = null)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        FfmpegNative.Ensure();
        _width = width;
        _height = height;
        var options = $"filename='{assPath}':alpha=1";
        if (!string.IsNullOrWhiteSpace(fontDirectory))
            options += $":fontsdir='{fontDirectory}'";
        _graph = ffmpeg.avfilter_graph_alloc();
        if (_graph == null)
            throw new InvalidOperationException("无法创建 ASS 滤镜图。");
        try
        {
            AVFilterContext* source = null;
            AVFilterContext* sink = null;
            FfmpegError.Check(ffmpeg.avfilter_graph_create_filter(
                &source,
                ffmpeg.avfilter_get_by_name("buffer"),
                "ass-source",
                $"video_size={width}x{height}:pix_fmt=rgba:time_base=1/1000",
                null,
                _graph), "创建 ASS 输入");
            FfmpegError.Check(ffmpeg.avfilter_graph_create_filter(
                &sink,
                ffmpeg.avfilter_get_by_name("ass"),
                "ass-render",
                options,
                null,
                _graph), "创建 libass 渲染器");
            AVFilterContext* output = null;
            FfmpegError.Check(ffmpeg.avfilter_graph_create_filter(
                &output,
                ffmpeg.avfilter_get_by_name("buffersink"),
                "ass-output",
                null,
                null,
                _graph), "创建 ASS 输出");
            FfmpegError.Check(ffmpeg.avfilter_link(source, 0, sink, 0), "连接 ASS 输入");
            FfmpegError.Check(ffmpeg.avfilter_link(sink, 0, output, 0), "连接 ASS 输出");
            FfmpegError.Check(ffmpeg.avfilter_graph_config(_graph, null), "配置 ASS 滤镜图");
            _source = source;
            _sink = output;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public static bool IsAvailable()
    {
        try
        {
            FfmpegNative.Ensure();
            return ffmpeg.avfilter_get_by_name("ass") != null;
        }
        catch
        {
            return false;
        }
    }

    public SubtitleBitmap Render(TimeSpan position)
    {
        lock (_gate)
        {
            AVFrame* input = null;
            AVFrame* output = null;
            try
            {
                input = ffmpeg.av_frame_alloc();
                if (input == null)
                    throw new InvalidOperationException("无法分配 ASS 输入帧。");
                input->format = (int)AVPixelFormat.AV_PIX_FMT_RGBA;
                input->width = _width;
                input->height = _height;
                input->pts = (long)Math.Max(0, position.TotalMilliseconds);
                FfmpegError.Check(ffmpeg.av_frame_get_buffer(input, 32), "分配 ASS 输入像素");
                FfmpegError.Check(ffmpeg.av_frame_make_writable(input), "初始化 ASS 输入像素");
                for (var y = 0; y < _height; y++)
                {
                    var row = input->data[0] + y * input->linesize[0];
                    for (var x = 0; x < _width * 4; x++)
                        row[x] = 0;
                }
                FfmpegError.Check(ffmpeg.av_buffersrc_add_frame(_source, input), "提交 ASS 输入帧");
                input = null;
                output = ffmpeg.av_frame_alloc();
                if (output == null)
                    throw new InvalidOperationException("无法分配 ASS 输出帧。");
                var result = ffmpeg.av_buffersink_get_frame(_sink, output);
                FfmpegError.Check(result, "读取 ASS 渲染帧");
                var bgra = new byte[_width * _height * 4];
                for (var y = 0; y < _height; y++)
                {
                    var row = output->data[0] + y * output->linesize[0];
                    for (var x = 0; x < _width; x++)
                    {
                        var destination = (y * _width + x) * 4;
                        bgra[destination] = row[x * 4 + 2];
                        bgra[destination + 1] = row[x * 4 + 1];
                        bgra[destination + 2] = row[x * 4];
                        bgra[destination + 3] = row[x * 4 + 3];
                    }
                }
                return new SubtitleBitmap(0, 0, _width, _height, bgra);
            }
            finally
            {
                if (input != null)
                    ffmpeg.av_frame_free(&input);
                if (output != null)
                    ffmpeg.av_frame_free(&output);
            }
        }
    }

    public void Dispose()
    {
        var graph = _graph;
        if (graph != null)
            ffmpeg.avfilter_graph_free(&graph);
    }
}
