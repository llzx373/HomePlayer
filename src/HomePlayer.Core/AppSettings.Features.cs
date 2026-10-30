namespace HomePlayer.Core;

public sealed partial class AppSettings
{
    public bool RestorePlaylist { get; set; } = true;
    public int ResumeRewindSeconds { get; set; } = 5;
    public bool RememberVolumePerFile { get; set; }
    public bool RememberPlaybackRatePerFile { get; set; }
    public int SeekStepSeconds { get; set; } = 5;
    public int LargeSeekStepSeconds { get; set; } = 30;
    public List<string> LastPlaylist { get; set; } = [];
    public int LastPlaylistIndex { get; set; } = -1;
    public string PreferredAudioLanguage { get; set; } = "";
    public bool AudioNormalization { get; set; }
    public bool AudioNightMode { get; set; }
    public bool HardwareAcceleration { get; set; } = true;
    public bool SnapshotIncludeSubtitles { get; set; }
    public string UpdateChannel { get; set; } = "stable";
}
