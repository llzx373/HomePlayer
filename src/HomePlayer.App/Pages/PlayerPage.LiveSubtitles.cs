using HomePlayer.AI;
using HomePlayer.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HomePlayer;

public sealed partial class PlayerPage
{
    private CancellationTokenSource? _liveSubtitles;
    private int _liveSubtitleGeneration;
    private readonly List<LiveCueEntry> _liveEntries = [];
    private readonly LiveSubtitleBuffer _liveBuffer = new();
    private bool _liveReadyShown;
    private readonly List<Task> _liveTranslationTasks = [];
    private readonly object _liveTranslationLock = new();
    private LiveTranslationService? _liveTranslation;
    private CancellationTokenSource? _aiGeneration;
    private AiGenerationMenuFlyoutItem? _aiGenerationMenuItem;
    private string? _aiGenerationPath;
    private string _aiGenerationStage = "";
    private double _aiGenerationProgress;
    private int _aiGenerationVersion;

    private void SelectLiveSubtitles()
    {
        var path = Playback.Playlist.Current?.Path;
        if (string.IsNullOrWhiteSpace(path))
        {
            ShowStatusHold("请先打开一个媒体文件。");
            return;
        }
        if (!App.Host.Settings.AiSubtitlesEnabled)
        {
            ShowStatusHold("请先在设置的“AI 字幕”标签页中启用 AI 字幕。");
            return;
        }
        if (Playback.GetAudioTracks().Count == 0)
        {
            ShowStatusHold("当前视频没有可识别的音轨。");
            return;
        }
        _subtitles.UseLive();
        FinishSubtitleChoice();
        StartLiveSubtitles(path);
    }

