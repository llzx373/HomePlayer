using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using HomePlayer.Core;

namespace HomePlayer.AI;

/// <summary>
/// 实时配音：常驻语音合成服务，按播放头逐句合成译文并写入 <see cref="LiveDubbingTrack"/>。
/// 识别和翻译由上层驱动，这里负责排队、说话人音色和时长适配。
/// </summary>
public sealed class LiveDubbingService : IAsyncDisposable
{
    private const double LookaheadSeconds = 20;
    private const double EvictBeforeSeconds = 5;
    private const double PollSeconds = 0.12;
    private const double SpeakerGraceSeconds = 2;

    private readonly AiModelManager _models;
    private readonly Func<string> _mirror;
    private readonly AiComputeDevice _device;
    private readonly DubbingEngine _engine;
    private readonly IReadOnlyList<VoiceProfile> _voices;
    private readonly VoiceProfile _defaultVoice;
    private readonly Func<double> _playhead;
    private readonly LiveDubbingScheduler _scheduler = new();
    private readonly LiveDubbingTrack _track = new();
    private readonly CancellationTokenSource _cancel = new();
    private readonly object _gate = new();
    private readonly object _speakerGate = new();
    private readonly List<LiveDubbingCue> _cues = [];
    private readonly HashSet<long> _assigned = [];
    private readonly Dictionary<int, string> _speakerVoices = [];
    private LiveSpeakerTimeline? _speakerTimeline;
    private bool _separateSpeakers;
    private string? _workDirectory;
    private CosyVoiceService? _cosyVoice;
    private TtsService? _zipVoice;
    private Task? _start;
    private Task? _pump;
    private string? _lastError;
    private long _generation;
    private int _disposed;

    public LiveDubbingService(
        AiModelManager models,
        Func<string> mirror,
        AiComputeDevice device,
        DubbingEngine engine,
        IReadOnlyList<VoiceProfile> voices,
        VoiceProfile defaultVoice,
        bool separateSpeakers,
        Func<double> playhead)
    {
        _models = models;
        _mirror = mirror;
        _device = device;
        _engine = engine;
        _voices = voices;
        _defaultVoice = defaultVoice;
        _separateSpeakers = separateSpeakers;
        _playhead = playhead;
    }

    public LiveDubbingTrack Track => _track;

    public double CoveredThrough => _scheduler.CoveredThrough;

    public string? LastError
    {
        get
        {
            lock (_gate)
                return _lastError;
        }
    }

    public Task StartAsync(IProgress<AiSubtitleProgress>? progress, CancellationToken cancellationToken) =>
        _start ??= StartCoreAsync(progress, cancellationToken);

    public void SetRecognizedThrough(double seconds) => _scheduler.SetRecognizedThrough(seconds);

    public void Register(IReadOnlyList<SubtitleCue> cues)
    {
        foreach (var cue in cues)
        {
            var key = KeyOf(cue);
            var start = cue.Start.TotalSeconds;
            var end = cue.End.TotalSeconds;
            _scheduler.Track(key, start, end);
            lock (_speakerGate)
            {
                if (!_cues.Any(item => item.Key == key))
                    _cues.Add(new LiveDubbingCue(key, start, end));
            }
        }

        SyncSpeakers();
    }

    public void SubmitTranslated(SubtitleCue cue) => _scheduler.Translate(KeyOf(cue), cue.Translation);

    public void SkipCue(SubtitleCue cue) => _scheduler.Skip(KeyOf(cue));

    public void SetSpeakerTimeline(LiveSpeakerTimeline? timeline)
    {
        lock (_speakerGate)
            _speakerTimeline = timeline;
        SyncSpeakers();
    }

    public void SetSeparateSpeakers(bool enabled)
    {
        lock (_speakerGate)
            _separateSpeakers = enabled;
        SyncSpeakers();
    }

