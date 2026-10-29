using System.Collections.Concurrent;
using HomePlayer.Core;
using HomePlayer.Data;

namespace HomePlayer.Library.Tests;

public class LibraryIncrementalTests
{
    [Fact]
    public async Task Watcher_adds_and_deletes_one_file_without_a_full_rescan()
    {
        var directory = NewDirectory();
        var scans = new ConcurrentQueue<string>();
        try
        {
            using var repository = new LibraryRepository(Path.Combine(directory, "library.db"));
            var library = AddLibrary(repository, directory);
            await using var coordinator = new LibraryCoordinator(repository, () => null, root =>
            {
                scans.Enqueue(root);
                return VideoFiles.Scan(root);
            });
            coordinator.Start();
            await WaitFor(() => scans.Count == 1);
            var path = Path.Combine(directory, "one.mkv");
            await File.WriteAllBytesAsync(path, [1, 2, 3]);

            await WaitFor(() => repository.FindItemByPath(path) != null);
            Assert.Single(scans);
            repository.Save(new PlaybackState(path, 20, 100, false, DateTime.UtcNow.Ticks));
            repository.Flush();
            File.Delete(path);

            await WaitFor(() => repository.FindItemByPath(path) == null);
            Assert.Empty(repository.LoadPlaybackStates([path]));
            Assert.Single(scans);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task Watcher_rename_scans_only_the_new_directory()
    {
        var directory = NewDirectory();
        var oldDirectory = Path.Combine(directory, "old");
        Directory.CreateDirectory(oldDirectory);
        var oldPath = Path.Combine(oldDirectory, "one.mkv");
        await File.WriteAllBytesAsync(oldPath, [1]);
        var scans = new ConcurrentQueue<string>();
        try
        {
            using var repository = new LibraryRepository(Path.Combine(directory, "library.db"));
            var library = AddLibrary(repository, directory);
            await using var coordinator = new LibraryCoordinator(repository, () => null, root =>
            {
                scans.Enqueue(root);
                return VideoFiles.Scan(root);
            });
            coordinator.Start();
            await WaitFor(() => repository.FindItemByPath(oldPath) != null);
            var newDirectory = Path.Combine(directory, "new");
            Directory.Move(oldDirectory, newDirectory);
            var newPath = Path.Combine(newDirectory, "one.mkv");

            await WaitFor(() => repository.FindItemByPath(newPath) != null && repository.FindItemByPath(oldPath) == null);
            Assert.Equal(2, scans.Count);
            Assert.Equal(newDirectory, scans.Last());
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task Unavailable_library_recovers_and_watches_after_reconnection()
    {
        var parent = NewDirectory();
        var directory = Path.Combine(parent, "removable");
        try
        {
            using var repository = new LibraryRepository(Path.Combine(parent, "library.db"));
            var library = AddLibrary(repository, directory);
            await using var coordinator = new LibraryCoordinator(repository, () => null);
            coordinator.Start();
            await WaitFor(() => coordinator.ScanStatus(library.Id).Stage == LibraryScanStage.Unavailable);
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "found.mkv");
            await File.WriteAllBytesAsync(path, [1]);

            await WaitFor(() => repository.FindItemByPath(path) != null);
            Assert.NotEqual(LibraryScanStage.Unavailable, coordinator.ScanStatus(library.Id).Stage);
        }
        finally
        {
            DeleteDirectory(parent);
        }
    }

    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "HomePlayer.Incremental." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static MediaLibrary AddLibrary(LibraryRepository repository, string root)
    {
        var library = new MediaLibrary
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = "Test",
            RootPath = root,
            Kind = LibraryKind.Raw
        };
        repository.InsertLibrary(library);
        return library;
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 300; i++)
        {
            if (condition())
                return;
            await Task.Delay(50);
        }
        Assert.Fail("条件在 15 秒内没有成立。");
    }

    private static void DeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