    private void StartLiveSubtitles(string path)
    {
        StopLiveSubtitles();
        var generation = ++_liveSubtitleGeneration;
        var task = new CancellationTokenSource();
        _liveSubtitles = task;
        _liveEntries.Clear();
        _liveBuffer.Clear();
        _liveReadyShown = false;
        lock (_liveTranslationLock)
            _liveTranslationTasks.Clear();
        _subtitles.UseLive();
        var settings = App.Host.Settings;
        var device = AiComputeDeviceExtensions.Parse(settings.AiComputeDevice);
        var translate = settings.AiTranslateLiveSubtitles;
        var drafts = settings.AiLiveDraftSubtitles;
        var translation = translate
            ? App.Host.AiSubtitles.CreateLiveTranslator(
                settings.AiTranslationModel, settings.AiSourceLanguage, settings.AiTargetLanguage, device)
            : null;
        _liveTranslation = translation;
        ShowStatusHold(translate ? "正在生成实时 AI 字幕与翻译（SenseVoice）" : "正在生成实时 AI 字幕（SenseVoice）");
        if (translation != null)
        {
            var translationStart = translation.StartAsync(null, task.Token);
            _ = Task.Run(async () =>
            {
                try
                {
                    await translationStart;
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (generation == _liveSubtitleGeneration)
                            ShowStatusHold("实时翻译不可用：" + ex.Message + "（继续显示原文字幕）");
                    });
                }
            });
        }
        var audioTrack = Playback.AudioTrackIndex;
        var startSeconds = Math.Max(0, Playback.Position.TotalSeconds - 0.25);
        _ = Task.Run(async () =>
        {
            try
            {
                var cues = await App.Host.AiSubtitles.RecognizeWithSherpaAsync(
                    path, audioTrack, device, startSeconds,
                    null,
                    update => DispatcherQueue.TryEnqueue(() => ApplyLiveSegment(generation, path, update, translation, task.Token)),
                    task.Token,
                    realtime: true,
                    drafts: drafts);
                if (generation == _liveSubtitleGeneration && cues.Count > 0)
                    await FinishLiveSubtitlesAsync(generation, path, translation, task.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (generation == _liveSubtitleGeneration)
                        ShowStatusHold("实时字幕失败：" + ex.Message);
                });
            }
        });
    }

    private void ApplyLiveSegment(
        int generation,
        string path,
        SubtitleSegmentUpdate update,
        LiveTranslationService? translation,
        CancellationToken cancellationToken)
    {
        if (generation != _liveSubtitleGeneration || _liveSubtitles == null
            || !string.Equals(Playback.Playlist.Current?.Path, path, StringComparison.OrdinalIgnoreCase))
            return;
        if (!_liveBuffer.Apply(update))
            return;
        foreach (var old in _liveEntries.Where(entry => entry.SegmentId == update.SegmentId).ToArray())
            old.Superseded = true;
        _liveEntries.RemoveAll(entry => entry.SegmentId == update.SegmentId);
        var entries = update.Cues.Select(cue => new LiveCueEntry(update.SegmentId, update.Final, cue)).ToList();
        _liveEntries.AddRange(entries);
        if (!_liveReadyShown && _liveEntries.Count > 0)
        {
            _liveReadyShown = true;
            ShowStatusHold($"实时字幕已就绪 · 当前 {Playback.Position:mm\\:ss}");
        }
        RefreshLiveCues();
        if (!update.Final || translation == null)
            return;
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.OriginalText))
                continue;
            var translationTask = TranslateLiveCueAsync(generation, entry, translation, cancellationToken);
            lock (_liveTranslationLock)
                _liveTranslationTasks.Add(translationTask);
        }
    }

    private async Task TranslateLiveCueAsync(
        int generation,
        LiveCueEntry entry,
        LiveTranslationService translation,
        CancellationToken cancellationToken)
    {
        try
        {
            var translated = await translation.TranslateAsync(entry.OriginalText, cancellationToken);
            if (string.IsNullOrWhiteSpace(translated))
                return;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (generation != _liveSubtitleGeneration || entry.Superseded)
                    return;
                entry.Cue = entry.Cue with
                {
                    Text = entry.OriginalText,
                    Translation = translated.Trim(),
                    Bitmaps = [],
                    AssEvent = null
                };
                RefreshLiveCues();
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
        }
    }

    private void RefreshLiveCues()
    {
        _liveEntries.Sort((left, right) => left.Cue.Start.CompareTo(right.Cue.Start));
        _subtitles.SetCues(_liveEntries.Select(entry => entry.Cue).ToArray(), null);
    }

    private async Task FinishLiveSubtitlesAsync(
        int generation,
        string path,
        LiveTranslationService? translation,
        CancellationToken cancellationToken)
    {
        if (translation != null)
        {
            try
            {
                await translation.StartAsync(null, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
            }
            await DrainDispatcherAsync();
            Task[] tasks;
            lock (_liveTranslationLock)
                tasks = _liveTranslationTasks.ToArray();
            if (tasks.Length > 0)
            {
                try
                {
                    await Task.WhenAll(tasks).WaitAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                }
            }
            await DrainDispatcherAsync();
        }
        if (generation != _liveSubtitleGeneration)
            return;
        var snapshot = await SnapshotLiveCuesAsync();
        if (snapshot.Length == 0)
            return;
        var description = "SenseVoice 实时字幕" + (translation != null
            ? " · " + AiSubtitleCatalog.LanguageName(App.Host.Settings.AiTargetLanguage)
            : "");
        var bilingual = App.Host.Settings.AiBilingualSubtitles;
        await Task.Run(() => App.Host.AiSubtitles.SaveBesideMedia(path, snapshot, description, bilingual), cancellationToken);
        DispatcherQueue.TryEnqueue(() =>
        {
            if (generation != _liveSubtitleGeneration)
                return;
            RefreshGeneratedSubtitles(path);
            ShowStatusHold("实时字幕已生成并保存，可在字幕列表中选择外挂字幕。");
        });
    }

    private Task DrainDispatcherAsync()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(() => completion.TrySetResult()))
            completion.TrySetResult();
        return completion.Task;
    }

    private Task<SubtitleCue[]> SnapshotLiveCuesAsync()
    {
        var completion = new TaskCompletionSource<SubtitleCue[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(() =>
                completion.TrySetResult(_liveEntries
                    .Where(entry => entry.Final && !entry.Superseded)
                    .Select(entry => entry.Cue)
                    .OrderBy(cue => cue.Start)
                    .ToArray())))
            completion.TrySetResult([]);
        return completion.Task;
    }

    private sealed class LiveCueEntry(int segmentId, bool final, SubtitleCue cue)
    {
        public int SegmentId { get; } = segmentId;
        public bool Final { get; } = final;
        public bool Superseded { get; set; }
        public string OriginalText { get; } = cue.Text;
        public SubtitleCue Cue { get; set; } = cue;
    }

    private async void RestartLiveAfterSeek()
    {
        if (_liveSubtitles == null)
            return;
        var generation = ++_liveSubtitleGeneration;
        var path = Playback.Playlist.Current?.Path;
        try
        {
            await Task.Delay(350);
            if (generation == _liveSubtitleGeneration && path != null && _liveSubtitles != null)
                StartLiveSubtitles(path);
        }
        catch
        {
        }
    }

    private void StopLiveSubtitles()
    {
        ++_liveSubtitleGeneration;
        var task = _liveSubtitles;
        _liveSubtitles = null;
        if (task != null)
        {
            task.Cancel();
            task.Dispose();
        }
        _liveBuffer.Clear();
        _liveEntries.Clear();
        _liveReadyShown = false;
        var translation = _liveTranslation;
        _liveTranslation = null;
        if (translation != null)
            _ = translation.DisposeAsync().AsTask();
    }

    private MenuFlyoutItem CreateAiGenerationMenuItem()
    {
        if (_aiGeneration != null)
        {
            var active = new AiGenerationMenuFlyoutItem
            {
                Style = (Style)Resources["AiGenerationMenuItemStyle"],
                IsEnabled = false,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentFillColorDefaultBrush"]
            };
            _aiGenerationMenuItem = active;
            UpdateAiGenerationMenuItem();
            return active;
        }
        var item = new MenuFlyoutItem { Text = "后台生成字幕" };
        item.Click += async (_, _) => await StartAiGeneration();
        return item;
    }

    private async Task StartAiGeneration()
    {
        if (_aiGeneration != null)
            return;
        var path = Playback.Playlist.Current?.Path;
        if (string.IsNullOrWhiteSpace(path))
        {
            ShowStatusHold("请先打开一个媒体文件。");
            return;
        }
        if (!App.Host.Settings.AiSubtitlesEnabled)
        {
            ShowStatusHold("请先在设置的“AI 字幕”标签页中启用 AI 字幕。");
            return;
        }
        var tracks = Playback.GetAudioTracks();
        if (tracks.Count == 0)
        {
            ShowStatusHold("当前视频没有可识别的音轨。");
            return;
        }

        var settings = App.Host.Settings;
        var useSherpa = settings.AiRecognitionEngine != "whisper";
        var translate = settings.AiTranslateAfterGeneration;
        var device = AiComputeDeviceExtensions.Parse(settings.AiComputeDevice);
        var sourceLanguage = settings.AiSourceLanguage;
        var targetLanguage = settings.AiTargetLanguage;
        var recognitionModel = settings.AiRecognitionModel;
        var translationModel = settings.AiTranslationModel;
        var bilingual = settings.AiBilingualSubtitles;
        var task = new CancellationTokenSource();
        var version = ++_aiGenerationVersion;
        _aiGeneration = task;
        _aiGenerationPath = path;
        _aiGenerationStage = "准备识别";
        _aiGenerationProgress = 0;
        UpdateAiGenerationMenuItem();
        ShowStatusHold("后台字幕任务已开始，可以继续播放。");
        try
        {
            var recognitionProgress = AiGenerationProgress(version, path, translate ? 0.6 : 1, 0);
            IReadOnlyList<SubtitleCue> cues = useSherpa
                ? await App.Host.AiSubtitles.RecognizeWithSherpaAsync(path, Playback.AudioTrackIndex, device, 0,
                    recognitionProgress, null, task.Token)
                : await App.Host.AiSubtitles.RecognizeAsync(path, Playback.AudioTrackIndex, sourceLanguage,
                    recognitionModel, device, recognitionProgress, task.Token);
            if (cues.Count == 0)
                throw new InvalidOperationException("没有识别到可用对白。");
            if (translate)
            {
                cues = await App.Host.AiSubtitles.TranslateAsync(cues, sourceLanguage, targetLanguage,
                    translationModel, device, AiGenerationProgress(version, path, 0.4, 0.6), task.Token);
            }
            var description = (useSherpa ? "Sherpa 识别" : "Whisper 识别")
                + (translate ? " · " + AiSubtitleCatalog.LanguageName(targetLanguage) : " · 原文");
            var result = await Task.Run(() => App.Host.AiSubtitles.SaveBesideMedia(path, cues, description, bilingual), task.Token);
            RefreshGeneratedSubtitles(path);
            if (_aiGenerationMenuItem != null)
            {
                _aiGenerationMenuItem.StopProgress();
                _aiGenerationMenuItem.Text = "后台字幕已生成，可在上方选择";
            }
            ShowStatusHold("字幕已保存到视频同目录，可在字幕列表中选择：" + System.IO.Path.GetFileName(result.Path));
        }
        catch (OperationCanceledException)
        {
            ShowStatusHold("后台字幕任务已取消。");
        }
        catch (Exception ex)
        {
            if (_aiGenerationMenuItem != null)
            {
                _aiGenerationMenuItem.StopProgress();
                _aiGenerationMenuItem.Text = "后台生成失败，请重新打开菜单重试";
            }
            ShowStatusHold("后台字幕生成失败：" + ex.Message);
            CrashLog.Write("AI 后台字幕任务失败", ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            if (version == _aiGenerationVersion)
            {
                _aiGeneration = null;
                _aiGenerationPath = null;
                task.Dispose();
            }
        }
    }

    private Progress<AiSubtitleProgress> AiGenerationProgress(int version, string path, double scale, double offset) =>
        new(value =>
        {
            if (version != _aiGenerationVersion || !string.Equals(path, _aiGenerationPath, StringComparison.OrdinalIgnoreCase))
                return;
            _aiGenerationStage = value.Stage;
            _aiGenerationProgress = offset + value.Value * scale;
            UpdateAiGenerationMenuItem();
        });

    private void UpdateAiGenerationMenuItem()
    {
        if (_aiGenerationMenuItem == null)
            return;
        var prefix = string.Equals(_aiGenerationPath, _subtitles.VideoPath, StringComparison.OrdinalIgnoreCase)
            ? "后台生成字幕"
            : "正在为其他视频生成字幕";
        _aiGenerationMenuItem.Text = $"{prefix} · {_aiGenerationProgress:P0}";
        ToolTipService.SetToolTip(_aiGenerationMenuItem, _aiGenerationStage);
    }

    private void RefreshGeneratedSubtitles(string mediaPath)
    {
        if (!string.Equals(_subtitles.VideoPath, mediaPath, StringComparison.OrdinalIgnoreCase))
            return;
        _subtitles.ReloadSidecars();
        UpdateSubtitleChrome();
        UpdateSubtitleOverlay();
    }
}
