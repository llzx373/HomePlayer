using HomePlayer.Core;

namespace HomePlayer.Playback;

public sealed partial class PlaybackController
{
    private bool _useDefaultAudio;
    private string? _audioFallbackNotice;

    public Func<string?> PreferredAudioLanguage { get; set; } = () => null;
    public Func<bool> AudioNormalization { get; set; } = () => false;
    public Func<bool> AudioNightMode { get; set; } = () => false;
    public int AudioDelayMs { get; private set; }

    public void SetAudioDelay(int milliseconds)
    {
        AudioDelayMs = Math.Clamp(milliseconds, -10_000, 10_000);
        (_engine as IPlaybackAudioEnhancements)?.SetAudioDelay(AudioDelayMs);
        if (_playingPath != null && _preferences != null)
        {
            var preference = LoadPreference(_playingPath);
            _preferences.SavePreference(preference with { AudioDelayMs = AudioDelayMs });
        }
        Notify();
    }

    public void ApplyAudioSettings(IPlaybackEngine engine, string path)
    {
        var preference = LoadPreference(path);
        AudioDelayMs = preference.AudioDelayMs;
        if (engine is IPlaybackAudioEnhancements enhancements)
        {
            enhancements.SetAudioDelay(AudioDelayMs);
            enhancements.SetNormalization(AudioNormalization());
            enhancements.SetNightMode(AudioNightMode());
        }
    }

    private async Task MonitorAudioDeviceAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                if (!_useDefaultAudio && _engine != null && !AudioOutputs.IsAvailable(_audioOutput()))
                    OnAudioOutputFailed(new InvalidOperationException("音频设备已断开。"));
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void OnAudioOutputFailed(Exception error)
    {
        var token = _token;
        _post(() =>
        {
            if (token != _token)
                return;
            RememberPosition();
            _useDefaultAudio = true;
            _audioFallbackNotice = "所选音频设备不可用，已回退系统默认设备。";
            _ = PlayCurrentAsync(_engine is FfmpegPlaybackEngine);
        });
    }
}
