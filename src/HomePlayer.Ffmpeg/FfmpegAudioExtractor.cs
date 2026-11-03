using System.Diagnostics;
using System.Text;

namespace HomePlayer.Ffmpeg;

public static class FfmpegAudioExtractor
{
    public static void ExtractWav(string inputPath, int audioStreamIndex, string outputPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        FfmpegNative.Ensure();
        var executable = Path.Combine(FfmpegNative.Root, "ffmpeg.exe");
        if (!File.Exists(executable))
            throw new InvalidOperationException("缺少应用私有的 ffmpeg.exe。请恢复 FFmpeg 依赖。");
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        if (File.Exists(outputPath))
            File.Delete(outputPath);
        var info = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = FfmpegNative.Root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in new[]
                 {
                     "-hide_banner", "-nostats", "-loglevel", "error", "-y",
                     "-i", inputPath,
                     "-map", $"0:{audioStreamIndex}",
                     "-vn", "-ac", "1", "-ar", "16000",
                     "-c:a", "pcm_s16le", "-f", "wav", outputPath
                 })
            info.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = info };
        var errors = new StringBuilder();
        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data == null)
                return;
            if (errors.Length > 16_000)
                errors.Remove(0, errors.Length - 16_000);
            errors.AppendLine(args.Data);
        };
        try
        {
            if (!process.Start())
                throw new InvalidOperationException("无法启动 FFmpeg 音频提取进程。");
            process.BeginErrorReadLine();
            process.WaitForExitAsync(cancellationToken).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
        catch
        {
            TryKill(process);
            throw;
        }
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"FFmpeg 音频提取失败（{process.ExitCode}）。{errors}");
        if (!File.Exists(outputPath) || new FileInfo(outputPath).Length <= 44)
            throw new InvalidOperationException("FFmpeg 没有生成有效的识别音频。");
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
        }
    }
}
