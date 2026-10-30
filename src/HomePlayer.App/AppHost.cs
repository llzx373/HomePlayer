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
        _codecPlans = codecPlans;
        _settingsStore = settingsStore;
        SettingsWarning = settingsWarning;
    }

    public AppSettings Settings { get; }
    public event Action? SettingsChanged;
    public LibraryRepository Repository { get; }
    public LibraryCoordinator Libraries { get; }
    public PlaybackController Playback { get; }
    public string? SettingsWarning { get; private set; }
    public bool Exiting { get; set; }

    public void InvalidateCodecPlan() => _codecPlans.Invalidate();

    public static AppHost Create(Action<Action> post)
    {
        string? backupWarning = null;
        try
        {
            DataBackup.ApplyPendingRestore(AppPaths.Root);
            DataBackup.CreateAutomaticBackup(AppPaths.Root);
        }
        catch (Exception ex)
        {
            backupWarning = "数据备份或恢复未完成：" + ex.Message;
            CrashLog.Write("数据备份失败", ex.ToString());
        }

        var settingsStore = new SettingsStore(AppPaths.SettingsFile);
        var loaded = settingsStore.Load();
        var settings = loaded.Settings;
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
        var warning = string.Join(Environment.NewLine, new[] { loaded.Warning, backupWarning }.Where(item => item != null));
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
