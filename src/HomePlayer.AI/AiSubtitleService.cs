using System.Security.Cryptography;
using System.Text;
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

    public bool SenseVoiceInstalled =>
        IsInstalled("sensevoice-model") && IsInstalled("sensevoice-tokens")
        && File.Exists(Path.Combine(AppContext.BaseDirectory, "ai", "sherpa-worker", "HomePlayer.AI.SherpaWorker.exe"));

    public Task<IReadOnlyList<SubtitleCue>> RecognizeWithSherpaAsync(
        string mediaPath,
        int audioTrackIndex,
        AiComputeDevice device,
        double startSeconds,
        IProgress<AiSubtitleProgress>? progress,
        Action<SubtitleCue>? cueReady,
        CancellationToken cancellationToken) =>
        _sherpa.RecognizeAsync(mediaPath, audioTrackIndex, _mirror(), device, startSeconds, progress, cueReady, cancellationToken);

    public AiSubtitleResult Save(string mediaPath, IReadOnlyList<SubtitleCue> cues, string description)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(mediaPath).ToUpperInvariant())))[..16].ToLowerInvariant();
        var directory = Path.Combine(AppPaths.AiSubtitles, hash);
        var path = Path.Combine(directory, $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.srt");
        SubtitleFileWriter.WriteSrt(path, cues);
        return new AiSubtitleResult(path, cues, description);
    }

    public void SaveEdits(AiSubtitleResult result, IReadOnlyList<SubtitleCue> cues) =>
        SubtitleFileWriter.WriteSrt(result.Path, cues);
}
