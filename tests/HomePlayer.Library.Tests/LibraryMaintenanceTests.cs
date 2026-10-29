using HomePlayer.Core;
using HomePlayer.Data;

namespace HomePlayer.Library.Tests;

public class LibraryMaintenanceTests
{
    [Fact]
    public async Task Root_migration_preserves_items_progress_and_preferences()
    {
        var directory = Path.Combine(Path.GetTempPath(), "HomePlayer.Migrate." + Guid.NewGuid().ToString("N"));
        var oldRoot = Path.Combine(directory, "old");
        var newRoot = Path.Combine(directory, "new");
        Directory.CreateDirectory(oldRoot);
        Directory.CreateDirectory(newRoot);
        var oldPath = Path.Combine(oldRoot, "movie.mkv");
        var newPath = Path.Combine(newRoot, "movie.mkv");
        await File.WriteAllBytesAsync(oldPath, [1, 2, 3]);
        File.Copy(oldPath, newPath);
        File.SetLastWriteTimeUtc(newPath, File.GetLastWriteTimeUtc(oldPath));
        try
        {
            using var repository = new LibraryRepository(Path.Combine(directory, "library.db"));
            var library = new MediaLibrary { Id = "library", Name = "Test", RootPath = oldRoot, Kind = LibraryKind.Raw };
            repository.InsertLibrary(library);
            var item = new MediaItem
            {
                Id = "item",
                LibraryId = library.Id,
                RelativePath = "movie.mkv",
                Size = 3,
                MtimeUtcTicks = File.GetLastWriteTimeUtc(oldPath).Ticks,
                Status = ItemStatus.Ready,
                Title = "保留的标题"
            };
            repository.InsertItem(item);
            repository.SaveMetadata(item);
            var state = new PlaybackState(oldPath, 30, 100, false, DateTime.UtcNow.Ticks);
            repository.Save(state);
            repository.Flush();
            repository.SavePreference(new MediaPreference(oldPath, 1, "日语", "ja", SubtitlePreferenceMode.Auto, null, null, null, null, 250));
            await using var coordinator = new LibraryCoordinator(repository, () => null);

            await coordinator.MigrateRootAsync(library.Id, newRoot);

            Assert.Equal(newRoot, repository.GetLibrary(library.Id)?.RootPath);
            Assert.Equal("item", repository.FindItemByPath(newPath)?.Id);
            Assert.Equal(state.PositionSeconds, repository.Load(newPath)?.PositionSeconds);
            Assert.Null(repository.Load(oldPath));
            Assert.Equal(250, repository.LoadPreference(newPath)?.SubtitleDelayMs);
            Assert.Null(repository.LoadPreference(oldPath));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void Orphan_cleanup_preserves_current_library_and_existing_external_records()
    {
        var directory = Path.Combine(Path.GetTempPath(), "HomePlayer.Cleanup." + Guid.NewGuid().ToString("N"));
        var libraryRoot = Path.Combine(directory, "library");
        Directory.CreateDirectory(libraryRoot);
        try
        {
            Directory.CreateDirectory(Path.Combine(directory, "data"));
            using var repository = new LibraryRepository(Path.Combine(directory, "data", "library.db"));
            var library = new MediaLibrary { Id = "library", Name = "Test", RootPath = libraryRoot, Kind = LibraryKind.Raw };
            repository.InsertLibrary(library);
            var item = new MediaItem { Id = "item", LibraryId = library.Id, RelativePath = "missing.mkv", Status = ItemStatus.Ready };
            repository.InsertItem(item);
            var currentPath = item.FullPath(libraryRoot);
            var orphanPath = Path.Combine(directory, "orphan.mkv");
            var externalPath = Path.Combine(directory, "external.mkv");
            File.WriteAllBytes(externalPath, [1]);
            foreach (var path in new[] { currentPath, orphanPath, externalPath })
            {
                repository.Save(new PlaybackState(path, 10, 100, false, DateTime.UtcNow.Ticks));
                repository.SavePreference(MediaPreference.Empty(path));
            }
            repository.Flush();
            repository.Enqueue("orphan-work", LibraryKind.Raw);
            repository.SetFavorite("orphan-favorite", true);

            var result = repository.CleanupOrphanRecords();

            Assert.True(result.Total >= 4);
            Assert.NotNull(repository.Load(currentPath));
            Assert.NotNull(repository.Load(externalPath));
            Assert.Null(repository.Load(orphanPath));
            Assert.Null(repository.LoadPreference(orphanPath));
            Assert.NotNull(repository.LoadPreference(externalPath));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    private static string repositoryDatabase(string directory) => Path.Combine(directory, "data", "library.db");

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
