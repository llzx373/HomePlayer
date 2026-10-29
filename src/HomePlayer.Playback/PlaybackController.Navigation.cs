using HomePlayer.Core;

namespace HomePlayer.Playback;

public sealed partial class PlaybackController
{
    public Func<bool> RememberVolumePerFile { get; set; } = () => false;
    public Func<bool> RememberPlaybackRatePerFile { get; set; } = () => false;
    public Func<int> ResumeRewindSeconds { get; set; } = () => 5;
    public string? CurrentPath => _playingPath ?? Playlist.Current?.Path;

    public void RestorePlaylist(IReadOnlyList<string> paths, int currentIndex)
    {
        Playlist.Replace(paths, currentIndex);
        Notify();
    }

    public IReadOnlyList<MediaChapter> GetChapters() =>
        (_engine as IPlaybackChapterSource)?.GetChapters() ?? [];

    private void ApplyPlaybackPreferences(IPlaybackEngine engine, string path)
    {
        if (_preferences == null)
            return;
        var preference = LoadPreference(path);
        if (RememberVolumePerFile() && preference.Volume is double volume)
            _volume = Math.Clamp(volume, 0, 1);
        if (RememberPlaybackRatePerFile() && preference.PlaybackRate is double rate)
            _rate = PlaybackRates.Clamp(rate);
        engine.SetVolume(_volume);
        engine.SetRate(_rate);
    }

    private void SavePlaybackPreference()
    {
        if (_playingPath == null || _preferences == null || (!RememberVolumePerFile() && !RememberPlaybackRatePerFile()))
            return;
        try
        {
            var preference = LoadPreference(_playingPath);
            _preferences.SavePreference(preference with
            {
                Volume = RememberVolumePerFile() ? _volume : preference.Volume,
                PlaybackRate = RememberPlaybackRatePerFile() ? _rate : preference.PlaybackRate
            });
        }
        catch (Exception)
        {
        }
    }
}
