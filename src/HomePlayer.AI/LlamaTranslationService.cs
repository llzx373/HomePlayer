using System.Collections.Concurrent;
using System.Text.Json;
using HomePlayer.Core;

namespace HomePlayer.AI;

public sealed class LlamaTranslationService(AiModelManager models)
{
    public async Task<IReadOnlyList<SubtitleCue>> TranslateAsync(
        IReadOnlyList<SubtitleCue> cues,
        string? sourceLanguage,
        string targetLanguage,
        string modelId,
        string mirror,
        AiComputeDevice device,
        IProgress<AiSubtitleProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (cues.Count == 0)
            throw new InvalidOperationException("源字幕没有可翻译的文本。");
        var model = AiSubtitleCatalog.Model(modelId);
        if (model.Kind != AiModelKind.Translation)
            throw new InvalidOperationException("所选模型不是翻译模型。");
        Report(progress, "检查翻译模型", 0.02);
        var modelPath = await models.EnsureAsync(model, mirror,
            new Progress<double>(value => Report(progress, "下载翻译模型", value * 0.25)), cancellationToken);
        var batches = Batches(cues);
        var requests = batches.Select((batch, index) =>
        {
            var inputs = batch.Select((cue, offset) => new SubtitleTranslationInput(offset, cue.Text)).ToArray();
            return new WorkerRequest(index, AiTranslationText.Prompt(inputs, sourceLanguage ?? "", targetLanguage), inputs.Length);
        }).ToArray();
        var directory = Path.Combine(AppPaths.AiTemp, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var jobPath = Path.Combine(directory, "translation.json");
        try
        {
            Report(progress, "启动本地翻译模型", 0.28);
            var outputs = new ConcurrentDictionary<int, string>();
            Action<string> handle = line =>
            {
                try
                {
                    var response = JsonSerializer.Deserialize<WorkerResponse>(line);
                    if (response != null)
                    {
                        outputs[response.Id] = response.Text;
                        Report(progress, $"本地字幕翻译 {outputs.Count}/{batches.Count}", 0.3 + 0.68 * outputs.Count / batches.Count);
                    }
                }
                catch (JsonException)
                {
                }
            };
            Task RunWorker(bool useGpu, bool allowFallback)
            {
                File.WriteAllText(jobPath, JsonSerializer.Serialize(new WorkerJob(modelPath, requests, useGpu, allowFallback)));
                return AiProcess.RunAsync(AiRuntime.WorkerExecutable, [jobPath], AiRuntime.WorkerDirectory, handle, cancellationToken);
            }
            try
            {
                await RunWorker(device != AiComputeDevice.Cpu, device == AiComputeDevice.Auto);
            }
            catch (InvalidOperationException) when (device == AiComputeDevice.Auto && outputs.IsEmpty)
            {
                outputs.Clear();
                Report(progress, "GPU 翻译启动失败，回退 CPU", 0.28);
                await RunWorker(false, true);
            }
            var translated = new string[cues.Count];
            for (var batchIndex = 0; batchIndex < batches.Count; batchIndex++)
            {
                if (!outputs.TryGetValue(batchIndex, out var output))
                    throw new InvalidOperationException("本地翻译进程没有返回完整结果。");
                var batch = batches[batchIndex];
                var inputs = batch.Select((cue, offset) => new SubtitleTranslationInput(offset, cue.Text)).ToArray();
                var map = AiTranslationText.Parse(output, inputs.Select(input => input.Id));
                if (map.Count != inputs.Length && inputs.Length == 1)
                {
                    var single = AiTranslationText.CleanModelOutput(output);
                    if (!string.IsNullOrWhiteSpace(single))
                        map = new Dictionary<int, string> { [inputs[0].Id] = single };
                }
                if (map.Count != inputs.Length)
                    throw new InvalidOperationException("翻译结果缺少条目或 ID 不匹配，已停止保存以避免字幕错位。");
                for (var cueIndex = 0; cueIndex < batch.Count; cueIndex++)
                    translated[batch[cueIndex].Index] = map[cueIndex];
            }

            var result = cues.Select((cue, index) => cue with
            {
                Text = cue.Text.Trim(),
                Translation = string.IsNullOrWhiteSpace(translated[index]) ? null : translated[index].Trim(),
                Bitmaps = [],
                AssEvent = null
            }).ToArray();
            Report(progress, "翻译完成", 0.99);
            return result;
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

    private static List<IReadOnlyList<IndexedCue>> Batches(IReadOnlyList<SubtitleCue> cues)
    {
        var result = new List<IReadOnlyList<IndexedCue>>();
        var batch = new List<IndexedCue>();
        var length = 0;
        for (var index = 0; index < cues.Count; index++)
        {
            var cue = new IndexedCue(index, cues[index].Text);
            if (batch.Count > 0 && (batch.Count >= 6 || length + cue.Text.Length > 1200))
            {
                result.Add(batch);
                batch = [];
                length = 0;
            }
            batch.Add(cue);
            length += cue.Text.Length;
        }
        if (batch.Count > 0)
            result.Add(batch);
        return result;
    }

    private static void Report(IProgress<AiSubtitleProgress>? progress, string stage, double value) =>
        progress?.Report(new AiSubtitleProgress(stage, Math.Clamp(value, 0, 1)));

    private sealed record IndexedCue(int Index, string Text);
    private sealed record WorkerJob(string ModelPath, WorkerRequest[] Requests, bool UseGpu, bool AllowCpuFallback);
    private sealed record WorkerRequest(int Id, string Prompt, int Count);
    private sealed record WorkerResponse(int Id, string Text);
}
