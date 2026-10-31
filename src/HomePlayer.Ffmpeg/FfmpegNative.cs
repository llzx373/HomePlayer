using FFmpeg.AutoGen;
using HomePlayer.Core;

namespace HomePlayer.Ffmpeg;

public static class FfmpegNative
{
    private static readonly object Gate = new();
    private static readonly string[] RequiredFiles =
    [
        "avcodec-63.dll", "avdevice-63.dll", "avfilter-12.dll", "avformat-63.dll",
        "avutil-61.dll", "swresample-7.dll", "swscale-10.dll"
    ];
    private static bool _ready;

    public static string Root { get; private set; } = "";

    public static void Ensure()
    {
        lock (Gate)
        {
            if (_ready)
                return;
            Root = Resolve();
            NativeDependencyIntegrity.Verify(Root, "ffmpeg", RequiredFiles);
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
        var directory = Path.Combine(AppContext.BaseDirectory, "ffmpeg");
        if (Directory.Exists(directory) && File.Exists(Path.Combine(directory, "avcodec-63.dll")))
            return directory;
        throw new InvalidOperationException("未找到应用私有的 FFmpeg 动态库。");
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
