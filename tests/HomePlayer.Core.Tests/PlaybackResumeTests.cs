using HomePlayer.Core;

namespace HomePlayer.Core.Tests;

public class PlaybackResumeTests
{
    [Fact]
    public void Position_skips_the_first_few_seconds()
    {
        Assert.Null(PlaybackResume.Position(TimeSpan.FromSeconds(4.9), TimeSpan.FromHours(2)));
        Assert.Equal(TimeSpan.FromSeconds(5), PlaybackResume.Position(TimeSpan.FromSeconds(5), TimeSpan.FromHours(2)));
    }

    [Fact]
    public void Position_skips_the_end_of_the_file()
    {
        var duration = TimeSpan.FromMinutes(10);
        Assert.Null(PlaybackResume.Position(duration - TimeSpan.FromSeconds(5), duration));
        Assert.Equal(TimeSpan.FromMinutes(9.5), PlaybackResume.Position(TimeSpan.FromMinutes(9.5), duration));
    }

    [Fact]
    public void Position_keeps_mid_file_progress_when_duration_is_unknown()
    {
        Assert.Equal(TimeSpan.FromMinutes(12), PlaybackResume.Position(TimeSpan.FromMinutes(12), TimeSpan.Zero));
    }
}
