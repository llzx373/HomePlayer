using HomePlayer.Core;
using HomePlayer.Library;

namespace HomePlayer.Library.Tests;

public class NfoMetadataTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "HomePlayer.Nfo." + Guid.NewGuid().ToString("N"));

    public NfoMetadataTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void Episode_nfo_and_local_poster_override_online_metadata()
    {
        var video = Path.Combine(_directory, "episode.mkv");
        var poster = Path.Combine(_directory, "poster.jpg");
        File.WriteAllBytes(video, []);
        File.WriteAllBytes(poster, [1]);
        File.WriteAllText(Path.ChangeExtension(video, ".nfo"), """
            <episodedetails>
              <title>Local Episode</title>
              <season>2</season>
              <episode>7</episode>
              <plot>Local overview</plot>
              <year>2025</year>
            </episodedetails>
            """);
        var item = new MediaItem { RelativePath = "episode.mkv" };

        var applied = NfoMetadata.Apply(item, video);

        Assert.True(applied);
        Assert.Equal("Local Episode", item.Title);
        Assert.Equal(2, item.Season);
        Assert.Equal(7, item.Episode);
        Assert.Equal("tv", item.MediaType);
        Assert.Equal(poster, item.PosterPath);
        Assert.Equal(MatchStatus.Matched, item.MatchStatus);
    }

    [Fact]
    public void Movie_nfo_reads_collection()
    {
        var video = Path.Combine(_directory, "movie.mkv");
        File.WriteAllBytes(video, []);
        File.WriteAllText(Path.Combine(_directory, "movie.nfo"), """
            <movie><title>Movie</title><set><name>Series Collection</name></set></movie>
            """);
        var item = new MediaItem { RelativePath = "movie.mkv" };

        Assert.True(NfoMetadata.Apply(item, video));
        Assert.Equal("Series Collection", item.CollectionName);
        Assert.Equal("movie", item.MediaType);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, true);
        }
        catch (IOException)
        {
        }
    }
}
