using HomePlayer.Core;
using HomePlayer.Data;

namespace HomePlayer.Library.Tests;

public class LibraryRepositoryTests
{
    [Fact]
    public void GetItems_returns_the_requested_entries()
    {
        using var fixture = new RepositoryFixture();
        var library = fixture.AddLibrary("Test", LibraryKind.Raw);
        var items = Enumerable.Range(0, 5)
            .Select(i => fixture.AddItem(library, $"file{i}.mkv"))
            .ToArray();

        var loaded = fixture.Repository.GetItems(items.Select(item => item.Id).ToArray());

        Assert.Equal(
            items.Select(item => item.Id).OrderBy(id => id, StringComparer.Ordinal),
            loaded.Select(item => item.Id).OrderBy(id => id, StringComparer.Ordinal));
    }

    [Fact]
    public void GetItems_ignores_ids_that_no_longer_exist()
    {
        using var fixture = new RepositoryFixture();
        var library = fixture.AddLibrary("Test", LibraryKind.Raw);
        var item = fixture.AddItem(library, "only.mkv");

        var loaded = fixture.Repository.GetItems([item.Id, "missing"]);

        var single = Assert.Single(loaded);
        Assert.Equal(item.Id, single.Id);
    }

    [Fact]
    public void GetItems_chunks_large_requests()
    {
        using var fixture = new RepositoryFixture();
        var library = fixture.AddLibrary("Test", LibraryKind.Raw);
        var ids = new List<string>();
        for (var i = 0; i < 401; i++)
            ids.Add(fixture.AddItem(library, $"file{i}.mkv").Id);

        var loaded = fixture.Repository.GetItems(ids);

        Assert.Equal(401, loaded.Count);
    }

    [Fact]
    public void GetItems_with_no_ids_returns_empty()
    {
        using var fixture = new RepositoryFixture();
        Assert.Empty(fixture.Repository.GetItems([]));
    }

    [Fact]
    public void Count_and_page_return_sorted_windows()
    {
        using var fixture = new RepositoryFixture();
        var library = fixture.AddLibrary("Test", LibraryKind.Raw);
        for (var i = 0; i < 25; i++)
            fixture.AddItem(library, $"file{i:00}.mkv");

        Assert.Equal(25, fixture.Repository.CountItems(library.Id, null, true, null, LibraryFilter.All));

        var first = fixture.Repository.ListItemsPage(
            library.Id, null, true, null, LibraryFilter.All, SortMode.FileName, 0, 10);
        Assert.Equal(
            Enumerable.Range(0, 10).Select(i => $"file{i:00}.mkv"),
            first.Select(item => item.FileName));

        var lastPage = fixture.Repository.ListItemsPage(
            library.Id, null, true, null, LibraryFilter.All, SortMode.FileName, 20, 10);
        Assert.Equal(
            Enumerable.Range(20, 5).Select(i => $"file{i:00}.mkv"),
            lastPage.Select(item => item.FileName));
    }

    [Fact]
    public void Directory_filter_honours_the_recursive_switch()
    {
        using var fixture = new RepositoryFixture();
        var library = fixture.AddLibrary("Test", LibraryKind.Raw);
        fixture.AddItem(library, "root.mkv");
        fixture.AddItem(library, "a/direct.mkv");
        fixture.AddItem(library, "a/b/nested.mkv");
        fixture.AddItem(library, "other/x.mkv");

        Assert.Equal(1, fixture.Repository.CountItems(library.Id, null, false, null, LibraryFilter.All));
        Assert.Equal(4, fixture.Repository.CountItems(library.Id, null, true, null, LibraryFilter.All));
        Assert.Equal(1, fixture.Repository.CountItems(library.Id, "a", false, null, LibraryFilter.All));
        Assert.Equal(2, fixture.Repository.CountItems(library.Id, "a", true, null, LibraryFilter.All));
        Assert.Equal(1, fixture.Repository.CountItems(library.Id, "a/b", true, null, LibraryFilter.All));

        var nested = fixture.Repository.ListItemsPage(
            library.Id, "a", true, null, LibraryFilter.All, SortMode.FileName, 0, 10);
        Assert.Equal(["direct.mkv", "nested.mkv"], nested.Select(item => item.FileName));
    }

    [Fact]
    public void Search_matches_titles_and_file_names_and_escapes_wildcards()
    {
        using var fixture = new RepositoryFixture();
        var library = fixture.AddLibrary("Test", LibraryKind.Raw);
        var titled = fixture.AddItem(library, "z.mkv");
        titled.Title = "Alpha Movie";
        fixture.Repository.SaveMetadata(titled);
        fixture.AddItem(library, "beta.mkv");
        fixture.AddItem(library, "100%.mkv");

        Assert.Equal(1, fixture.Repository.CountItems(library.Id, null, true, "alpha", LibraryFilter.All));
        Assert.Equal(1, fixture.Repository.CountItems(library.Id, null, true, "BETA", LibraryFilter.All));
        Assert.Equal(1, fixture.Repository.CountItems(library.Id, null, true, "%", LibraryFilter.All));
        Assert.Equal(0, fixture.Repository.CountItems(library.Id, null, true, "_", LibraryFilter.All));
    }

