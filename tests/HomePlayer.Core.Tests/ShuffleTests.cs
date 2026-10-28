using HomePlayer.Core;

namespace HomePlayer.Core.Tests;

public class ShuffleTests
{
    [Fact]
    public void Order_returns_every_item_exactly_once()
    {
        var items = Enumerable.Range(0, 50).ToList();

        var shuffled = Shuffle.Order(items);

        Assert.Equal(items.Count, shuffled.Count);
        Assert.Equal(items, shuffled.OrderBy(value => value));
    }

    [Fact]
    public void Order_leaves_the_input_untouched()
    {
        List<int> items = [1, 2, 3, 4, 5];

        var shuffled = Shuffle.Order(items);

        Assert.Equal([1, 2, 3, 4, 5], items);
        Assert.NotSame(items, shuffled);
    }

    [Fact]
    public void Order_is_repeatable_with_a_fixed_seed()
    {
        var items = Enumerable.Range(0, 20).ToList();

        var first = Shuffle.Order(items, new Random(7));
        var second = Shuffle.Order(items, new Random(7));

        Assert.Equal(first, second);
    }

    [Fact]
    public void Empty_input_stays_empty()
    {
        Assert.Empty(Shuffle.Order<int>([]));
    }
}
