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

        public MediaItem AddItem(MediaLibrary library, string relativePath)
        {
            var item = new MediaItem
            {
                Id = Guid.NewGuid().ToString("N"),
                LibraryId = library.Id,
                RelativePath = relativePath,
                Size = 1,
                MtimeUtcTicks = DateTime.UtcNow.Ticks,
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
