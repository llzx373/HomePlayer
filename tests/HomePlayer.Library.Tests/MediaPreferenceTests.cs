using HomePlayer.Core;
using HomePlayer.Data;

namespace HomePlayer.Library.Tests;

public class MediaPreferenceTests
{
    [Fact]
    public void Subtitle_display_mode_roundtrips_and_can_stay_unset()
    {
        var directory = Path.Combine(Path.GetTempPath(), "HomePlayer.Pref." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var repository = new LibraryRepository(Path.Combine(directory, "library.db"));
            var path = Path.Combine(directory, "movie.mkv");

            repository.SavePreference(new MediaPreference(
                path, null, null, null, SubtitlePreferenceMode.Sidecar, null, null, null,
                Path.Combine(directory, "movie.srt"), 0,
                SubtitleDisplayMode: SubtitleDisplayMode.Translation));

            Assert.Equal(SubtitleDisplayMode.Translation, repository.LoadPreference(path)?.SubtitleDisplayMode);

            repository.SavePreference(new MediaPreference(
                path, null, null, null, SubtitlePreferenceMode.Sidecar, null, null, null,
                Path.Combine(directory, "movie.srt"), 0));

            Assert.Null(repository.LoadPreference(path)?.SubtitleDisplayMode);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
