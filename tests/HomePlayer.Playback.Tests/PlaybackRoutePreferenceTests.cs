namespace HomePlayer.Playback.Tests;

public sealed class PlaybackRoutePreferenceTests
{
    [Theory]
    [InlineData("movie.wmv", true)]
    [InlineData("MOVIE.WMV", true)]
    [InlineData("movie.asf", true)]
    [InlineData(@"D:\videos\movie.wmv", true)]
    [InlineData("movie.mkv", false)]
    [InlineData("movie.mp4", false)]
    [InlineData("wmv.txt", false)]
    [InlineData("", false)]
    public void Wmv_and_asf_prefer_native_ffmpeg(string path, bool expected)
    {
        Assert.Equal(expected, PlaybackProviderRegistry.PrefersNativeFfmpeg(path));
    }
}