    [Fact]
    public void Status_and_review_filters_are_pushed_down()
    {
        using var fixture = new RepositoryFixture();
        var library = fixture.AddLibrary("Test", LibraryKind.Catalog);
        var failed = fixture.AddItem(library, "failed.mkv");
        var review = fixture.AddItem(library, "review.mkv");
        fixture.AddItem(library, "ok.mkv");
        fixture.Repository.SetStatus(failed.Id, ItemStatus.Failed, "坏了");
        review.MatchStatus = MatchStatus.NeedsReview;
        fixture.Repository.SaveMetadata(review);

        Assert.Equal(1, fixture.Repository.CountItems(library.Id, null, true, null, LibraryFilter.Failed));
        Assert.Equal(1, fixture.Repository.CountItems(library.Id, null, true, null, LibraryFilter.NeedsReview));
        Assert.Equal(3, fixture.Repository.CountItems(library.Id, null, true, null, LibraryFilter.All));
    }

    [Fact]
    public void List_all_paths_returns_absolute_paths_in_sort_order()
    {
        using var fixture = new RepositoryFixture();
        var library = fixture.AddLibrary("Test", LibraryKind.Raw);
        fixture.AddItem(library, "b.mkv");
        fixture.AddItem(library, "a.mkv");

        var paths = fixture.Repository.ListAllPaths(
            library.Id, null, true, null, LibraryFilter.All, SortMode.FileName);

        Assert.Equal(
            [Path.Combine(library.RootPath, "a.mkv"), Path.Combine(library.RootPath, "b.mkv")],
            paths);
    }

    [Fact]
    public void Folder_rows_are_listed_by_parent_and_report_children()
    {
        using var fixture = new RepositoryFixture();
        var library = fixture.AddLibrary("Test", LibraryKind.Raw);
        var folders = new List<MediaFolder>
        {
            new() { LibraryId = library.Id, RelativePath = "", Name = "库根", DirectCount = 1, RecursiveCount = 3 },
            new() { LibraryId = library.Id, RelativePath = "a", ParentPath = "", Name = "a", DirectCount = 1, RecursiveCount = 2 },
            new() { LibraryId = library.Id, RelativePath = "a/b", ParentPath = "a", Name = "b", DirectCount = 1, RecursiveCount = 1 }
        };
        fixture.Repository.SyncFolders(library.Id, folders);

        var root = Assert.Single(fixture.Repository.ListChildFolders(library.Id, null));
        Assert.Equal("a", root.RelativePath);
        Assert.True(root.HasChildren);
        Assert.Equal(2, root.RecursiveCount);

        var nested = Assert.Single(fixture.Repository.ListChildFolders(library.Id, "a"));
        Assert.Equal("a/b", nested.RelativePath);
        Assert.False(nested.HasChildren);

        var self = fixture.Repository.GetFolder(library.Id, "");
        Assert.NotNull(self);
        Assert.Equal(3, self.RecursiveCount);
    }

    [Fact]
    public void Sync_folders_updates_counts_and_removes_missing_folders()
    {
        using var fixture = new RepositoryFixture();
        var library = fixture.AddLibrary("Test", LibraryKind.Raw);
        fixture.Repository.SyncFolders(library.Id,
        [
            new MediaFolder { LibraryId = library.Id, RelativePath = "", Name = "库根", DirectCount = 1, RecursiveCount = 2 },
            new MediaFolder { LibraryId = library.Id, RelativePath = "a", ParentPath = "", Name = "a", DirectCount = 1, RecursiveCount = 1 },
            new MediaFolder { LibraryId = library.Id, RelativePath = "b", ParentPath = "", Name = "b", DirectCount = 1, RecursiveCount = 1 }
        ]);
        fixture.Repository.SaveFolderCover(library.Id, "a", @"C:\cover.jpg", "stamp");

        var removed = fixture.Repository.SyncFolders(library.Id,
        [
            new MediaFolder { LibraryId = library.Id, RelativePath = "", Name = "库根", DirectCount = 2, RecursiveCount = 3 },
            new MediaFolder { LibraryId = library.Id, RelativePath = "a", ParentPath = "", Name = "a", DirectCount = 2, RecursiveCount = 2 }
        ]);

        Assert.Equal(["b"], removed);
        Assert.Null(fixture.Repository.GetFolder(library.Id, "b"));
        var root = fixture.Repository.GetFolder(library.Id, "");
        Assert.NotNull(root);
        Assert.Equal(3, root.RecursiveCount);
        var a = fixture.Repository.GetFolder(library.Id, "a");
        Assert.NotNull(a);
        Assert.Equal(2, a.DirectCount);
        Assert.Equal("stamp", a.CoverStamp);
    }

