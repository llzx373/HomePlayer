using HomePlayer.Ffmpeg;

namespace HomePlayer.Playback.Tests;

public sealed class FfmpegInputProfileTests
{
    [Theory]
    [InlineData(@"\\server\share\movie.mkv")]
    [InlineData("//server/share/movie.mkv")]
    [InlineData(@"\\?\UNC\server\share\movie.mkv")]
    [InlineData("file://server/share/movie.mkv")]
    public void Unc_paths_use_bounded_two_stage_reading(string path)
    {
        var profile = FfmpegInputProfile.ForPath(path);

        Assert.True(profile.IsUnc);
        Assert.InRange(profile.ProbeSize, 1, 4 * 1024 * 1024);
        Assert.InRange(profile.AnalyzeDurationMicroseconds, 1, 2_000_000);
        Assert.InRange(profile.ReadWriteTimeoutMicroseconds, 1, 5_000_000);
        Assert.InRange(profile.ReadBufferSize, 256 * 1024, 1024 * 1024);
    }

    [Theory]
    [InlineData(@"C:\Media\movie.mkv")]
    [InlineData(@"\\?\C:\Media\movie.mkv")]
    [InlineData(@"\\.\PhysicalDrive0")]
    [InlineData("")]
    public void Local_paths_keep_ffmpeg_defaults(string path)
    {
        var profile = FfmpegInputProfile.ForPath(path);

        Assert.False(profile.IsUnc);
        Assert.Equal(0, profile.ProbeSize);
        Assert.Equal(0, profile.AnalyzeDurationMicroseconds);
        Assert.Equal(0, profile.ReadBufferSize);
        Assert.Equal(0, profile.ReadWriteTimeoutMicroseconds);
    }
}
