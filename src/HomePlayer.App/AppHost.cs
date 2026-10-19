using System.Text.Json;
using HomePlayer.Core;
using HomePlayer.Data;
using HomePlayer.Library;
using HomePlayer.Playback;

namespace HomePlayer;

public sealed class AppHost : IAsyncDisposable
{
    private readonly CodecPlanCache _codecPlans;

    private AppHost(
        AppSettings settings,
        LibraryRepository repository,
        LibraryCoordinator libraries,
        PlaybackController playback,
        CodecPlanCache codecPlans)
    {
        Settings = settings;
        Repository = repository;
        Libraries = libraries;
        Playback = playback;
        _codecPlans = codecPlans;
    }

    public AppSettings Settings { get; }
    public LibraryRepository Repository { get; }
    public LibraryCoordinator Libraries { get; }
    public PlaybackController Playback { get; }
    public bool Exiting { get; set; }

    public void InvalidateCodecPlan() => _codecPlans.Invalidate();

    public static AppHost Create(Action<Action> post)
    {
        var settings = LoadSettings();
        var repository = new LibraryRepository(AppPaths.Database);
        var libraries = new LibraryCoordinator(repository, () => App.Host?.Settings.TmdbApiKey ?? settings.TmdbApiKey);
        var codecPlans = new CodecPlanCache(() => settings);
        var playback = new PlaybackController(post, codecPlans.Get, () => settings.AudioOutputId);
        playback.SetVolume(settings.Volume);
        var host = new AppHost(settings, repository, libraries, playback, codecPlans);
        playback.PlaybackActiveChanged += libraries.SetPlaybackActive;
        libraries.Start();
        return host;
    }

    public void SaveSettings()
    {
        try
        {
            Settings.Volume = Playback.Volume;
            var json = JsonSerializer.Serialize(Settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(AppPaths.SettingsFile, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        SaveSettings();
        Playback.Dispose();
        await Libraries.DisposeAsync();
        Repository.Dispose();
    }

    private static AppSettings LoadSettings()
    {
        try
        {
            if (!File.Exists(AppPaths.SettingsFile))
                return WithHotkeys(new AppSettings());
            return WithHotkeys(JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile)) ?? new AppSettings());
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return WithHotkeys(new AppSettings());
        }
    }

    private static AppSettings WithHotkeys(AppSettings settings)
    {
        settings.Hotkeys = HotkeyCatalog.Merge(settings.Hotkeys);
        return settings;
    }

    private sealed class CodecPlanCache(Func<AppSettings> settings)
    {
        private CodecPlan? _plan;

        public CodecPlan Get() => _plan ??= CodecCatalog.Plan(settings());

        public void Invalidate() => _plan = null;
    }
}
