using System.Text.Json;
using System.Text.RegularExpressions;
using HomePlayer.Core;
using HomePlayer.Ffmpeg;

namespace HomePlayer.AI;

public sealed partial class SherpaRecognitionService(AiModelManager models)
{
    public async Task<IReadOnlyList<SubtitleCue>> RecognizeAsync(
        string mediaPath,
        int audioTrackIndex,
        string mirror,
        AiComputeDevice device,
        double startSeconds,
        IProgress<AiSubtitleProgress>? progress,
        Action<SubtitleCue>? cueReady,
        CancellationToken cancellationToken,
        bool realtime = false)
    {
        var model = AiSubtitleCatalog.Model("sensevoice-model");
        var tokens = AiSubtitleCatalog.Model("sensevoice-tokens");
        Report(progress, "检查 SenseVoice 模型", 0.02);
        await models.EnsureAsync(model, mirror,
            new Progress<double>(value => Report(progress, "下载 SenseVoice 模型", value * 0.2)), cancellationToken);
        await models.EnsureAsync(tokens, mirror, null, cancellationToken);
        try
        {
            await models.EnsureAsync(AiSubtitleCatalog.Model("silero-vad-onnx"), mirror, null, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
        }
        var directory = Path.Combine(AppPaths.AiTemp, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var wav = Path.Combine(directory, "audio.wav");
        var jobPath = Path.Combine(directory, "sherpa.json");
        var useCuda = device == AiComputeDevice.Cuda || device == AiComputeDevice.Auto && AiRuntime.HasNvidiaGpu;
        File.WriteAllText(jobPath, JsonSerializer.Serialize(new SherpaJob(
            Path.GetDirectoryName(models.PathFor(model))!, wav, startSeconds, 4, 20, useCuda, realtime)));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var extract = Task.Run(() =>
        {
            try
            {
                FfmpegAudioExtractor.ExtractWav(mediaPath, audioTrackIndex, wav, linked.Token, startSeconds);
                File.WriteAllText(wav + ".done", "");
            }
            catch
            {
                File.WriteAllText(wav + ".error", "");
                throw;
            }
        }, linked.Token);
        var cues = new List<SubtitleCue>();
        try
        {
            Report(progress, useCuda ? "Sherpa CUDA 实时识别" : "Sherpa CPU 识别", 0.25);
            await AiProcess.RunAsync(AiRuntime.SherpaWorkerExecutable, [jobPath], AiRuntime.SherpaWorkerDirectory, line =>
            {
                try
                {
                    var result = JsonSerializer.Deserialize<SherpaResult>(line);
                    if (result == null)
                        return;
                    var text = MetadataTags().Replace(result.Text, "").Trim();
                    var start = TimeSpan.FromSeconds(result.StartSeconds);
                    var end = TimeSpan.FromSeconds(Math.Max(result.StartSeconds + 0.1, result.EndSeconds));
                    var formatted = end > start ? SubtitleCueFormatter.Build(text, start, end) : [];
                    foreach (var cue in formatted)
                    {
                        cues.Add(cue);
                        cueReady?.Invoke(cue);
                    }
                    if (formatted.Count > 0)
                        Report(progress, $"已识别至 {end:mm\\:ss}", Math.Min(0.95, 0.25 + cues.Count * 0.005));
                }
                catch (JsonException)
                {
                }
            }, linked.Token);
            await extract;
            Report(progress, "Sherpa 识别完成", 0.99);
            return cues;
        }
        catch
        {
            linked.Cancel();
            try
            {
                await extract;
            }
            catch
            {
            }
            throw;
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
    }

    private static void Report(IProgress<AiSubtitleProgress>? progress, string stage, double value) =>
        progress?.Report(new AiSubtitleProgress(stage, Math.Clamp(value, 0, 1)));

    [GeneratedRegex(@"<\|[^|]+\|>", RegexOptions.CultureInvariant)]
    private static partial Regex MetadataTags();

    private sealed record SherpaJob(string ModelDirectory, string AudioPath, double StartSeconds, double MinChunkSeconds, double MaxChunkSeconds, bool UseCuda, bool Realtime);
    private sealed record SherpaResult(int Id, double StartSeconds, double EndSeconds, string Text);
}
