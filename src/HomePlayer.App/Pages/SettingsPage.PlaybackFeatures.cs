using Microsoft.UI.Xaml.Controls;

namespace HomePlayer;

public sealed partial class SettingsPage
{
    private bool _playbackFeatureEvents;

    private void InitializePlaybackFeatureSettings()
    {
        InitializeUpdateSettings();
        var settings = App.Host.Settings;
        RestorePlaylistSwitch.IsOn = settings.RestorePlaylist;
        ResumeRewindBox.Value = settings.ResumeRewindSeconds;
        SeekStepBox.Value = settings.SeekStepSeconds;
        LargeSeekStepBox.Value = settings.LargeSeekStepSeconds;
        RememberVolumeSwitch.IsOn = settings.RememberVolumePerFile;
        RememberRateSwitch.IsOn = settings.RememberPlaybackRatePerFile;
        PreferredAudioLanguageBox.Text = settings.PreferredAudioLanguage;
        AudioNormalizationSwitch.IsOn = settings.AudioNormalization;
        AudioNightModeSwitch.IsOn = settings.AudioNightMode;
        HardwareAccelerationSwitch.IsOn = settings.HardwareAcceleration;
        SnapshotSubtitlesSwitch.IsOn = settings.SnapshotIncludeSubtitles;
        if (_playbackFeatureEvents)
            return;
        _playbackFeatureEvents = true;
        RestorePlaylistSwitch.Toggled += (_, _) => SavePlaybackFeatures();
        ResumeRewindBox.ValueChanged += (_, _) => SavePlaybackFeatures();
        SeekStepBox.ValueChanged += (_, _) => SavePlaybackFeatures();
        LargeSeekStepBox.ValueChanged += (_, _) => SavePlaybackFeatures();
        RememberVolumeSwitch.Toggled += (_, _) => SavePlaybackFeatures();
        RememberRateSwitch.Toggled += (_, _) => SavePlaybackFeatures();
        PreferredAudioLanguageBox.TextChanged += (_, _) => SavePlaybackFeatures();
        AudioNormalizationSwitch.Toggled += (_, _) => SavePlaybackFeatures();
        AudioNightModeSwitch.Toggled += (_, _) => SavePlaybackFeatures();
        HardwareAccelerationSwitch.Toggled += (_, _) => SavePlaybackFeatures();
        SnapshotSubtitlesSwitch.Toggled += (_, _) => SavePlaybackFeatures();
    }

    private void SavePlaybackFeatures()
    {
        if (_loading)
            return;
        var settings = App.Host.Settings;
        settings.RestorePlaylist = RestorePlaylistSwitch.IsOn;
        settings.ResumeRewindSeconds = double.IsNaN(ResumeRewindBox.Value) ? settings.ResumeRewindSeconds : (int)ResumeRewindBox.Value;
        settings.SeekStepSeconds = double.IsNaN(SeekStepBox.Value) ? settings.SeekStepSeconds : (int)SeekStepBox.Value;
        settings.LargeSeekStepSeconds = double.IsNaN(LargeSeekStepBox.Value) ? settings.LargeSeekStepSeconds : (int)LargeSeekStepBox.Value;
        settings.RememberVolumePerFile = RememberVolumeSwitch.IsOn;
        settings.RememberPlaybackRatePerFile = RememberRateSwitch.IsOn;
        settings.PreferredAudioLanguage = PreferredAudioLanguageBox.Text.Trim();
        settings.AudioNormalization = AudioNormalizationSwitch.IsOn;
        settings.AudioNightMode = AudioNightModeSwitch.IsOn;
        settings.HardwareAcceleration = HardwareAccelerationSwitch.IsOn;
        settings.SnapshotIncludeSubtitles = SnapshotSubtitlesSwitch.IsOn;
        App.Host.SaveSettings();
    }
}
