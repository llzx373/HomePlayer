using HomePlayer.Core;

namespace HomePlayer.Core.Tests;

public class FilenameParserTests
{
    [Theory]
    [InlineData("Inception (2010).mkv", "Inception", 2010)]
    [InlineData("The Matrix (1999) 1080p BluRay x264.mkv", "The Matrix", 1999)]
    [InlineData("Movie.2020.1080p.mkv", "Movie", 2020)]
    [InlineData("1917 (2019) 2160p.mkv", "1917", 2019)]
    [InlineData("Blade.Runner.2049.2017.mkv", "Blade Runner 2049", 2017)]
    public void Parses_movie_titles_without_dangling_brackets(string file, string title, int year)
    {
        var parsed = FilenameParser.Parse(file);

        Assert.Equal(title, parsed.Title);
        Assert.Equal(year, parsed.Year);
        Assert.False(parsed.IsTv);
        Assert.Null(parsed.Season);
        Assert.Null(parsed.Episode);
    }

    [Theory]
    [InlineData("Show (2019) S01E02 1080p.mkv", "Show", 2019, 1, 2)]
    [InlineData("Show.S01E02.1080p.mkv", "Show", null, 1, 2)]
    [InlineData("Show 2x07 720p.mkv", "Show", null, 2, 7)]
    public void Parses_tv_titles(string file, string title, int? year, int season, int episode)
    {
        var parsed = FilenameParser.Parse(file);

        Assert.Equal(title, parsed.Title);
        Assert.Equal(year, parsed.Year);
        Assert.True(parsed.IsTv);
        Assert.Equal(season, parsed.Season);
        Assert.Equal(episode, parsed.Episode);
    }

    [Theory]
    [InlineData(@"D:\Shows\Season 2\Show.E07.mkv", 2, 7)]
    [InlineData(@"D:\Shows\第 3 季\Show 第 08 集.mkv", 3, 8)]
    [InlineData(@"D:\Shows\Specials\Show.E01.mkv", 0, 1)]
    public void Parses_episode_only_names_and_season_directories(string file, int season, int episode)
    {
        var parsed = FilenameParser.Parse(file);

        Assert.True(parsed.IsTv);
        Assert.Equal(season, parsed.Season);
        Assert.Equal(episode, parsed.Episode);
    }

    [Fact]
    public void Keeps_ova_in_series_navigation()
    {
        var parsed = FilenameParser.Parse("Show.OVA.1080p.mkv");

        Assert.True(parsed.IsTv);
        Assert.Equal("Show", parsed.Title);
    }

    [Fact]
    public void Keeps_title_when_no_year_exists()
    {
        var parsed = FilenameParser.Parse("No Year Here.mkv");

        Assert.Equal("No Year Here", parsed.Title);
        Assert.Null(parsed.Year);
        Assert.False(parsed.IsTv);
    }
}
