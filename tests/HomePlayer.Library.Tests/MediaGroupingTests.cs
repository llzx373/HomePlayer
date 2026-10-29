using HomePlayer.Core;
using HomePlayer.Library;

namespace HomePlayer.Library.Tests;

public class MediaGroupingTests
{
    [Fact]
    public void Natural_order_handles_specials_episode_numbers_and_ova()
    {
        var library = Library();
        var entries = new[]
        {
            Entry("Show.S01E10.mkv", library, season: 1, episode: 10),
            Entry("Show.S01E2.mkv", library, season: 1, episode: 2),
            Entry("Show.S00E01.mkv", library, season: 0, episode: 1),
            Entry("Show.OVA.mkv", library, mediaType: "tv")
        };

        var group = Assert.Single(MediaGrouping.Build(entries));

        Assert.Equal(new int?[] { 0, 1, null }, group.Seasons);
        Assert.Equal(new int?[] { 0, 1, 1, null }, group.OrderedItems.Select(item => item.Item.Season));
        Assert.Equal(new int?[] { 1, 2, 10, null }, group.OrderedItems.Select(item => item.Item.Episode));
    }

    [Fact]
    public void Multiple_versions_form_one_show_and_one_watched_episode()
    {
        var library = Library();
        var first = Entry("Show.S01E01.1080p.mkv", library, tmdbId: 42, season: 1, episode: 1);
        var second = Entry("Show.S01E01.2160p.mkv", library, tmdbId: 42, season: 1, episode: 1);
        var third = Entry("Show.S01E02.mkv", library, tmdbId: 42, season: 1, episode: 2);
        first = first with { State = new PlaybackState(first.Item.FullPath(library.RootPath), 0, 100, true, 1) };

        var group = Assert.Single(MediaGrouping.Build([first, second, third]));

        Assert.Equal(2, group.EpisodeCount);
        Assert.Equal(1, group.WatchedCount);
        Assert.Equal(2, group.NextUnwatched?.Item.Episode);
        Assert.Equal(3, group.InSeason(1).Count);
    }

    [Fact]
    public void Tmdb_identity_groups_episodes_across_directories_and_movie_collections()
    {
        var library = Library();
        var showA = Entry("one/renamed.mkv", library, tmdbId: 7, mediaType: "tv", title: "Show", season: 1, episode: 1);
        var showB = Entry("other/another.mkv", library, tmdbId: 7, mediaType: "tv", title: "Show", season: 2, episode: 1);
        var movieA = Entry("Movie 1.mkv", library, mediaType: "movie", title: "Movie 1", collection: "Movie Collection");
        var movieB = Entry("Movie 2.mkv", library, mediaType: "movie", title: "Movie 2", collection: "Movie Collection");
        var standalone = Entry("Standalone.mkv", library, mediaType: "movie", title: "Standalone");

        var groups = MediaGrouping.Build([showA, showB, movieA, movieB, standalone]);

        Assert.Equal(2, groups.Count);
        var show = Assert.Single(groups, group => group.Title == "Show");
        Assert.Equal(2, show.Seasons.Count);
        var movies = Assert.Single(groups, group => group.IsMovieCollection);
        Assert.Equal(2, movies.EpisodeCount);
    }

    private static MediaLibrary Library() => new() { Id = "lib", Name = "Library", RootPath = @"D:\Media" };

    private static MediaSeriesEntry Entry(
        string path,
        MediaLibrary library,
        int? tmdbId = null,
        string mediaType = "tv",
        string title = "Show",
        int? season = null,
        int? episode = null,
        string? collection = null)
    {
        var item = new MediaItem
        {
            Id = Guid.NewGuid().ToString("N"),
            LibraryId = library.Id,
            RelativePath = path,
            MediaType = mediaType,
            TmdbId = tmdbId,
            Title = title,
            Season = season,
            Episode = episode,
            CollectionName = collection,
            Status = ItemStatus.Ready
        };
        return new MediaSeriesEntry(item, library, null);
    }
}
