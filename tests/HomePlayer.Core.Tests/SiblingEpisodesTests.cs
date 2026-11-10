using HomePlayer.Core;

namespace HomePlayer.Core.Tests;

public class SiblingEpisodesTests
{
    private static readonly string ShowDirectory = Path.Combine(Path.GetTempPath(), "HomePlayerSiblingTests", "Show");

    private static string PathFor(string name) => Path.Combine(ShowDirectory, name);

    private static SiblingPlaylist Build(string anchor, params string[] files) =>
        SiblingEpisodes.Build(PathFor(anchor), files.Select(PathFor));

    [Fact]
    public void Groups_standard_episodes_and_selects_anchor()
    {
        var result = Build(
            "Show.S01E02.mkv",
            "Show.S01E01.mkv",
            "Show.S01E02.mkv",
            "Show.S01E10.mkv");

        Assert.Equal(
            new[]
            {
                PathFor("Show.S01E01.mkv"),
                PathFor("Show.S01E02.mkv"),
                PathFor("Show.S01E10.mkv")
            },
            result.Paths);
        Assert.Equal(1, result.Index);
    }

    [Fact]
    public void Groups_anime_prefix_number_suffix_names()
    {
        var result = Build(
            "[Group] Show - 02 [720p].mkv",
            "[Group] Show - 01 [1080p].mkv",
            "[Group] Show - 02 [720p].mkv",
            "[Group] Show - 03 [1080p].mkv");

        Assert.Equal(
            new[]
            {
                PathFor("[Group] Show - 01 [1080p].mkv"),
                PathFor("[Group] Show - 02 [720p].mkv"),
                PathFor("[Group] Show - 03 [1080p].mkv")
            },
            result.Paths);
        Assert.Equal(1, result.Index);
    }

    [Fact]
    public void Groups_names_with_different_trailing_tags()
    {
        var result = Build(
            "[Group] Show - 01 [1080p][AAAA].mkv",
            "[Group] Show - 01 [1080p][AAAA].mkv",
            "[Group] Show - 02 [1080p][BBBB].mkv");

        Assert.Equal(
            new[]
            {
                PathFor("[Group] Show - 01 [1080p][AAAA].mkv"),
                PathFor("[Group] Show - 02 [1080p][BBBB].mkv")
            },
            result.Paths);
        Assert.Equal(0, result.Index);
    }

    [Fact]
    public void Groups_names_where_episode_titles_differ()
    {
        var result = Build(
            "Show - 01 - Pilot.mkv",
            "Show - 01 - Pilot.mkv",
            "Show - 02 - Finale.mkv");

        Assert.Equal(
            new[]
            {
                PathFor("Show - 01 - Pilot.mkv"),
                PathFor("Show - 02 - Finale.mkv")
            },
            result.Paths);
        Assert.Equal(0, result.Index);
    }

    [Fact]
    public void Does_not_merge_different_shows_in_the_same_folder()
    {
        var result = Build(
            "Show A - 01.mkv",
            "Show A - 01.mkv",
            "Show A - 02.mkv",
            "Show B - 01.mkv",
            "Show B - 02.mkv");

        Assert.Equal(
            new[]
            {
                PathFor("Show A - 01.mkv"),
                PathFor("Show A - 02.mkv")
            },
            result.Paths);
        Assert.Equal(0, result.Index);
    }

    [Fact]
    public void Does_not_treat_years_as_episode_numbers()
    {
        var result = Build(
            "Movie (2019).mkv",
            "Movie (2019).mkv",
            "Movie (2020).mkv");

        Assert.Equal(new[] { PathFor("Movie (2019).mkv") }, result.Paths);
        Assert.Equal(0, result.Index);
    }

    [Fact]
    public void Groups_mixed_strong_and_loose_names()
    {
        var result = Build(
            "Show.S01E01.mkv",
            "Show.S01E01.mkv",
            "Show - 02.mkv",
            "Show 03.mkv");

        Assert.Equal(
            new[]
            {
                PathFor("Show.S01E01.mkv"),
                PathFor("Show - 02.mkv"),
                PathFor("Show 03.mkv")
            },
            result.Paths);
        Assert.Equal(0, result.Index);
    }

