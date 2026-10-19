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