    [Fact]
    public void Playback_state_round_trips_through_flush()
    {
        using var fixture = new RepositoryFixture();
        var path = @"D:\lib\a.mkv";
        fixture.Repository.Save(new PlaybackState(path, 42, 100, false, 1));
        fixture.Repository.Save(new PlaybackState(@"D:\lib\b.mkv", 0, 50, true, 2));
        fixture.Repository.Flush();

        var all = fixture.Repository.LoadAll(10);
        Assert.Equal(2, all.Count);
        Assert.Equal(42, all[path].PositionSeconds);
        Assert.True(all[@"D:\lib\b.mkv"].Finished);
        Assert.Equal(100, fixture.Repository.LoadPlaybackStates([path])[path].DurationSeconds, 3);
    }

    [Fact]
    public void Load_all_keeps_only_the_most_recent_entries()
    {
        using var fixture = new RepositoryFixture();
        fixture.Repository.Save(new PlaybackState(@"D:\lib\old.mkv", 1, 10, false, 1));
        fixture.Repository.Save(new PlaybackState(@"D:\lib\new.mkv", 2, 10, false, 3));
        fixture.Repository.Save(new PlaybackState(@"D:\lib\middle.mkv", 3, 10, false, 2));
        fixture.Repository.Flush();

        var recent = fixture.Repository.LoadAll(2);

        Assert.Equal(2, recent.Count);
        Assert.Contains(@"D:\lib\new.mkv", recent.Keys);
        Assert.Contains(@"D:\lib\middle.mkv", recent.Keys);
    }

    [Fact]
    public void Recent_queries_return_ordered_items_and_match_absolute_paths()
    {
        using var fixture = new RepositoryFixture();
        var library = fixture.AddLibrary("Test", LibraryKind.Raw);
        fixture.AddItem(library, "older.mkv", addedTicks: 10);
        var newer = fixture.AddItem(library, "a/newer.mkv", addedTicks: 20);

        Assert.Equal(newer.Id, Assert.Single(fixture.Repository.ListRecentlyAdded(1)).Id);
        Assert.Equal(newer.Id, fixture.Repository.FindItemByPath(newer.FullPath(library.RootPath))?.Id);
        Assert.Null(fixture.Repository.FindItemByPath(Path.Combine(library.RootPath, "missing.mkv")));

        fixture.Repository.Save(new PlaybackState(@"D:\lib\old.mkv", 1, 10, false, 1));
        fixture.Repository.Save(new PlaybackState(@"D:\lib\new.mkv", 2, 10, false, 3));
        fixture.Repository.Save(new PlaybackState(@"D:\lib\middle.mkv", 3, 10, false, 2));
        fixture.Repository.Flush();
        Assert.Equal(
            [@"D:\lib\new.mkv", @"D:\lib\middle.mkv"],
            fixture.Repository.ListRecentStates(2).Select(state => state.Path));
    }

    [Fact]
    public void Clear_history_removes_saved_and_pending_states()
    {
        using var fixture = new RepositoryFixture();
        fixture.Repository.Save(new PlaybackState(@"D:\lib\saved.mkv", 20, 100, false, 1));
        fixture.Repository.Flush();
        fixture.Repository.Save(new PlaybackState(@"D:\lib\pending.mkv", 30, 100, false, 2));

        fixture.Repository.ClearPlaybackStates();
        fixture.Repository.Flush();

        Assert.Empty(fixture.Repository.LoadAll(10));
    }

    [Fact]
    public void Changed_or_deleted_items_drop_their_playback_state()
    {
        using var fixture = new RepositoryFixture();
        var library = fixture.AddLibrary("Test", LibraryKind.Raw);
        var changed = fixture.AddItem(library, "a/changed.mkv");
        var deleted = fixture.AddItem(library, "b/deleted.mkv");
        fixture.Repository.Save(new PlaybackState(changed.FullPath(library.RootPath), 20, 100, false, 1));
        fixture.Repository.Save(new PlaybackState(deleted.FullPath(library.RootPath), 30, 100, false, 2));
        fixture.Repository.Flush();

        fixture.Repository.MarkChanged(changed, 999, changed.MtimeUtcTicks + 10);
        fixture.Repository.DeleteItem(deleted.Id);

        Assert.Empty(fixture.Repository.LoadAll(10));
    }

    private sealed class RepositoryFixture : IDisposable
    {
        private readonly string _directory;

        public RepositoryFixture()
        {
            _directory = Path.Combine(Path.GetTempPath(), "HomePlayer.Repo." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            Repository = new LibraryRepository(Path.Combine(_directory, "library.db"));
        }

        public LibraryRepository Repository { get; }

        public MediaLibrary AddLibrary(string name, LibraryKind kind)
        {
            var library = new MediaLibrary
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = name,
                RootPath = _directory,
                Kind = kind
            };
            Repository.InsertLibrary(library);
            return library;
        }

        public MediaItem AddItem(MediaLibrary library, string relativePath, long addedTicks = 0)
        {
            var item = new MediaItem
            {
                Id = Guid.NewGuid().ToString("N"),
                LibraryId = library.Id,
                RelativePath = relativePath,
                Size = 1,
                MtimeUtcTicks = DateTime.UtcNow.Ticks,
                AddedUtcTicks = addedTicks,
                Status = ItemStatus.Ready
            };
            Repository.InsertItem(item);
            return item;
        }

        public void Dispose()
        {
            Repository.Dispose();
            try
            {
                Directory.Delete(_directory, true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
