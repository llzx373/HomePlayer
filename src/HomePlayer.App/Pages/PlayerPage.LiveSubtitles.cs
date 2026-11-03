using HomePlayer.AI;
using HomePlayer.Core;

namespace HomePlayer;

public sealed partial class PlayerPage
{
    private CancellationTokenSource? _liveSubtitles;
    private int _liveSubtitleGeneration;
    private readonly List<SubtitleCue> _liveCueList = [];

    private void MaybeStartLiveSubtitles()
    {
        var path = Playback.Playlist.Current?.Path;
        if (string.IsNullOrWhiteSpace(path) || _liveSubtitles != null)
            return;
        if (!App.Host.Settings.AiSubtitlesEnabled || !App.Host.Settings.AiLiveSubtitles
            || App.Host.Settings.AiRecognitionEngine != "sherpa"
            || !App.Host.AiSubtitles.SenseVoiceInstalled
            || _subtitles.Mode != SubtitleSession.Choice.Auto
            || _subtitles.Sidecars.Count > 0 || _subtitles.Embedded.Any(track => track.IsText))
            return;
        var tracks = Playback.GetAudioTracks();
        if (tracks.Count == 0)
            return;
        StartLiveSubtitles(path);
    }

    private void StartLiveSubtitles(string path)
    {
        StopLiveSubtitles();
        var generation = ++_liveSubtitleGeneration;
        var task = new CancellationTokenSource();
        _liveSubtitles = task;
        _liveCueList.Clear();
        _subtitles.UseLive();
        ShowStatusHold("正在生成实时 AI 字幕（Sherpa）");
        var audioTrack = Playback.AudioTrackIndex;
        var startSeconds = Math.Max(0, Playback.Position.TotalSeconds - 0.25);
        _ = Task.Run(async () =>
        {
            try
            {
                var cues = await App.Host.AiSubtitles.RecognizeWithSherpaAsync(
                    path, audioTrack, AiComputeDeviceExtensions.Parse(App.Host.Settings.AiComputeDevice), startSeconds,
                    null, cue => DispatcherQueue.TryEnqueue(() => AddLiveCue(generation, path, cue)), task.Token);
                if (generation == _liveSubtitleGeneration && cues.Count > 0)
                    App.Host.AiSubtitles.Save(path, cues, "Sherpa 实时字幕");
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

    private void AddLiveCue(int generation, string path, SubtitleCue cue)
    {
        if (generation != _liveSubtitleGeneration || _liveSubtitles == null
            || !string.Equals(Playback.Playlist.Current?.Path, path, StringComparison.OrdinalIgnoreCase))
            return;
        _liveCueList.Add(cue);
        if (_liveCueList.Count == 1)
            ShowStatusHold($"实时字幕已就绪 · 当前 {Playback.Position:mm\\:ss}");
        _liveCueList.Sort((left, right) => left.Start.CompareTo(right.Start));
        _subtitles.SetCues(_liveCueList.ToArray(), null);
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
    }
}
