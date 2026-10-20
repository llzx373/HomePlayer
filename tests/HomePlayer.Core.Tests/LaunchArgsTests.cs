using HomePlayer.Core;

namespace HomePlayer.Core.Tests;

public class LaunchArgsTests
{
    [Fact]
    public void Splits_quoted_paths()
    {
        var parts = LaunchArgs.Split(@"D:\a.mkv ""E:\two words.mp4""");

        Assert.Equal(["D:\\a.mkv", "E:\\two words.mp4"], parts);
    }

    [Fact]
    public void Skips_exe_and_flags()
    {
        var paths = LaunchArgs.Paths(
            [@"C:\Program Files\HomePlayer\HomePlayer.exe", "--wait", @"D:\movie.mkv"],
            null);

        Assert.Equal(["D:\\movie.mkv"], paths);
    }

    [Fact]
    public void Reads_launch_arguments_without_duplicating()
    {
        var paths = LaunchArgs.Paths(
            [@"C:\HomePlayer.exe", @"D:\movie.mkv"],
            @"""D:\movie.mkv""");

        Assert.Equal(["D:\\movie.mkv"], paths);
    }
}
