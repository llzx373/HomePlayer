using System.Collections.Specialized;

namespace HomePlayer.Core.Tests;

public class BulkObservableCollectionTests
{
    [Fact]
    public void ReplaceAll_raises_a_single_reset_notification()
    {
        var collection = new BulkObservableCollection<int>();
        var actions = new List<NotifyCollectionChangedAction>();
        collection.CollectionChanged += (_, e) => actions.Add(e.Action);

        collection.ReplaceAll([1, 2, 3]);

        Assert.Equal(new[] { NotifyCollectionChangedAction.Reset }, actions);
        Assert.Equal(new[] { 1, 2, 3 }, collection);
    }

    [Fact]
    public void ReplaceAll_can_shrink_the_content()
    {
        var collection = new BulkObservableCollection<int>(new List<int> { 1, 2, 3, 4, 5 });

        collection.ReplaceAll([9]);

        Assert.Equal(new[] { 9 }, collection);
    }

    [Fact]
    public void ReplaceAll_with_empty_list_clears_the_items()
    {
        var collection = new BulkObservableCollection<string>(new List<string> { "a", "b" });

        collection.ReplaceAll([]);

        Assert.Empty(collection);
    }
}
