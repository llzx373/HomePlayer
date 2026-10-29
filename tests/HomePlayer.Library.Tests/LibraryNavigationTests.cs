using HomePlayer.Core;
using HomePlayer.Data;

namespace HomePlayer.Library.Tests;

public class LibraryNavigationTests
{
    [Fact]
    public void Bookmarks_round_trip_in_position_order()
    {
        var directory = Path.Combine(Path.GetTempPath(), "HomePlayer.Bookmarks." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var repository = new LibraryRepository(Path.Combine(directory, "library.db"));
            repository.SaveBookmark(new MediaBookmark("video.mkv", 40, "后", 2));
            repository.SaveBookmark(new MediaBookmark("video.mkv", 10, "前", 1));

            var bookmarks = repository.ListBookmarks("video.mkv");
            Assert.Equal([10d, 40d], bookmarks.Select(item => item.PositionSeconds).ToArray());
            repository.DeleteBookmark("video.mkv", 10);
            Assert.Equal("后", Assert.Single(repository.ListBookmarks("video.mkv")).Title);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Playback_volume_and_rate_preferences_round_trip()
    {
        var directory = Path.Combine(Path.GetTempPath(), "HomePlayer.PlaybackPrefs." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var repository = new LibraryRepository(Path.Combine(directory, "library.db"));
            repository.SavePreference(MediaPreference.Empty("video.mkv") with { Volume = 0.45, PlaybackRate = 1.75 });

            var preference = repository.LoadPreference("video.mkv");
            Assert.Equal(0.45, preference?.Volume);
            Assert.Equal(1.75, preference?.PlaybackRate);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
