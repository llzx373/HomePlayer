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
    public void Audio_selection_does_not_guess_when_language_is_ambiguous()
    {
        var preference = MediaPreference.Empty("movie.mkv") with
        {
            AudioTrackIndex = 1,
            AudioTrackTitle = "Renamed",
            AudioTrackLanguage = "en"
        };

        Assert.Null(MediaPreferenceSelection.AudioTrack(preference, Tracks));
    }

    [Fact]
    public void Stable_uid_selects_the_same_track_after_remux_reordering()
    {
        var tracks = new[]
        {
            new MediaTrack(7, MediaTrackKind.Audio, "Other", "ja", true) { StableId = "audio:uid:22" },
            new MediaTrack(9, MediaTrackKind.Audio, "Commentary", "en", false) { StableId = "audio:uid:11" }
        };
        var preference = MediaPreference.Empty("movie.mkv") with
        {
            AudioTrackIndex = 1,
            AudioTrackStableId = "audio:uid:11"
        };

        Assert.Equal(9, MediaPreferenceSelection.AudioTrack(preference, tracks));
    }

    [Fact]
    public void Ambiguous_stable_identity_does_not_select_an_arbitrary_track()
    {
        var tracks = new[]
        {
            new MediaTrack(1, MediaTrackKind.Audio, "Japanese", "ja", true) { StableId = "audio:meta:ja||0" },
            new MediaTrack(2, MediaTrackKind.Audio, "Japanese", "ja", false) { StableId = "audio:meta:ja||0" }
        };
        var preference = MediaPreference.Empty("movie.mkv") with
        {
            AudioTrackIndex = 2,
            AudioTrackStableId = "audio:meta:ja||0"
        };

        Assert.Null(MediaPreferenceSelection.AudioTrack(preference, tracks));
    }

    [Fact]
    public void Audio_selection_returns_null_when_saved_track_disappears()
    {
        var preference = MediaPreference.Empty("movie.mkv") with { AudioTrackIndex = 99 };

        Assert.Null(MediaPreferenceSelection.AudioTrack(preference, Tracks));
        Assert.Null(MediaPreferenceSelection.AudioTrack(null, Tracks));
    }
}