    public void Reset(double position)
    {
        Interlocked.Increment(ref _generation);
        _scheduler.Reset(position);
        _track.Reset(position);
        lock (_speakerGate)
        {
            _cues.Clear();
            _assigned.Clear();
            _speakerVoices.Clear();
            _speakerTimeline = null;
        }
        lock (_gate)
            _lastError = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _cancel.Cancel();
        foreach (var running in new[] { _pump, _start })
        {
            if (running == null)
                continue;
            try
            {
                await running;
            }
            catch
            {
            }
        }

        _cancel.Dispose();
        if (_cosyVoice != null)
        {
            try
            {
                await _cosyVoice.DisposeAsync();
            }
            catch
            {
            }
        }

        if (_zipVoice != null)
        {
            try
            {
                await _zipVoice.DisposeAsync();
            }
            catch
            {
            }
        }

        var directory = _workDirectory;
        _workDirectory = null;
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

    private async Task StartCoreAsync(IProgress<AiSubtitleProgress>? progress, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(AppPaths.AiTemp, "live-dub-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        _workDirectory = directory;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cancel.Token);
        if (_engine == DubbingEngine.CosyVoice)
            await StartCosyVoiceAsync(directory, progress, linked.Token);
        else
            await StartZipVoiceAsync(progress, linked.Token);
        _pump = Task.Run(() => PumpAsync(_cancel.Token), CancellationToken.None);
    }

    private async Task StartCosyVoiceAsync(string directory, IProgress<AiSubtitleProgress>? progress, CancellationToken cancellationToken)
    {
        var model = await _models.EnsureAsync(AiSubtitleCatalog.Model("cosyvoice3-model"), _mirror(),
            new Progress<double>(value => Report(progress, "下载语音克隆模型", value * 0.4)), cancellationToken);
        var tokenizer = await _models.EnsureAsync(AiSubtitleCatalog.Model("cosyvoice-tokenizer"), _mirror(),
            new Progress<double>(value => Report(progress, "下载语音分词器", 0.4 + value * 0.1)), cancellationToken);
        var campplus = await _models.EnsureAsync(AiSubtitleCatalog.Model("cosyvoice-campplus"), _mirror(),
            new Progress<double>(value => Report(progress, "下载声纹模型", 0.5 + value * 0.1)), cancellationToken);

        var service = new CosyVoiceService(model, tokenizer, campplus, _device != AiComputeDevice.Cpu,
            Path.Combine(AppPaths.DubbingCache, "voices"));
        _cosyVoice = service;
        Report(progress, "准备参考音色", 0.65);
        var prompt = await PrepareVoiceAsync(service, cancellationToken);
        await service.StartAsync(new Dictionary<string, string>(StringComparer.Ordinal) { ["default"] = prompt },
            new Progress<AiSubtitleProgress>(value => Report(progress, value.Stage, 0.7 + value.Value * 0.3)),
            cancellationToken);
    }

    private async Task<string> PrepareVoiceAsync(CosyVoiceService service, CancellationToken cancellationToken)
    {
        var cacheKey = VoiceKey(_defaultVoice);
        var cacheDirectory = Path.Combine(AppPaths.DubbingCache, "voices");
        Directory.CreateDirectory(cacheDirectory);
        var cached = Path.Combine(cacheDirectory, cacheKey + ".gguf");
        if (File.Exists(cached))
            return cached;
        var prepared = await service.PrepareVoiceAsync(cacheKey, _defaultVoice.AudioPath, _defaultVoice.ReferenceText, cancellationToken);
        if (!string.Equals(prepared, cached, StringComparison.OrdinalIgnoreCase) && File.Exists(prepared))
            File.Move(prepared, cached, true);
        return cached;
    }

    private async Task StartZipVoiceAsync(IProgress<AiSubtitleProgress>? progress, CancellationToken cancellationToken)
    {
        var service = new TtsService(_models, _device, _mirror, Path.GetDirectoryName(_defaultVoice.AudioPath)!);
        _zipVoice = service;
        await service.StartAsync(progress, cancellationToken);
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        var index = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            SyncSpeakers();
            var playhead = _playhead();
            var job = _scheduler.Next(playhead, LookaheadSeconds);
            if (job == null)
            {
                _track.EvictBefore(playhead - EvictBeforeSeconds);
                await Task.Delay(TimeSpan.FromSeconds(PollSeconds), cancellationToken);
                continue;
            }

            var directory = _workDirectory;
            if (directory == null)
                break;
            var path = Path.Combine(directory, (++index).ToString("D6", CultureInfo.InvariantCulture) + ".wav");
            var generation = Volatile.Read(ref _generation);
            try
            {
                await SynthesizeAsync(job.Text, VoiceIdFor(job.Speaker), path, cancellationToken);
                if (generation != Volatile.Read(ref _generation))
                    continue;
                var (samples, sampleRate, channels) = WavFile.ReadPcm16(path);
                if (channels != 1 || sampleRate <= 0)
                {
                    _scheduler.Skip(job.Key);
                    continue;
                }

                var (fitted, _) = DubbingAudio.FitSamples(samples, sampleRate, job.EndSeconds - job.StartSeconds);
                var end = job.StartSeconds + fitted.Length / (double)sampleRate;
                _track.Add(new LiveDubbingSegment(job.Key, job.StartSeconds, end, sampleRate, fitted));
                _scheduler.Complete(job.Key);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lock (_gate)
                    _lastError = ex.Message;
                _scheduler.Skip(job.Key);
            }
            finally
            {
                try
                {
                    if (File.Exists(path))
                        File.Delete(path);
                }
                catch (IOException)
                {
                }
            }

            _track.EvictBefore(playhead - EvictBeforeSeconds);
        }
    }

    private void SyncSpeakers()
    {
        List<LiveDubbingCue> unassigned;
        LiveSpeakerTimeline? timeline;
        bool separate;
        lock (_speakerGate)
        {
            timeline = _speakerTimeline;
            separate = _separateSpeakers;
            unassigned = _cues.Where(cue => !_assigned.Contains(cue.Key)).ToList();
        }

        if (separate && timeline == null)
            return;

        foreach (var cue in unassigned)
        {
            var speaker = -1;
            if (separate)
            {
                speaker = timeline!.DominantSpeaker(cue.Start, cue.End);
                if (speaker < 0)
                    continue;
            }

            _scheduler.AssignSpeaker(cue.Key, speaker);
            lock (_speakerGate)
            {
                _assigned.Add(cue.Key);
                if (speaker >= 0)
                    EnsureVoiceLocked(speaker);
            }
        }

        if (!separate || timeline == null)
            return;
        if (_scheduler.ExpireSpeakers(timeline.DiarizedThrough, SpeakerGraceSeconds) > 0)
        {
            lock (_speakerGate)
            {
                foreach (var cue in _cues)
                {
                    if (!_assigned.Contains(cue.Key) && cue.End + SpeakerGraceSeconds <= timeline.DiarizedThrough)
                        _assigned.Add(cue.Key);
                }
            }
        }
    }

    private void EnsureVoiceLocked(int speaker)
    {
        if (_speakerVoices.ContainsKey(speaker))
            return;
        _speakerVoices[speaker] = _engine == DubbingEngine.CosyVoice
            ? "default"
            : PickPresetLocked(speaker);
    }

    private string PickPresetLocked(int speaker)
    {
        if (_voices.Count == 0)
            return _defaultVoice.Id;
        var used = new HashSet<string>(_speakerVoices.Values, StringComparer.Ordinal);
        var start = Math.Abs(speaker) % _voices.Count;
        for (var offset = 0; offset < _voices.Count; offset++)
        {
            var voice = _voices[(start + offset) % _voices.Count];
            if (!used.Contains(voice.Id))
                return voice.Id;
        }

        return _voices[start].Id;
    }

    private string VoiceIdFor(int speaker)
    {
        if (speaker >= 0)
        {
            lock (_speakerGate)
            {
                if (_separateSpeakers && _speakerVoices.TryGetValue(speaker, out var id))
                    return id;
            }
        }

        return _engine == DubbingEngine.CosyVoice ? "default" : _defaultVoice.Id;
    }

    private Task<(int SampleRate, long Samples)> SynthesizeAsync(string text, string voiceId, string path, CancellationToken cancellationToken) =>
        _cosyVoice != null
            ? _cosyVoice.SynthesizeAsync(text, voiceId, path, cancellationToken)
            : _zipVoice!.SynthesizeAsync(text, voiceId, path, 2, cancellationToken);

    private static long KeyOf(SubtitleCue cue) => (long)Math.Round(cue.Start.TotalMilliseconds);

    private static string VoiceKey(VoiceProfile voice)
    {
        var info = File.Exists(voice.AudioPath) ? new FileInfo(voice.AudioPath) : null;
        var fingerprint = string.Join('|',
            voice.Id,
            voice.AudioPath.ToLowerInvariant(),
            info?.Length.ToString(CultureInfo.InvariantCulture) ?? "0",
            info?.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture) ?? "0",
            voice.ReferenceText);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint)))[..16].ToLowerInvariant();
    }

    private static void Report(IProgress<AiSubtitleProgress>? progress, string stage, double value) =>
        progress?.Report(new AiSubtitleProgress(stage, Math.Clamp(value, 0, 1)));

    private sealed record LiveDubbingCue(long Key, double Start, double End);
}
