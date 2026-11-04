using HomePlayer.Core;

namespace HomePlayer.AI;

public sealed record AiSubtitleProgress(string Stage, double Value);
public sealed record AiSubtitleResult(string Path, IReadOnlyList<SubtitleCue> Cues, string Description);

public sealed class AiSubtitleService
{
    private readonly AiModelManager _models;
    private readonly WhisperRecognitionService _recognition;
    private readonly SherpaRecognitionService _sherpa;
    private readonly LlamaTranslationService _translation;
    private readonly Func<string> _mirror;

    public AiSubtitleService(string modelDirectory, Func<string> mirror, Func<string> proxy)
    {
        _models = new AiModelManager(modelDirectory, proxy);
        _recognition = new WhisperRecognitionService(_models);
        _sherpa = new SherpaRecognitionService(_models);
        _translation = new LlamaTranslationService(_models);
        _mirror = mirror;
    }

    public bool IsInstalled(string modelId) => _models.IsInstalled(AiSubtitleCatalog.Model(modelId));

    public string ModelDirectory => _models.ModelDirectory;

    public void ChangeModelDirectory(string directory) => _models.SetModelDirectory(directory);

    public Task<IReadOnlyList<SubtitleCue>> RecognizeAsync(
        string mediaPath,
        int audioTrackIndex,
        string? sourceLanguage,
        string modelId,
        AiComputeDevice device,
        IProgress<AiSubtitleProgress>? progress,
        CancellationToken cancellationToken) =>
        _recognition.RecognizeAsync(mediaPath, audioTrackIndex, sourceLanguage, modelId, _mirror(), device, progress, cancellationToken);

    public Task<IReadOnlyList<SubtitleCue>> TranslateAsync(
        IReadOnlyList<SubtitleCue> cues,
        string? sourceLanguage,
        string targetLanguage,
        bool bilingual,
        string modelId,
        AiComputeDevice device,
        IProgress<AiSubtitleProgress>? progress,
        CancellationToken cancellationToken) =>
        _translation.TranslateAsync(cues, sourceLanguage, targetLanguage, bilingual, modelId, _mirror(), device, progress, cancellationToken);

    public static bool LightweightTranslationRecommended(string? computeDevice) =>
        RecommendLightweightTranslation(computeDevice, AiRuntime.HasNvidiaGpu, AiRuntime.NvidiaMemoryMiB);

    public static bool RecommendLightweightTranslation(string? computeDevice, bool hasNvidiaGpu, int nvidiaMemoryMiB)
    {
        var device = AiComputeDeviceExtensions.Parse(computeDevice);
        if (device == AiComputeDevice.Cpu || !hasNvidiaGpu)
            return true;
        return nvidiaMemoryMiB > 0 && nvidiaMemoryMiB < 8192;
    }

    public LiveTranslationService CreateLiveTranslator(
        string modelId,
        string? sourceLanguage,
        string targetLanguage,
        AiComputeDevice device) =>
        new(_models, modelId, sourceLanguage, targetLanguage, device, _mirror);

    public bool SenseVoiceInstalled =>
        IsInstalled("sensevoice-model") && IsInstalled("sensevoice-tokens")
        && File.Exists(Path.Combine(AppContext.BaseDirectory, "ai", "sherpa-worker", "HomePlayer.AI.SherpaWorker.exe"));

    public Task<IReadOnlyList<SubtitleCue>> RecognizeWithSherpaAsync(
        string mediaPath,
        int audioTrackIndex,
        AiComputeDevice device,
        double startSeconds,
        IProgress<AiSubtitleProgress>? progress,
        Action<SubtitleSegmentUpdate>? segmentReady,
        CancellationToken cancellationToken,
        bool realtime = false,
        bool drafts = false) =>
        _sherpa.RecognizeAsync(mediaPath, audioTrackIndex, _mirror(), device, startSeconds, progress, segmentReady, cancellationToken, realtime, drafts);

    public AiSubtitleResult SaveBesideMedia(string mediaPath, IReadOnlyList<SubtitleCue> cues, string description)
    {
        var fullMediaPath = Path.GetFullPath(mediaPath);
        var directory = Path.GetDirectoryName(fullMediaPath)
            ?? throw new InvalidOperationException("无法确定视频所在目录。");
        var path = Path.Combine(directory, Path.GetFileNameWithoutExtension(fullMediaPath) + ".ai.srt");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            SubtitleFileWriter.WriteSrt(temporary, cues);
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
        return new AiSubtitleResult(path, cues, description);
    }

    public void SaveEdits(AiSubtitleResult result, IReadOnlyList<SubtitleCue> cues) =>
        SubtitleFileWriter.WriteSrt(result.Path, cues);
}
