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

    [Theory]
    [InlineData(RendererGate.Stopped, RendererGate.Paused, true)]
    [InlineData(RendererGate.Running, RendererGate.Paused, false)]
    [InlineData(RendererGate.Paused, RendererGate.Paused, false)]
    [InlineData(RendererGate.Stopped, RendererGate.Running, false)]
    public void Preroll_is_only_required_when_leaving_stopped(int previous, int next, bool expected)
    {
        Assert.Equal(expected, RendererGate.NeedsPreroll(previous, next));
    }

    [Fact]
    public void Only_the_first_sample_consumes_preroll()
    {
        var waiting = RendererGate.PrerollWaiting;
        Assert.True(RendererGate.ConsumePreroll(ref waiting));
        Assert.Equal(RendererGate.PrerollReady, waiting);
        Assert.False(RendererGate.ConsumePreroll(ref waiting));
    }
}
