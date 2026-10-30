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
    int AudioDelayMs = 0,
    string? AudioTrackStableId = null,
    string? SubtitleTrackStableId = null)
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
        var selected = Select(preference?.AudioTrackStableId, preference?.AudioTrackTitle, preference?.AudioTrackLanguage, preference?.AudioTrackIndex, tracks);
        if (selected != null)
            return selected;
        return tracks.FirstOrDefault(track =>
            !string.IsNullOrWhiteSpace(preferredLanguage)
            && string.Equals(track.Language, preferredLanguage, StringComparison.OrdinalIgnoreCase))?.Index;
    }

    public static int? SubtitleTrack(MediaPreference? preference, IReadOnlyList<MediaTrack> tracks) =>
        Select(preference?.SubtitleTrackStableId, preference?.SubtitleTrackTitle, preference?.SubtitleTrackLanguage, preference?.SubtitleTrackIndex, tracks);

    private static int? Select(string? stableId, string? title, string? language, int? savedIndex, IReadOnlyList<MediaTrack> tracks)
    {
        if (savedIndex == null && string.IsNullOrWhiteSpace(stableId))
            return null;
        if (!string.IsNullOrWhiteSpace(stableId))
        {
            var stable = tracks.Where(track => string.Equals(track.StableId, stableId, StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
            if (stable.Count == 1)
                return stable[0].Index;
            if (stable.Count > 1)
                return null;
        }
        var exact = tracks.Where(track =>
            string.Equals(track.Title, title, StringComparison.OrdinalIgnoreCase)
            && string.Equals(track.Language, language, StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
        if (exact.Count == 1)
            return exact[0].Index;
        if (exact.Count > 1)
            return null;
        if (!string.IsNullOrWhiteSpace(language))
        {
            var languageMatches = tracks.Where(track => string.Equals(track.Language, language, StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
            if (languageMatches.Count == 1)
                return languageMatches[0].Index;
            if (languageMatches.Count > 1)
                return null;
        }
        return savedIndex != null && tracks.Any(track => track.Index == savedIndex) ? savedIndex : null;
    }
}
