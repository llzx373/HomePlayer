using HomePlayer.Core;
using HomePlayer.Data;
using HomePlayer.Library;

namespace HomePlayer.Library.Tests;

public class LibraryCoordinatorTests
{
    [Fact]
    public async Task Add_and_remove_mark_all_items_dirty()
    {
        var directory = Path.Combine(Path.GetTempPath(), "HomePlayer.Tests." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var database = Path.Combine(directory, "library.db");
        try
        {
            using var repository = new LibraryRepository(database);
            await using var coordinator = new LibraryCoordinator(repository, () => null);

            var library = coordinator.Add("Test", directory, LibraryKind.Raw);

            var added = coordinator.TakeDirtyItems();
            Assert.True(added.All);
            Assert.Empty(added.Ids);
            Assert.NotNull(repository.GetLibrary(library.Id));

            await coordinator.RemoveAsync(library.Id);

            var removed = coordinator.TakeDirtyItems();
            Assert.True(removed.All);
            Assert.Null(repository.GetLibrary(library.Id));
        }
        finally
        {
            try
            {
                Directory.Delete(directory, true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    [Fact]
    public async Task Reconcile_builds_and_updates_the_folder_table()
    {
        var directory = Path.Combine(Path.GetTempPath(), "HomePlayer.Tests." + Guid.NewGuid().ToString("N"));
        var nested = Path.Combine(directory, "sub", "deep");
        Directory.CreateDirectory(nested);
        File.WriteAllBytes(Path.Combine(directory, "root.mkv"), []);
        File.WriteAllBytes(Path.Combine(directory, "sub", "b.mkv"), []);
        File.WriteAllBytes(Path.Combine(nested, "c.mkv"), []);
        try
        {
            using var repository = new LibraryRepository(Path.Combine(directory, "library.db"));
            await using var coordinator = new LibraryCoordinator(repository, () => null);
            var library = coordinator.Add("Test", directory, LibraryKind.Raw);

            await WaitFor(() => repository.GetFolder(library.Id, "sub/deep") != null);
            var root = repository.GetFolder(library.Id, "");
            Assert.NotNull(root);
            Assert.Equal(1, root.DirectCount);
            Assert.Equal(3, root.RecursiveCount);
            var sub = repository.GetFolder(library.Id, "sub");
            Assert.NotNull(sub);
            Assert.Equal(1, sub.DirectCount);
            Assert.Equal(2, sub.RecursiveCount);

            File.Delete(Path.Combine(nested, "c.mkv"));
            Directory.Delete(nested);
            coordinator.Refresh(library.Id);

            await WaitFor(() =>
            {
                var refreshed = repository.GetFolder(library.Id, "sub");
                return refreshed is { RecursiveCount: 1 } && repository.GetFolder(library.Id, "sub/deep") == null;
            });
        }
        finally
        {
            try
            {
                Directory.Delete(directory, true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    [Fact]
    public async Task Partial_scan_preserves_unconfirmed_items_until_a_complete_scan()
    {
        var directory = Path.Combine(Path.GetTempPath(), "HomePlayer.Tests." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var repository = new LibraryRepository(Path.Combine(directory, "library.db"));
            var scan = new VideoScanResult([], [new DirectoryScanFailure(directory, "访问被拒绝")]);
            await using var coordinator = new LibraryCoordinator(repository, () => null, _ => scan);
            var library = new MediaLibrary
            {
                Id = "library",
                Name = "Test",
                RootPath = directory,
                Kind = LibraryKind.Raw
            };
            repository.InsertLibrary(library);
            var item = new MediaItem
            {
                Id = "item",
                LibraryId = library.Id,
                RelativePath = "missing.mkv",
                Status = ItemStatus.Ready
            };
            repository.InsertItem(item);
            var fullPath = item.FullPath(directory);
            repository.Save(new PlaybackState(fullPath, 30, 100, false, DateTime.UtcNow.Ticks));
            repository.Flush();

            coordinator.Refresh(library.Id);
            await WaitFor(() => coordinator.ScanWarning(library.Id) != null);

            Assert.NotNull(repository.GetItem(item.Id));
            Assert.NotNull(repository.LoadPlaybackStates([fullPath]).GetValueOrDefault(fullPath));
            Assert.Null(repository.GetLibrary(library.Id)?.LastFullScanUtc);

            scan = new VideoScanResult([], []);
            coordinator.Refresh(library.Id);
            await WaitFor(() => repository.GetItem(item.Id) == null);

            Assert.Null(coordinator.ScanWarning(library.Id));
            Assert.Empty(repository.LoadPlaybackStates([fullPath]));
        }
        finally
        {
            try
            {
                Directory.Delete(directory, true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    [Fact]
    public async Task Manual_requeue_rebuilds_metadata_without_clearing_playback_progress()
    {
        var directory = Path.Combine(Path.GetTempPath(), "HomePlayer.Tests." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var repository = new LibraryRepository(Path.Combine(directory, "library.db"));
            await using var coordinator = new LibraryCoordinator(repository, () => null);
            var library = new MediaLibrary
            {
                Id = "library",
                Name = "Test",
                RootPath = directory,
                Kind = LibraryKind.Raw
            };
            repository.InsertLibrary(library);
            var item = new MediaItem
            {
                Id = "item",
                LibraryId = library.Id,
                RelativePath = "video.mkv",
                Status = ItemStatus.Ready
            };
            repository.InsertItem(item);
            var fullPath = item.FullPath(directory);
            File.WriteAllBytes(fullPath, [1, 2, 3]);
            var state = new PlaybackState(fullPath, 30, 100, false, DateTime.UtcNow.Ticks);
            repository.Save(state);
            repository.Flush();

            coordinator.Requeue(item.Id);

            Assert.Equal(ItemStatus.Pending, repository.GetItem(item.Id)?.Status);
            Assert.Equal(state, repository.LoadPlaybackStates([fullPath])[fullPath]);
        }
        finally
        {
            try
            {
                Directory.Delete(directory, true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 200; i++)
        {
            if (condition())
                return;
            await Task.Delay(50);
        }

        Assert.Fail("条件在 10 秒内没有成立。");
    }

    [Fact]
    public async Task Start_can_run_more_than_once()
    {
        var directory = Path.Combine(Path.GetTempPath(), "HomePlayer.Tests." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var database = Path.Combine(directory, "library.db");
        try
        {
            using var repository = new LibraryRepository(database);
            await using var coordinator = new LibraryCoordinator(repository, () => null);
            coordinator.Add("Test", directory, LibraryKind.Raw);

            await Task.Run(coordinator.Start);
            coordinator.Start();

            Assert.NotEmpty(repository.ListLibraries());
        }
        finally
        {
            try
            {
                Directory.Delete(directory, true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    [Fact]
    public async Task Empty_database_has_no_dirty_items()
    {
        var directory = Path.Combine(Path.GetTempPath(), "HomePlayer.Tests." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var database = Path.Combine(directory, "library.db");
        try
        {
            using var repository = new LibraryRepository(database);
            await using var coordinator = new LibraryCoordinator(repository, () => null);

            var dirty = coordinator.TakeDirtyItems();

            Assert.False(dirty.All);
            Assert.Empty(dirty.Ids);
        }
        finally
        {
            try
            {
                Directory.Delete(directory, true);
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
