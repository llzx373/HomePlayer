namespace HomePlayer.Core.Tests;

public class FrameBufferPoolTests
{
    [Fact]
    public void Rent_reuses_a_returned_buffer()
    {
        var pool = new FrameBufferPool(2);
        var first = pool.Rent(1024);
        pool.Return(first);

        var second = pool.Rent(1024);

        Assert.Same(first, second);
        Assert.Equal(1, pool.AllocatedCount);
        Assert.Equal(1, pool.ReusedCount);
    }

    [Fact]
    public void Rent_drops_buffers_when_the_frame_size_changes()
    {
        var pool = new FrameBufferPool(2);
        var small = pool.Rent(1024);
        pool.Return(small);
        Assert.Equal(1, pool.FreeCount);

        var large = pool.Rent(4096);

        Assert.NotSame(small, large);
        Assert.Equal(4096, large.Length);
        Assert.Equal(0, pool.FreeCount);
        pool.Return(large);
        Assert.Equal(1, pool.FreeCount);
    }

    [Fact]
    public void Return_keeps_at_most_the_configured_capacity()
    {
        var pool = new FrameBufferPool(2);
        var first = pool.Rent(64);
        var second = pool.Rent(64);
        var third = pool.Rent(64);

        pool.Return(first);
        pool.Return(second);
        pool.Return(third);

        Assert.Equal(2, pool.FreeCount);
    }

    [Fact]
    public void Return_ignores_null_and_foreign_buffers()
    {
        var pool = new FrameBufferPool(2);
        pool.Return(null);
        pool.Return(new byte[100]);

        Assert.Equal(0, pool.FreeCount);
    }

    [Fact]
    public void Rent_rejects_invalid_length()
    {
        var pool = new FrameBufferPool(2);
        Assert.Throws<ArgumentOutOfRangeException>(() => pool.Rent(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => pool.Rent(-1));
    }

    [Fact]
    public void Clear_drops_the_cached_buffers()
    {
        var pool = new FrameBufferPool(2);
        pool.Return(pool.Rent(128));
        pool.Clear();

        Assert.Equal(0, pool.FreeCount);
        var again = pool.Rent(128);
        Assert.Equal(2, pool.AllocatedCount);
        Assert.NotNull(again);
    }

    [Fact]
    public void Steady_state_playback_does_not_allocate_per_frame()
    {
        var pool = new FrameBufferPool(4);
        var size = 1920 * 1080 * 4;
        var inFlight = new List<byte[]>();

        for (var frame = 0; frame < 240; frame++)
        {
            inFlight.Add(pool.Rent(size));
            if (inFlight.Count > 2)
            {
                pool.Return(inFlight[0]);
                inFlight.RemoveAt(0);
            }
        }

        Assert.True(
            pool.AllocatedCount <= 3,
            $"expected the pool to reach a steady state, allocated {pool.AllocatedCount} buffers");
        Assert.True(pool.ReusedCount >= 237);
    }

    [Fact]
    public void Concurrent_rent_and_return_never_hands_out_the_same_buffer_twice()
    {
        var pool = new FrameBufferPool(4);
        var handedOut = new System.Collections.Concurrent.ConcurrentDictionary<byte[], byte>(ReferenceEqualityComparer.Instance);
        var duplicates = 0;

        Parallel.For(0, 2000, index =>
        {
            var buffer = pool.Rent(256);
            if (!handedOut.TryAdd(buffer, 0))
                Interlocked.Increment(ref duplicates);
            handedOut.TryRemove(buffer, out _);
            pool.Return(buffer);
        });

        Assert.Equal(0, duplicates);
    }
}
