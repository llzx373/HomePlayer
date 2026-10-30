using HomePlayer.Core;

namespace HomePlayer.Core.Tests;

public sealed class MediaTimestampRepairTests
{
    [Fact]
    public void Missing_timestamps_use_the_expected_position_and_duration()
    {
        var repair = new MediaTimestampRepair();

        var first = repair.Next(0, 100, 40, 40, 1000);
        var second = repair.Next(0, null, 0, 40, 1000);
        var third = repair.Next(0, null, 0, 0, 1000);

        Assert.Equal(new RepairedMediaTimestamp(100, 40), first);
        Assert.Equal(new RepairedMediaTimestamp(140, 40), second);
        Assert.Equal(new RepairedMediaTimestamp(180, 40), third);
    }

    [Fact]
    public void Variable_frame_durations_preserve_the_original_timeline()
    {
        var repair = new MediaTimestampRepair();

        Assert.Equal(0, repair.Next(0, 0, 40, 40, 1000).PresentationTimestamp);
        Assert.Equal(40, repair.Next(0, 40, 80, 40, 1000).PresentationTimestamp);
        var third = repair.Next(0, 120, 20, 40, 1000);

        Assert.Equal(new RepairedMediaTimestamp(120, 20), third);
    }

    [Fact]
    public void Backward_wrap_and_forward_discontinuity_rejoin_the_expected_position()
    {
        var repair = new MediaTimestampRepair();
        repair.Next(0, 8_589_934_592L - 45_000, 45_000, 45_000, 450_000);
        var wrapped = repair.Next(0, 0, 45_000, 45_000, 450_000);
        var afterWrap = repair.Next(0, 45_000, 45_000, 45_000, 450_000);
        var jumped = repair.Next(0, 45_000 + 8_589_934_592L + 10_000_000, 45_000, 45_000, 450_000);

        Assert.Equal(8_589_934_592L, wrapped.PresentationTimestamp);
        Assert.Equal(8_589_934_592L + 45_000, afterWrap.PresentationTimestamp);
        Assert.Equal(8_589_934_592L + 90_000, jumped.PresentationTimestamp);
    }

    [Fact]
    public void A_new_epoch_discards_the_previous_offset()
    {
        var repair = new MediaTimestampRepair();
        repair.Next(4, 100, 40, 40, 1000);
        repair.Next(4, 10_000, 40, 40, 1000);

        var seek = repair.Next(5, 10_000, 40, 40, 1000);

        Assert.Equal(new RepairedMediaTimestamp(10_000, 40), seek);
    }
}
