using HomePlayer.Core;
using HomePlayer.Data;

namespace HomePlayer.Library.Tests;

public class LibraryCollectionTests
{
    [Fact]
    public void Tags_manual_collections_and_smart_rules_filter_items()
    {
        var directory = Path.Combine(Path.GetTempPath(), "HomePlayer.Collections." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var repository = new LibraryRepository(Path.Combine(directory, "library.db"));
            var library = new MediaLibrary { Id = "library", Name = "Test", RootPath = directory, Kind = LibraryKind.Raw };
            repository.InsertLibrary(library);
            var first = AddItem(repository, library.Id, "科幻.mkv", "科幻电影");
            var second = AddItem(repository, library.Id, "second.mkv", "纪录片");
            repository.SetTags([first.Id, second.Id], ["待看"]);
            repository.SetTags([first.Id], ["科幻"]);
            var manual = repository.CreateCollection("精选", MediaCollectionKind.Manual);
            repository.AddToCollection(manual.Id, [first.Id]);
            var smart = repository.CreateCollection("科幻未看", MediaCollectionKind.Smart, new SmartCollectionRule("科幻", "科幻", WatchFilter.Unwatched, true));
            repository.SetFavorite(first.Id, true);

            Assert.Equal(2, repository.ListTags().Count);
            Assert.Contains("科幻", repository.ListTags());
            Assert.Contains("待看", repository.ListTags());
            Assert.Equal(first.Id, Assert.Single(repository.ListItemsPage(new MediaLibraryQuery { LibraryId = library.Id, CollectionId = manual.Id }, SortMode.FileName, 0, 10)).Id);
            var smartItems = repository.ListItemsPage(new MediaLibraryQuery
            {
                LibraryId = library.Id,
                AdditionalSearch = smart.Rule.Search,
                AdditionalTag = smart.Rule.Tag,
                Watch = smart.Rule.Watch,
                FavoritesOnly = smart.Rule.FavoritesOnly
            }, SortMode.FileName, 0, 10);
            Assert.Equal(first.Id, Assert.Single(smartItems).Id);

            repository.DeleteItem(first.Id);
            Assert.Empty(repository.ListItemsPage(new MediaLibraryQuery { CollectionId = manual.Id }, SortMode.FileName, 0, 10));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static MediaItem AddItem(LibraryRepository repository, string libraryId, string path, string title)
    {
        var item = new MediaItem
        {
            Id = Guid.NewGuid().ToString("N"),
            LibraryId = libraryId,
            RelativePath = path,
            Title = title,
            Status = ItemStatus.Ready
        };
        repository.InsertItem(item);
        return item;
    }
}
