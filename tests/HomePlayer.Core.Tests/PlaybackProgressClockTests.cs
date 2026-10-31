using HomePlayer.Core;

namespace HomePlayer.Core.Tests;

public class PlaybackProgressClockTests
{
    [Theory]
    [InlineData(0.5, 10.125)]
    [InlineData(1, 10.25)]
    [InlineData(2, 10.5)]
    public void Prediction_follows_playback_rate(double rate, double expected)
    {
        var clock = new PlaybackProgressClock();
        clock.Anchor(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(100), true, 20);

        var position = clock.Predict(TimeSpan.FromSeconds(100), rate, 20.25);

        Assert.Equal(expected, position.TotalSeconds, 6);
    }

    [Fact]
    public void Small_correction_preserves_position_then_decays()
    {
        var clock = new PlaybackProgressClock();
        clock.Anchor(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(100), true, 0);
        var predicted = clock.Predict(TimeSpan.FromSeconds(100), 1, 0.1);

        clock.Correct(TimeSpan.FromSeconds(10.08), predicted, TimeSpan.FromSeconds(100), true, 0.1);
        var immediate = clock.Predict(TimeSpan.FromSeconds(100), 1, 0.1);
        var later = clock.Predict(TimeSpan.FromSeconds(100), 1, 0.2);

        Assert.Equal(predicted.TotalSeconds, immediate.TotalSeconds, 6);
        Assert.Equal(10.18 + 0.02 * Math.Exp(-0.4), later.TotalSeconds, 6);
    }

    [Fact]
    public void Large_correction_snaps_to_engine()
    {
        var clock = new PlaybackProgressClock();
        clock.Anchor(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(100), true, 0);
        var predicted = clock.Predict(TimeSpan.FromSeconds(100), 1, 0.1);

        clock.Correct(TimeSpan.FromSeconds(12), predicted, TimeSpan.FromSeconds(100), true, 0.1);
        var position = clock.Predict(TimeSpan.FromSeconds(100), 1, 0.1);

        Assert.Equal(12, position.TotalSeconds, 6);
    }

    [Fact]
    public void Pause_and_explicit_anchor_clear_pending_correction()
    {
        var clock = new PlaybackProgressClock();
        clock.Anchor(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(100), true, 0);
        var predicted = clock.Predict(TimeSpan.FromSeconds(100), 1, 0.1);
        clock.Correct(TimeSpan.FromSeconds(10.08), predicted, TimeSpan.FromSeconds(100), true, 0.1);

        clock.Anchor(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(100), false, 0.2);
        var position = clock.Predict(TimeSpan.FromSeconds(100), 2, 1);

        Assert.Equal(30, position.TotalSeconds, 6);
    }
}
