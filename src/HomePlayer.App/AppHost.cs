using System.Text.Json;
using HomePlayer.Core;
using HomePlayer.Data;
using HomePlayer.Library;
using HomePlayer.Playback;

namespace HomePlayer;

public sealed class AppHost : IAsyncDisposable
{
    private AppHost(AppSettings settings, LibraryRepository repository, LibraryCoordinator libraries, PlaybackController playback)
    {
        Settings = settings;
        Repository = repository;
        Libraries = libraries;
        Playback = playback;
    }

    public AppSettings Settings { get; }
    public LibraryRepository Repository { get; }
    public LibraryCoordinator Libraries { get; }
    public PlaybackController Playback { get; }
    public bool Exiting { get; set; }

    public static AppHost Create(Action<Action> post)
    {
        var settings = LoadSettings();
        var repository = new LibraryRepository(AppPaths.Database);
        var libraries = new LibraryCoordinator(repository, () => App.Host?.Settings.TmdbApiKey ?? settings.TmdbApiKey);
        var playback = new PlaybackController(post, () => CodecCatalog.Plan(settings));
        playback.SetVolume(settings.Volume);
        var host = new AppHost(settings, repository, libraries, playback);
        playback.PlaybackActiveChanged += libraries.SetPlaybackActive;
        libraries.Start();
        return host;
    }

    public void SaveSettings()
    {
        Settings.Volume = Playback.Volume;
        var json = JsonSerializer.Serialize(Settings, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(AppPaths.SettingsFile, json);
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
                return new AppSettings();
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile)) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return new AppSettings();
        }
    }
}
