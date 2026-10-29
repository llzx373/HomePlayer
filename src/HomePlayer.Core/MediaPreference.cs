namespace HomePlayer.Core;

public enum SubtitlePreferenceMode
{
    Auto,
    Off,
    Sidecar,
    Embedded
}

public sealed record MediaPreference(
    string Path,
    int? AudioTrackIndex,
    string? AudioTrackTitle,
    string? AudioTrackLanguage,
    SubtitlePreferenceMode SubtitleMode,
    int? SubtitleTrackIndex,
    string? SubtitleTrackTitle,
    string? SubtitleTrackLanguage,
    string? SubtitlePath,
    int SubtitleDelayMs,
    double? Volume = null,
    double? PlaybackRate = null,
    int AudioDelayMs = 0)
{
    public static MediaPreference Empty(string path) => new(
        path, null, null, null, SubtitlePreferenceMode.Auto, null, null, null, null, 0);
}

public interface IMediaPreferenceStore
{
    MediaPreference? LoadPreference(string path);

    void SavePreference(MediaPreference preference);
}

public static class MediaPreferenceSelection
{
    public static int? AudioTrack(MediaPreference? preference, IReadOnlyList<MediaTrack> tracks, string? preferredLanguage = null)
    {
        if (tracks.Count == 0)
            return null;
        if (preference?.AudioTrackIndex is int savedIndex)
        {
            var exact = tracks.FirstOrDefault(track =>
                string.Equals(track.Title, preference.AudioTrackTitle, StringComparison.OrdinalIgnoreCase)
                && string.Equals(track.Language, preference.AudioTrackLanguage, StringComparison.OrdinalIgnoreCase));
            if (exact != null)
                return exact.Index;
            if (!string.IsNullOrWhiteSpace(preference.AudioTrackLanguage))
            {
                var language = tracks.FirstOrDefault(track =>
                    string.Equals(track.Language, preference.AudioTrackLanguage, StringComparison.OrdinalIgnoreCase));
                if (language != null)
                    return language.Index;
            }
            if (tracks.Any(track => track.Index == savedIndex))
                return savedIndex;
        }
        return tracks.FirstOrDefault(track =>
            !string.IsNullOrWhiteSpace(preferredLanguage)
            && string.Equals(track.Language, preferredLanguage, StringComparison.OrdinalIgnoreCase))?.Index;
    }
}
