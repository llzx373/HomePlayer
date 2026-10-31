using HomePlayer.Ffmpeg;

namespace HomePlayer.Playback.Tests;

public sealed class MediaDecodeRecoveryPolicyTests
{
    [Fact]
    public void Consecutive_errors_are_bounded_and_success_resets_only_the_streak()
    {
        var policy = new MediaDecodeRecoveryPolicy(2);
        Assert.True(policy.TryRecover(-100));
        Assert.True(policy.TryRecover(-101));
        Assert.False(policy.TryRecover(-102));
        Assert.Equal(3, policy.Total);
        Assert.Equal(-102, policy.LastError);
        policy.Reset();
        Assert.True(policy.TryRecover(-103));
        Assert.Equal(4, policy.Total);
    }

    [Fact]
    public void New_media_resets_recovery_evidence()
    {
        var policy = new MediaDecodeRecoveryPolicy();
        policy.TryRecover(-1);
        policy.ResetAll();
        Assert.Equal(0, policy.Total);
        Assert.Null(policy.LastError);
        Assert.Throws<ArgumentOutOfRangeException>(() => new MediaDecodeRecoveryPolicy(0));
    }
}
