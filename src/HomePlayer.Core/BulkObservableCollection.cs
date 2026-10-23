using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace HomePlayer.Core;

/// <summary>
/// 允许一次性替换全部内容，只发出一个 Reset 通知。
/// 逐项 Add 会让列表控件对每一项都做一次增量处理，过滤上千条时会明显卡顿。
/// </summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    public BulkObservableCollection()
    {
    }

    public BulkObservableCollection(List<T> items) : base(items)
    {
    }

    public void ReplaceAll(IReadOnlyList<T> items)
    {
        Items.Clear();
        for (var i = 0; i < items.Count; i++)
            Items.Add(items[i]);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
