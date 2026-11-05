using HomePlayer.AI;
using HomePlayer.Core;
using HomePlayer.Data;
using HomePlayer.Library;
using HomePlayer.Playback;

namespace HomePlayer;

public sealed class AppHost : IAsyncDisposable
{
    private readonly CodecPlanCache _codecPlans;
    private readonly SettingsStore _settingsStore;

    private AppHost(
        AppSettings settings,
        LibraryRepository repository,
        LibraryCoordinator libraries,
        PlaybackController playback,
        CodecPlanCache codecPlans,
        SettingsStore settingsStore,
        string? settingsWarning)
    {
        Settings = settings;
        Repository = repository;
        Libraries = libraries;
        Playback = playback;
        AiSubtitles = new AiSubtitleService(AppPaths.ResolveAiModels(settings.AiModelDirectory), () => settings.AiModelMirror, () => settings.AiDownloadProxy);
        Voices = new VoiceLibrary(AppPaths.Voices);
        _codecPlans = codecPlans;
        _settingsStore = settingsStore;
        SettingsWarning = settingsWarning;
    }

    public AppSettings Settings { get; }
    public event Action? SettingsChanged;
    public LibraryRepository Repository { get; }
    public LibraryCoordinator Libraries { get; }
    public PlaybackController Playback { get; }
    public AiSubtitleService AiSubtitles { get; }
    public VoiceLibrary Voices { get; }
    public string? SettingsWarning { get; private set; }
    public bool Exiting { get; set; }

    public void InvalidateCodecPlan() => _codecPlans.Invalidate();

    private static string? RewriteCachePaths(AppSettings settings, SettingsStore store, bool preserveBackup)
    {
        var previous = settings.PreviousDataDirectory;
        if (string.IsNullOrWhiteSpace(previous)
            && !string.Equals(
                Path.GetFullPath(AppPaths.DefaultRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(AppPaths.Root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
            previous = AppPaths.DefaultRoot;
        if (string.IsNullOrWhiteSpace(previous))
            return null;
        try
        {
            CachePathMigration.Rewrite(AppPaths.Database, previous, AppPaths.Root);
            if (!string.IsNullOrWhiteSpace(settings.PreviousDataDirectory))
            {
                settings.PreviousDataDirectory = "";
                store.Save(settings, preserveBackup);
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            return "缓存路径迁移未完成：" + ex.Message;
        }
    }

    public void SetAiModelDirectory(string? directory)
    {
        Settings.AiModelDirectory = string.IsNullOrWhiteSpace(directory) ? "" : Path.GetFullPath(directory);
        AiSubtitles.ChangeModelDirectory(AppPaths.ResolveAiModels(Settings.AiModelDirectory));
        SaveSettings();
    }

    public static AppHost Create(Action<Action> post)
    {
        string? backupWarning = null;
        try
        {
            DataBackup.ApplyPendingRestore(AppPaths.Root, AppPaths.SettingsFile);
            DataBackup.CreateAutomaticBackup(AppPaths.Root, AppPaths.SettingsFile);
        }
        catch (Exception ex)
        {
            backupWarning = "数据备份或恢复未完成：" + ex.Message;
            CrashLog.Write("数据备份失败", ex.ToString());
        }

        var settingsStore = new SettingsStore(AppPaths.SettingsFile);
        var loaded = settingsStore.Load();
        var settings = loaded.Settings;
        if (settings.SettingsVersion < 2)
        {
            if (settings.AiTranslationModel == "hy-mt2-7b-q4"
                && AiSubtitleService.LightweightTranslationRecommended(settings.AiComputeDevice))
                settings.AiTranslationModel = "hy-mt2-1.8b-q4";
            settings.SettingsVersion = 2;
            settingsStore.Save(settings, loaded.Warning != null);
        }
        var cacheWarning = RewriteCachePaths(settings, settingsStore, loaded.Warning != null);
        var repository = new LibraryRepository(AppPaths.Database);
        var libraries = new LibraryCoordinator(repository, () => App.Host?.Settings.TmdbApiKey ?? settings.TmdbApiKey);
        var codecPlans = new CodecPlanCache(() => settings);
        var playback = new PlaybackController(
            post, codecPlans.Get, () => settings.AudioOutputId, repository,
            historyEnabled: () => settings.RecordPlaybackHistory,
            preferences: repository);
        playback.AutoAdvance = settings.AutoPlayNextEpisode;
        playback.RememberVolumePerFile = () => settings.RememberVolumePerFile;
        playback.RememberPlaybackRatePerFile = () => settings.RememberPlaybackRatePerFile;
        playback.ResumeRewindSeconds = () => settings.ResumeRewindSeconds;
        playback.PreferredAudioLanguage = () => settings.PreferredAudioLanguage;
        playback.AudioNormalization = () => settings.AudioNormalization;
        playback.AudioNightMode = () => settings.AudioNightMode;
        playback.SetVolume(settings.Volume);
        if (settings.RestorePlaylist && settings.LastPlaylist.Count > 0)
            playback.RestorePlaylist(settings.LastPlaylist, settings.LastPlaylistIndex);
        var warning = string.Join(Environment.NewLine, new[] { loaded.Warning, backupWarning, cacheWarning, AppPaths.MigrationWarning }.Where(item => item != null));
        var host = new AppHost(settings, repository, libraries, playback, codecPlans, settingsStore,
            warning.Length > 0 ? warning : null);
        playback.PlaybackActiveChanged += libraries.SetPlaybackActive;
        libraries.ItemReset += playback.ForgetProgress;
        return host;
    }

    public void StartLibraries()
    {
        _ = Task.Run(() =>
        {
            try
            {
                Libraries.Start();
            }
            catch (Exception)
            {
            }
        });
    }

    public void SaveSettings()
    {
        try
        {
            Settings.Volume = Playback.Volume;
            Settings.LastPlaylist = Playback.Playlist.Items.Select(item => item.Path).ToList();
            Settings.LastPlaylistIndex = Playback.Playlist.CurrentIndex;
            _settingsStore.Save(Settings, SettingsWarning != null);
            SettingsWarning = null;
            SettingsChanged?.Invoke();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        SaveSettings();
        await Playback.ShutdownAsync();
        await Libraries.DisposeAsync();
        Repository.Dispose();
    }

    private sealed class CodecPlanCache(Func<AppSettings> settings)
    {
        private CodecPlan? _plan;

        public CodecPlan Get() => _plan ??= CodecCatalog.Plan(settings());

        public void Invalidate() => _plan = null;
    }
}
