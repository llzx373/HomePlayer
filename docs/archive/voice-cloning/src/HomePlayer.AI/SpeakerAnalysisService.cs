using System.Text.Json;
using HomePlayer.Core;
using HomePlayer.Ffmpeg;

namespace HomePlayer.AI;

public sealed record SpeakerSegment(int Speaker, double StartSeconds, double EndSeconds);

/// <summary>
/// 说话人分析结果，持有临时提取的音轨；释放时删除临时目录。
/// </summary>
public sealed class SpeakerAnalysis : IDisposable
{
    private readonly string _directory;

    internal SpeakerAnalysis(string directory, string audioPath, IReadOnlyList<SpeakerSegment> segments)
    {
        _directory = directory;
        AudioPath = audioPath;
        Segments = segments;
    }

    /// <summary>测试用：不持有临时目录。</summary>
    public SpeakerAnalysis(string audioPath, IReadOnlyList<SpeakerSegment> segments)
    {
        _directory = "";
        AudioPath = audioPath;
        Segments = segments;
    }

    public string AudioPath { get; }

    public IReadOnlyList<SpeakerSegment> Segments { get; }

    public int SpeakerCount => Segments.Select(segment => segment.Speaker).Distinct().Count();

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, true);
        }
        catch (IOException)
        {
        }
    }
}

public sealed class SpeakerAnalysisService(AiModelManager models, Func<string> mirror)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public async Task<SpeakerAnalysis> AnalyzeAsync(
        string mediaPath,
        int audioTrackIndex,
        AiComputeDevice device,
        IProgress<AiSubtitleProgress>? progress,
        CancellationToken cancellationToken)
    {
        var segmentationModel = AiSubtitleCatalog.Model("diarization-segmentation");
        var embeddingModel = AiSubtitleCatalog.Model("speaker-embedding");
        Report(progress, "检查说话人模型", 0.02);
        var segmentationDirectory = await models.EnsureArchiveAsync(segmentationModel, mirror(),
            new Progress<double>(value => Report(progress, "下载说话人分割模型", value * 0.4)), cancellationToken);
        var embeddingPath = await models.EnsureAsync(embeddingModel, mirror(),
            new Progress<double>(value => Report(progress, "下载声纹模型", 0.4 + value * 0.4)), cancellationToken);

        var directory = Path.Combine(AppPaths.AiTemp, "speakers-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var audioPath = Path.Combine(directory, "audio.wav");
            Report(progress, "提取音轨", 0.82);
            await Task.Run(() => FfmpegAudioExtractor.ExtractWav(mediaPath, audioTrackIndex, audioPath, cancellationToken), cancellationToken);

            var job = new DiarizeJob(
                audioPath,
                Path.Combine(segmentationDirectory, "model.onnx"),
                embeddingPath,
                device == AiComputeDevice.Cuda,
                300);
            var jobPath = Path.Combine(directory, "diarize.json");
            File.WriteAllText(jobPath, JsonSerializer.Serialize(job, Json));

            Report(progress, "分析说话人", 0.85);
            var segments = new List<SpeakerSegment>();
            var done = false;
            await AiProcess.RunAsync(AiRuntime.SherpaWorkerExecutable, ["--diarize", jobPath], AiRuntime.SherpaWorkerDirectory,
                line =>
                {
                    if (!line.StartsWith('{'))
                        return;
                    try
                    {
                        using var document = JsonDocument.Parse(line);
                        var root = document.RootElement;
                        if (!root.TryGetProperty("type", out var type))
                            return;
                        if (type.GetString() == "done")
                        {
                            done = true;
                            return;
                        }

                        if (type.GetString() != "segment"
                            || !root.TryGetProperty("speaker", out var speaker)
                            || !root.TryGetProperty("start", out var start)
                            || !root.TryGetProperty("end", out var end))
                            return;
                        var id = speaker.GetInt32();
                        if (id < 0)
                            return;
                        segments.Add(new SpeakerSegment(id, start.GetDouble(), end.GetDouble()));
                    }
                    catch (JsonException)
                    {
                    }
                }, cancellationToken);

            if (!done && segments.Count == 0)
                throw new InvalidOperationException("说话人分析没有返回结果。");
            Report(progress, "说话人分析完成", 1);
            return new SpeakerAnalysis(directory, audioPath, segments);
        }
        catch
        {
            try
            {
                Directory.Delete(directory, true);
            }
            catch (IOException)
            {
            }

            throw;
        }
    }

    private static void Report(IProgress<AiSubtitleProgress>? progress, string stage, double value) =>
        progress?.Report(new AiSubtitleProgress(stage, Math.Clamp(value, 0, 1)));

    private sealed record DiarizeJob(
        string AudioPath,
        string SegmentationModel,
        string EmbeddingModel,
        bool UseCuda,
        double BlockSeconds);
}
