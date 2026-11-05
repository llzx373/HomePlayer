using System.Diagnostics;
using System.Globalization;
using HomePlayer.Core;

namespace HomePlayer.AI;

internal static class AiRuntime
{
    public static string WhisperDirectory => Directory("whisper");
    public static string CudaWhisperDirectory => Directory("whisper-cuda");
    public static string WorkerDirectory => Directory("worker");
    public static string SherpaWorkerDirectory => Directory("sherpa-worker");
    public static string TtsWorkerDirectory => Directory("tts-worker");

    public static string WhisperExecutable => Verify(WhisperDirectory, "whisper", "whisper-cli.exe");
    public static string CudaWhisperExecutable => Verify(CudaWhisperDirectory, "whisper-cuda", "whisper-cli.exe");
    public static string WorkerExecutable => Path.Combine(WorkerDirectory, "HomePlayer.AI.Worker.exe");
    public static string SherpaWorkerExecutable => Path.Combine(SherpaWorkerDirectory, "HomePlayer.AI.SherpaWorker.exe");
    public static string TtsWorkerExecutable => Path.Combine(TtsWorkerDirectory, "HomePlayer.AI.TtsWorker.exe");
    public static bool HasNvidiaGpu => File.Exists(Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe"));

    public static int NvidiaMemoryMiB
    {
        get
        {
            if (!HasNvidiaGpu)
                return 0;
            try
            {
                var info = new ProcessStartInfo("nvidia-smi", "--query-gpu=memory.total --format=csv,noheader,nounits")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var process = Process.Start(info);
                if (process == null)
                    return 0;
                var text = process.StandardOutput.ReadToEnd();
                if (!process.WaitForExit(1500))
                {
                    try
                    {
                        process.Kill();
                    }
                    catch (InvalidOperationException)
                    {
                    }
                    return 0;
                }
                var first = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
                return int.TryParse(first, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
            }
            catch (Exception)
            {
                return 0;
            }
        }
    }
    public static bool CudaWhisperAvailable => File.Exists(Path.Combine(AppContext.BaseDirectory, "ai", "whisper-cuda", "whisper-cli.exe"));

    private static string Directory(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "ai", name);
        if (!System.IO.Directory.Exists(path))
            throw new InvalidOperationException($"缺少本地 AI 运行时 ai/{name}。请运行 scripts/Restore-AI.ps1 或重新安装完整版本。");
        return path;
    }

    private static string Verify(string directory, string relative, string executable)
    {
        NativeDependencyIntegrity.Verify(directory, "ai/" + relative, [executable]);
        return Path.Combine(directory, executable);
    }
}
