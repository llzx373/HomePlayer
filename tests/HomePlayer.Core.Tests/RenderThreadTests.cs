namespace HomePlayer.Core.Tests;

public class RenderThreadTests
{
    [Fact]
    public void Runs_posted_actions_in_order()
    {
        using var thread = new RenderThread("test");
        var seen = new List<int>();
        using var done = new ManualResetEventSlim(false);

        for (var i = 0; i < 100; i++)
        {
            var value = i;
            thread.Post(() => seen.Add(value));
        }

        thread.Post(done.Set);

        Assert.True(done.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(Enumerable.Range(0, 100), seen);
    }

    [Fact]
    public void Reports_the_rendering_thread_identity()
    {
        using var thread = new RenderThread("test");
        using var done = new ManualResetEventSlim(false);
        var onThread = false;

        thread.Post(() =>
        {
            onThread = thread.IsCurrentThread;
            done.Set();
        });

        Assert.True(done.Wait(TimeSpan.FromSeconds(5)));
        Assert.True(onThread);
    }

    [Fact]
    public void An_exception_does_not_stop_later_work()
    {
        using var thread = new RenderThread("test");
        using var done = new ManualResetEventSlim(false);

        thread.Post(() => throw new InvalidOperationException("boom"));
        thread.Post(done.Set);

        Assert.True(done.Wait(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Invoke_returns_value_and_propagates_errors()
    {
        using var thread = new RenderThread("test");

        Assert.Equal(42, thread.Invoke(() => 42));
        Assert.Throws<InvalidOperationException>(() => thread.Invoke<int>(() => throw new InvalidOperationException("boom")));
    }

    [Fact]
    public void Dispose_runs_work_that_is_already_queued()
    {
        var thread = new RenderThread("test");
        var ran = false;
        thread.Post(() => ran = true);

        thread.Dispose();

        Assert.True(ran);
        thread.Post(() => ran = false);
        Assert.True(ran);
    }
}
