using HomePlayer.Core;

namespace HomePlayer.Core.Tests;

public class PlaylistTests
{
    [Fact]
    public void Replace_sets_items_and_current_index()
    {
        var playlist = new Playlist();
        playlist.Replace(["a.mkv", "b.mkv", "c.mkv"], 1);

        Assert.Equal(3, playlist.Items.Count);
        Assert.Equal(1, playlist.CurrentIndex);
        Assert.Equal("b.mkv", playlist.Current?.DisplayName);
    }

    [Fact]
    public void Empty_replace_resets_current_index()
    {
        var playlist = new Playlist();
        playlist.Replace(["a.mkv"], 0);
        playlist.Replace([], 0);

        Assert.Empty(playlist.Items);
        Assert.Equal(-1, playlist.CurrentIndex);
        Assert.Null(playlist.Current);
    }

    [Fact]
    public void Next_stops_at_end_without_repeat()
    {
        var playlist = new Playlist();
        playlist.Replace(["a.mkv", "b.mkv", "c.mkv"], 0);

        Assert.Equal(1, playlist.NextIndex(false));
        playlist.CurrentIndex = 1;
        Assert.Equal(2, playlist.NextIndex(false));
        playlist.CurrentIndex = 2;
        Assert.Null(playlist.NextIndex(false));
    }

    [Fact]
    public void Next_wraps_with_repeat_all()
    {
        var playlist = new Playlist();
        playlist.Replace(["a.mkv", "b.mkv", "c.mkv"], 2);
        playlist.Repeat = RepeatMode.All;

        Assert.Equal(0, playlist.NextIndex(false));
    }

    [Fact]
    public void Repeat_one_restarts_current_track_at_end()
    {
        var playlist = new Playlist();
        playlist.Replace(["a.mkv", "b.mkv"], 1);
        playlist.Repeat = RepeatMode.One;

        Assert.Equal(1, playlist.NextIndex(true));
        Assert.Null(playlist.NextIndex(false));
        Assert.Equal(0, playlist.NextIndex(false) ?? 0);
    }

    [Fact]
    public void Previous_restarts_first_track_without_repeat()
    {
        var playlist = new Playlist();
        playlist.Replace(["a.mkv", "b.mkv", "c.mkv"], 0);

        Assert.Equal(0, playlist.PreviousIndex());
    }

    [Fact]
    public void Previous_wraps_to_last_track_with_repeat_all()
    {
        var playlist = new Playlist();
        playlist.Replace(["a.mkv", "b.mkv", "c.mkv"], 0);
        playlist.Repeat = RepeatMode.All;

        Assert.Equal(2, playlist.PreviousIndex());
    }

    [Fact]
    public void RemoveAt_keeps_current_item_or_clamps()
    {
        var playlist = new Playlist();
        playlist.Replace(["a.mkv", "b.mkv", "c.mkv"], 2);

        playlist.RemoveAt(0);
        Assert.Equal(2, playlist.Items.Count);
        Assert.Equal(1, playlist.CurrentIndex);
        Assert.Equal("c.mkv", playlist.Current?.DisplayName);

        playlist.RemoveAt(0);
        Assert.Equal(0, playlist.CurrentIndex);
        Assert.Equal("c.mkv", playlist.Current?.DisplayName);

        playlist.RemoveAt(0);
        Assert.Equal(-1, playlist.CurrentIndex);
        Assert.Null(playlist.Current);
    }

    [Fact]
    public void Move_keeps_current_item_selected()
    {
        var playlist = new Playlist();
        playlist.Replace(["a.mkv", "b.mkv", "c.mkv"], 1);
        var current = playlist.Current;

        playlist.Move(1, 2);

        Assert.Same(current, playlist.Current);
        Assert.Equal(2, playlist.CurrentIndex);
        Assert.Equal(["a.mkv", "c.mkv", "b.mkv"], playlist.Items.Select(item => item.DisplayName));
    }

    [Fact]
    public void Shuffle_skips_current_and_never_repeats_without_repeat()
    {
        var playlist = new Playlist();
        playlist.Replace(["a.mkv", "b.mkv", "c.mkv"], 0);
        playlist.Shuffle = true;

        var seen = new HashSet<int>();
        for (var i = 0; i < 10; i++)
        {
            var next = playlist.NextIndex(false);
            if (next == null)
                break;
            Assert.NotEqual(0, next.Value);
            Assert.True(seen.Add(next.Value));
            playlist.CurrentIndex = next.Value;
        }

        Assert.True(seen.Count <= 2);
    }

    [Fact]
    public void Shuffle_walk_uses_each_remaining_item_once()
    {
        var playlist = new Playlist();
        playlist.Replace(Enumerable.Range(0, 20).Select(i => $"{i}.mkv").ToList(), 0);
        playlist.Shuffle = true;

        var start = playlist.CurrentIndex;
        var visited = new HashSet<int>();
        while (playlist.NextIndex(false) is int next)
        {
            Assert.InRange(next, 0, 19);
            Assert.NotEqual(start, next);
            Assert.True(visited.Add(next), $"重复访问了第 {next} 项。");
            playlist.CurrentIndex = next;
        }

        Assert.Equal(19, visited.Count);
    }

    [Fact]
    public void RefreshShuffleOrder_does_not_change_items_or_current()
    {
        var playlist = new Playlist();
        playlist.Replace(["a.mkv", "b.mkv", "c.mkv"], 1);
        playlist.Shuffle = true;

        playlist.RefreshShuffleOrder();

        Assert.Equal(3, playlist.Items.Count);
        Assert.Equal(1, playlist.CurrentIndex);
    }
}
