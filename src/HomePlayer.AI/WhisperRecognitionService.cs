using System.Text.RegularExpressions;
using HomePlayer.Core;
using HomePlayer.Ffmpeg;

namespace HomePlayer.AI;

public sealed partial class WhisperRecognitionService(AiModelManager models)
{
    public async Task<IReadOnlyList<SubtitleCue>> RecognizeAsync(
        string mediaPath,
        int audioTrackIndex,
        string? language,
        string modelId,
        string mirror,
        AiComputeDevice device,
        IProgress<AiSubtitleProgress>? progress,
        CancellationToken cancellationToken)
    {
        var model = AiSubtitleCatalog.Model(modelId);
        if (model.Kind != AiModelKind.Recognition)
            throw new InvalidOperationException("所选模型不是语音识别模型。");
        Report(progress, "检查识别模型", 0.02);
        var modelPath = await models.EnsureAsync(model, mirror,
            new Progress<double>(value => Report(progress, "下载识别模型", value * 0.2)), cancellationToken);
        var vadPath = await models.EnsureAsync(AiSubtitleCatalog.Model("silero-vad"), mirror, null, cancellationToken);
        var useCuda = device == AiComputeDevice.Cuda
            || device == AiComputeDevice.Auto && AiRuntime.HasNvidiaGpu && AiRuntime.CudaWhisperAvailable;
        var directory = Path.Combine(AppPaths.AiTemp, Guid.NewGuid().ToString("N"));
        var wav = Path.Combine(directory, "audio.wav");
        var outputBase = Path.Combine(directory, "subtitle");
        Directory.CreateDirectory(directory);
        try
        {
            Report(progress, "提取所选音轨", 0.22);
            await Task.Run(() => FfmpegAudioExtractor.ExtractWav(mediaPath, audioTrackIndex, wav, cancellationToken), cancellationToken);
            try
            {
                await RunWhisper(useCuda, "CUDA GPU 语音识别");
            }
            catch (InvalidOperationException) when (device == AiComputeDevice.Auto && useCuda)
            {
                Report(progress, "GPU 识别失败，回退 CPU", 0.3);
                if (File.Exists(outputBase + ".srt"))
                    File.Delete(outputBase + ".srt");
                await RunWhisper(false, "CPU 语音识别");
            }

            var output = outputBase + ".srt";
            if (!File.Exists(output))
                throw new InvalidOperationException("语音识别没有生成字幕文件。");
            var cues = SubtitleText.ParseFile(output);
            if (cues.Count == 0)
                throw new InvalidOperationException("没有识别到可用对白，可以尝试指定正确语言或更换高质量模型。");
            Report(progress, "识别完成", 0.98);
            return cues;
        }
        finally
        {
            try
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, true);
            }
            catch (IOException)
            {
            }
        }

        async Task RunWhisper(bool cuda, string stage)
        {
            Report(progress, stage, 0.3);
            var executable = cuda ? AiRuntime.CudaWhisperExecutable : AiRuntime.WhisperExecutable;
            var workingDirectory = cuda ? AiRuntime.CudaWhisperDirectory : AiRuntime.WhisperDirectory;
            var arguments = new[]
            {
                "--model", modelPath,
                "--file", wav,
                "--language", string.IsNullOrWhiteSpace(language) ? "auto" : language,
                "--output-srt",
                "--output-file", outputBase,
                "--vad",
                "--vad-model", vadPath,
                "--print-progress",
                "--threads", Math.Max(1, Environment.ProcessorCount / 2).ToString()
            };
            if (!cuda)
                arguments = [.. arguments, "--no-gpu"];
            var lastProgress = -1.0;
            var lastProgressAt = 0L;
            await AiProcess.RunAsync(executable, arguments, workingDirectory, line =>
            {
                var match = ProgressLine().Match(line);
                if (!match.Success || !double.TryParse(match.Groups[1].Value, out var value))
                    return;
                var next = 0.3 + Math.Clamp(value, 0, 100) / 100 * 0.68;
                var now = Environment.TickCount64;
                if (lastProgress >= 0 && (next == lastProgress || now - lastProgressAt < 200))
                    return;
                lastProgress = next;
                lastProgressAt = now;
                Report(progress, stage, next);
            }, cancellationToken);
        }
    }

    private static void Report(IProgress<AiSubtitleProgress>? progress, string stage, double value) =>
        progress?.Report(new AiSubtitleProgress(stage, Math.Clamp(value, 0, 1)));

    [GeneratedRegex(@"(?:progress\s*=\s*)?(\d{1,3})\s*%", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ProgressLine();
}
