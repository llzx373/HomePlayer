using System.Text;
using System.Text.Json;
using HomePlayer.Core;
using HomePlayer.Ffmpeg;

namespace HomePlayer.AI;

/// <summary>
/// 实时说话人分析：从当前位置抽一份 WAV，交给 SherpaWorker 的流式分离模式，
/// 增量维护 <see cref="LiveSpeakerTimeline"/>。分析和识别各用一份音轨，互不阻塞。
/// </summary>
public sealed class LiveSpeakerService : IAsyncDisposable
{
    private const double BlockSeconds = 60;
    private const double OverlapSeconds = 10;

    private readonly AiModelManager _models;
    private readonly Func<string> _mirror;
    private readonly LiveSpeakerTimeline _timeline = new();
    private readonly CancellationTokenSource _cancel = new();
    private readonly object _gate = new();
    private string? _directory;
    private string? _audioPath;
    private double _startSeconds;
    private Task? _extraction;
    private Task? _run;
    private string? _lastError;
    private int _disposed;

    public LiveSpeakerService(AiModelManager models, Func<string> mirror)
    {
        _models = models;
        _mirror = mirror;
    }

    public LiveSpeakerTimeline Timeline => _timeline;

    public string? AudioPath => _audioPath;

    public double StartSeconds => _startSeconds;

    public string? LastError
    {
        get
        {
            lock (_gate)
                return _lastError;
        }
    }

    public async Task StartAsync(
        string mediaPath,
        int audioTrackIndex,
        double startSeconds,
        AiComputeDevice device,
        IProgress<AiSubtitleProgress>? progress,
        CancellationToken cancellationToken)
    {
        var segmentationModel = AiSubtitleCatalog.Model("diarization-segmentation");
        var embeddingModel = AiSubtitleCatalog.Model("speaker-embedding");
        Report(progress, "检查说话人模型", 0.02);
        var segmentationDirectory = await _models.EnsureArchiveAsync(segmentationModel, _mirror(),
            new Progress<double>(value => Report(progress, "下载说话人分割模型", value * 0.4)), cancellationToken);
        var embeddingPath = await _models.EnsureAsync(embeddingModel, _mirror(),
            new Progress<double>(value => Report(progress, "下载声纹模型", 0.4 + value * 0.4)), cancellationToken);

        var directory = Path.Combine(AppPaths.AiTemp, "live-speakers-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        _directory = directory;
        _startSeconds = Math.Max(0, startSeconds);
        var audioPath = Path.Combine(directory, "audio.wav");
        _audioPath = audioPath;

        Report(progress, "提取说话人分析音轨", 0.82);
        _extraction = Task.Run(() =>
        {
            try
            {
                FfmpegAudioExtractor.ExtractWav(mediaPath, audioTrackIndex, audioPath, _cancel.Token, _startSeconds);
                File.WriteAllText(audioPath + ".done", "");
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                try
                {
                    File.WriteAllText(audioPath + ".error", ex.Message);
                }
                catch (IOException)
                {
                }
            }
        }, CancellationToken.None);

        var job = new LiveSpeakerJob(
            audioPath,
            Path.Combine(segmentationDirectory, "model.onnx"),
            embeddingPath,
            device != AiComputeDevice.Cpu && AiRuntime.HasNvidiaGpu,
            BlockSeconds,
            OverlapSeconds,
            _startSeconds);
        var jobPath = Path.Combine(directory, "job.json");
        File.WriteAllText(jobPath, JsonSerializer.Serialize(job));

        Report(progress, "分析说话人", 0.9);
        _run = Task.Run(async () =>
        {
            try
            {
                await AiProcess.RunAsync(
                    AiRuntime.SherpaWorkerExecutable,
                    ["--diarize-live", jobPath],
                    AiRuntime.SherpaWorkerDirectory,
                    Parse,
                    _cancel.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                lock (_gate)
                    _lastError = ex.Message;
            }
        }, CancellationToken.None);
    }

    public bool TryCutReference(double fromSeconds, double toSeconds, string outputPath)
    {
        var audioPath = _audioPath;
        return audioPath != null
            && LiveAudioClipReader.TryCut(audioPath, _startSeconds, fromSeconds, toSeconds, outputPath);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _cancel.Cancel();
        foreach (var task in new[] { _run, _extraction })
        {
            if (task == null)
                continue;
            try
            {
                await task;
            }
            catch
            {
            }
        }

        _cancel.Dispose();
        var directory = _directory;
        _directory = null;
        if (directory != null)
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

    private void Parse(string line)
    {
        if (!line.StartsWith('{'))
            return;
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var type))
                return;
            switch (type.GetString())
            {
                case "segment":
                    if (root.TryGetProperty("speaker", out var speaker)
                        && root.TryGetProperty("start", out var start)
                        && root.TryGetProperty("end", out var end))
                    {
                        _timeline.Add(new LiveSpeakerSegment(
                            speaker.GetInt32(),
                            start.GetDouble(),
                            end.GetDouble()));
                    }
                    break;
                case "through":
                    if (root.TryGetProperty("seconds", out var seconds))
                        _timeline.SetThrough(seconds.GetDouble());
                    break;
            }
        }
        catch (JsonException)
        {
        }
    }

    private static void Report(IProgress<AiSubtitleProgress>? progress, string stage, double value) =>
        progress?.Report(new AiSubtitleProgress(stage, Math.Clamp(value, 0, 1)));

    private sealed record LiveSpeakerJob(
        string AudioPath,
        string SegmentationModel,
        string EmbeddingModel,
        bool UseCuda,
        double BlockSeconds,
        double OverlapSeconds,
        double StartSeconds);
}
