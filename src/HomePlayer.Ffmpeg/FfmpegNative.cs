using FFmpeg.AutoGen;

namespace HomePlayer.Ffmpeg;

public static class FfmpegNative
{
    private static readonly object Gate = new();
    private static bool _ready;

    public static string Root { get; private set; } = "";

    public static void Ensure()
    {
        lock (Gate)
        {
            if (_ready)
                return;
            Root = Resolve();
            ffmpeg.RootPath = Root;
            try
            {
                _ = ffmpeg.avcodec_version();
                ffmpeg.avformat_network_init();
                _ready = true;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"无法加载 FFmpeg 动态库（{Root}）。{ex.Message}", ex);
            }
        }
    }

    public static string Resolve()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "ffmpeg"),
            AppContext.BaseDirectory,
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "third_party", "ffmpeg"))
        };
        foreach (var dir in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (Directory.Exists(dir) && Directory.EnumerateFiles(dir, "avcodec-*.dll").Any())
                return dir;
        }

        throw new InvalidOperationException("未找到随身的 FFmpeg 动态库。");
    }
}

public static unsafe class FfmpegError
{
    public static void Check(int code, string action)
    {
        if (code >= 0)
            return;
        var buffer = stackalloc byte[512];
        ffmpeg.av_strerror(code, buffer, 512);
        var message = System.Runtime.InteropServices.Marshal.PtrToStringAnsi((IntPtr)buffer) ?? code.ToString();
        throw new InvalidOperationException($"{action}失败：{message}");
    }

    public static bool IsEof(int code) => code == ffmpeg.AVERROR_EOF;
}
