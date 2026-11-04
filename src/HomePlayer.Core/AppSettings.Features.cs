namespace HomePlayer.Core;

public enum VideoRenderQuality
{
    Basic,
    Performance,
    Balanced,
    HighQuality
}

public enum VideoOutputRange
{
    Full,
    Limited
}

public sealed partial class AppSettings
{
    public int SettingsVersion { get; set; }
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
    public VideoRenderQuality RenderQuality { get; set; } = VideoRenderQuality.Basic;
    public VideoOutputRange RgbOutputRange { get; set; } = VideoOutputRange.Full;
    public int DisplayRefreshRate { get; set; }
    public bool SnapshotIncludeSubtitles { get; set; }
    public string UpdateChannel { get; set; } = "stable";
    public bool AiSubtitlesEnabled { get; set; }
    public string AiModelMirror { get; set; } = "https://hf-mirror.com";
    public string AiDownloadProxy { get; set; } = "http://127.0.0.1:7890";
    public string AiComputeDevice { get; set; } = "auto";
    public string AiRecognitionEngine { get; set; } = "sherpa";
    public string AiRecognitionModel { get; set; } = "whisper-small";
    public string AiTranslationModel { get; set; } = "hy-mt2-7b-q4";
    public string AiSourceLanguage { get; set; } = "";
    public string AiTargetLanguage { get; set; } = "zh";
    public bool AiTranslateAfterGeneration { get; set; }
    public bool AiTranslateLiveSubtitles { get; set; } = true;
    public bool AiLiveDraftSubtitles { get; set; } = true;
    public bool AiBilingualSubtitles { get; set; } = true;
}