    [Fact]
    public void Groups_when_anchor_is_loose_and_others_are_strong()
    {
        var result = Build(
            "Show - 01.mkv",
            "Show - 01.mkv",
            "Show.S01E02.mkv",
            "Show.S01E03.mkv");

        Assert.Equal(
            new[]
            {
                PathFor("Show - 01.mkv"),
                PathFor("Show.S01E02.mkv"),
                PathFor("Show.S01E03.mkv")
            },
            result.Paths);
        Assert.Equal(0, result.Index);
    }

    [Fact]
    public void Does_not_treat_dotted_codec_or_audio_tags_as_episodes()
    {
        var codec = Build(
            "Movie.1080p.H.264.mkv",
            "Movie.1080p.H.264.mkv",
            "Movie.1080p.H.265.mkv");

        Assert.Equal(new[] { PathFor("Movie.1080p.H.264.mkv") }, codec.Paths);

        var audio = Build(
            "Movie.DDP5.1.mkv",
            "Movie.DDP5.1.mkv",
            "Movie.DDP7.1.mkv");

        Assert.Equal(new[] { PathFor("Movie.DDP5.1.mkv") }, audio.Paths);

        var bareAudio = Build(
            "Movie.5.1.mkv",
            "Movie.5.1.mkv",
            "Movie.7.1.mkv");

        Assert.Equal(new[] { PathFor("Movie.5.1.mkv") }, bareAudio.Paths);
    }

    [Fact]
    public void Deduplicates_multiple_versions_of_the_same_episode_and_keeps_anchor()
    {
        var result = Build(
            "Show - 01 1080p.mkv",
            "Show - 01 1080p.mkv",
            "Show - 01 720p.mkv",
            "Show - 02.mkv");

        Assert.Equal(
            new[]
            {
                PathFor("Show - 01 1080p.mkv"),
                PathFor("Show - 02.mkv")
            },
            result.Paths);
        Assert.Equal(0, result.Index);
    }

    [Fact]
    public void Prefers_the_specific_season_key_when_numbers_repeat()
    {
        var result = Build(
            "Show 01 02.mkv",
            "Show 01 01.mkv",
            "Show 01 02.mkv",
            "Show 02 01.mkv",
            "Show 02 02.mkv");

        Assert.Equal(
            new[]
            {
                PathFor("Show 01 01.mkv"),
                PathFor("Show 01 02.mkv")
            },
            result.Paths);
        Assert.Equal(1, result.Index);
    }

    [Fact]
    public void Ignores_files_in_subdirectories()
    {
        var result = Build(
            "Show - 01.mkv",
            "Show - 01.mkv",
            "Show - 02.mkv",
            Path.Combine("Season 2", "Show - 03.mkv"));

        Assert.Equal(
            new[]
            {
                PathFor("Show - 01.mkv"),
                PathFor("Show - 02.mkv")
            },
            result.Paths);
    }

    [Fact]
    public void Ignores_non_video_files()
    {
        var result = Build(
            "Show - 01.mkv",
            "Show - 01.mkv",
            "Show - 02.mkv",
            "Show - 01.srt",
            "cover.jpg");

        Assert.Equal(
            new[]
            {
                PathFor("Show - 01.mkv"),
                PathFor("Show - 02.mkv")
            },
            result.Paths);
    }

    [Fact]
    public void Returns_the_anchor_when_nothing_matches()
    {
        var result = Build("Show - 01.mkv", "Show - 01.mkv");

        Assert.Equal(new[] { PathFor("Show - 01.mkv") }, result.Paths);
        Assert.Equal(0, result.Index);
    }

    [Fact]
    public void Find_enumerates_the_anchor_folder()
    {
        var directory = Path.Combine(Path.GetTempPath(), "HomePlayerSiblingFind." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var first = Path.Combine(directory, "Show - 01.mkv");
            var second = Path.Combine(directory, "Show - 02.mkv");
            File.WriteAllText(first, "");
            File.WriteAllText(second, "");

            var result = SiblingEpisodes.Find(second);

            Assert.Equal(new[] { first, second }, result.Paths);
            Assert.Equal(1, result.Index);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
