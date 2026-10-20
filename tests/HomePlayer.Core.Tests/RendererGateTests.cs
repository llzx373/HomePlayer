using HomePlayer.Core;

namespace HomePlayer.Core.Tests;

public class RendererGateTests
{
    [Fact]
    public void Paused_samples_wait_instead_of_presenting()
    {
        Assert.Null(RendererGate.Decide(RendererGate.Paused, false, 40_000));
    }

    [Theory]
    [InlineData(RendererGate.Stopped, false, 40_000, false)]
    [InlineData(RendererGate.Paused, true, 40_000, false)]
    [InlineData(RendererGate.Running, true, 40_000, false)]
    [InlineData(RendererGate.Running, false, 0, true)]
    public void Stopped_or_flushing_drops_and_untimed_running_presents(
        int state, bool flushing, long streamTime, bool expected)
    {
        Assert.Equal(expected, RendererGate.Decide(state, flushing, streamTime));
    }

    [Fact]
    public void Running_timed_samples_wait_for_the_clock()
    {
        Assert.Null(RendererGate.Decide(RendererGate.Running, false, 40_000));
    }
}
