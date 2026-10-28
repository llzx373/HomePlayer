using HomePlayer.Core;

namespace HomePlayer.Core.Tests;

public class PlaybackProgressTests
{
    [Theory]
    [InlineData(10, 100, 10, false)]
    [InlineData(3, 100, 0, false)]
    [InlineData(97, 100, 0, true)]
    [InlineData(10, 0, 10, false)]
    [InlineData(0, 0, 0, false)]
    public void Evaluate_saves_position_and_flags_finished(double position, double duration, double expected, bool finished)
    {
        var result = PlaybackProgress.Evaluate(TimeSpan.FromSeconds(position), TimeSpan.FromSeconds(duration));

        Assert.Equal(expected, result.PositionSeconds, 3);
        Assert.Equal(finished, result.Finished);
    }
}
