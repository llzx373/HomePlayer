using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace HomePlayer.Core;

public sealed class PlaylistItem : INotifyPropertyChanged
{
    private string? _error;

    public required string Path { get; init; }

    public string DisplayName => System.IO.Path.GetFileName(Path);

    public override string ToString() => DisplayName;

    public string? Error
    {
        get => _error;
        set
        {
            if (_error == value)
                return;
            _error = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class Playlist
{
    private readonly Random _random = new();
    private List<int> _shuffle = [];
    private int _shuffleCursor = -1;

    public ObservableCollection<PlaylistItem> Items { get; } = [];
    public int CurrentIndex { get; set; } = -1;
    public RepeatMode Repeat { get; set; } = RepeatMode.Off;
    public bool Shuffle { get; set; }

    public PlaylistItem? Current =>
        CurrentIndex >= 0 && CurrentIndex < Items.Count ? Items[CurrentIndex] : null;

    public void Replace(IReadOnlyList<string> paths, int startIndex)
    {
        Items.Clear();
        foreach (var path in paths)
            Items.Add(new PlaylistItem { Path = path });
        CurrentIndex = Items.Count == 0 ? -1 : Math.Clamp(startIndex, 0, Items.Count - 1);
        RebuildShuffle();
    }

    public void Add(IEnumerable<string> paths)
    {
        foreach (var path in paths)
            Items.Add(new PlaylistItem { Path = path });
        RebuildShuffle();
    }

    public void RemoveAt(int index)
    {
        if (index < 0 || index >= Items.Count)
            return;
        Items.RemoveAt(index);
        if (Items.Count == 0)
            CurrentIndex = -1;
        else if (index < CurrentIndex)
            CurrentIndex--;
        else if (index == CurrentIndex)
            CurrentIndex = Math.Min(CurrentIndex, Items.Count - 1);
        RebuildShuffle();
    }

    public void Move(int from, int to)
    {
        if (from < 0 || to < 0 || from >= Items.Count || to >= Items.Count || from == to)
            return;
        var current = Current;
        var item = Items[from];
        Items.RemoveAt(from);
        Items.Insert(to, item);
        CurrentIndex = current == null ? -1 : Items.IndexOf(current);
        RebuildShuffle();
    }

    public void Clear()
    {
        Items.Clear();
        CurrentIndex = -1;
        _shuffle.Clear();
        _shuffleCursor = -1;
    }

    public void RefreshShuffleOrder() => RebuildShuffle();

    public int? NextIndex(bool endOfTrack)
    {
        if (Items.Count == 0 || CurrentIndex < 0)
            return null;
        if (endOfTrack && Repeat == RepeatMode.One)
            return CurrentIndex;
        if (Shuffle)
            return NextShuffled();
        var next = CurrentIndex + 1;
        if (next < Items.Count)
            return next;
        return Repeat == RepeatMode.All ? 0 : null;
    }

    public int? PreviousIndex()
    {
        if (Items.Count == 0 || CurrentIndex < 0)
            return null;
        if (Shuffle)
        {
            if (_shuffleCursor > 0)
            {
                _shuffleCursor--;
                return _shuffle[_shuffleCursor];
            }

            return CurrentIndex;
        }

        if (CurrentIndex == 0)
            return Repeat == RepeatMode.All ? Items.Count - 1 : 0;
        return CurrentIndex - 1;
    }

    private int? NextShuffled()
    {
        if (_shuffle.Count != Items.Count)
            RebuildShuffle();
        if (_shuffle.Count == 0)
            return null;
        if (_shuffleCursor + 1 < _shuffle.Count)
        {
            _shuffleCursor++;
            return _shuffle[_shuffleCursor];
        }

        if (Repeat != RepeatMode.All)
            return null;
        RebuildShuffle();
        return _shuffle.Count == 0 ? null : _shuffle[_shuffleCursor];
    }

    private void RebuildShuffle()
    {
        _shuffle = Enumerable.Range(0, Items.Count).OrderBy(_ => _random.Next()).ToList();
        _shuffleCursor = CurrentIndex < 0 ? -1 : Math.Max(0, _shuffle.IndexOf(CurrentIndex));
        if (_shuffleCursor < 0 && _shuffle.Count > 0)
            _shuffleCursor = 0;
    }
}
