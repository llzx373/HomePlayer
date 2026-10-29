namespace HomePlayer.Core.Tests;

public class MediaPreferenceTests
{
    private static readonly MediaTrack[] Tracks =
    [
        new(1, MediaTrackKind.Audio, "Japanese", "ja", true),
        new(2, MediaTrackKind.Audio, "English", "en", false),
        new(3, MediaTrackKind.Audio, "Commentary", "en", false)
    ];

    [Fact]
    public void Audio_selection_prefers_title_and_language_over_track_number()
    {
        var preference = MediaPreference.Empty("movie.mkv") with
        {
            AudioTrackIndex = 1,
            AudioTrackTitle = "Commentary",
            AudioTrackLanguage = "en"
        };

        Assert.Equal(3, MediaPreferenceSelection.AudioTrack(preference, Tracks));
    }

    [Fact]
    public void Audio_selection_uses_language_when_title_changes()
    {
        var preference = MediaPreference.Empty("movie.mkv") with
        {
            AudioTrackIndex = 1,
            AudioTrackTitle = "Renamed",
            AudioTrackLanguage = "en"
        };

        Assert.Equal(2, MediaPreferenceSelection.AudioTrack(preference, Tracks));
    }

    [Fact]
    public void Audio_selection_returns_null_when_saved_track_disappears()
    {
        var preference = MediaPreference.Empty("movie.mkv") with { AudioTrackIndex = 99 };

        Assert.Null(MediaPreferenceSelection.AudioTrack(preference, Tracks));
        Assert.Null(MediaPreferenceSelection.AudioTrack(null, Tracks));
    }
}
