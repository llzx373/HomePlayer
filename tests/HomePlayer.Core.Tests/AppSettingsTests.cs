using System.Text.Json;
using HomePlayer.Core;

namespace HomePlayer.Core.Tests;

public class AppSettingsTests
{
    [Fact]
    public void Library_view_state_round_trips_through_json()
    {
        var settings = new AppSettings
        {
            AutoPlayNextEpisode = false,
            LibraryView = new LibraryViewState
            {
                LibraryId = "lib",
                FolderPath = "剧集/第 1 季",
                Recursive = false,
                Page = 3,
                PageSize = 100,
                SortMode = "ModifiedDesc",
                Layout = LibraryLayout.List,
                CardSize = LibraryCardSize.Large,
                InfoDisplay = LibraryInfoDisplay.Technical,
                ExpandedFolders = ["剧集", "剧集/第 1 季"]
            }
        };

        var json = JsonSerializer.Serialize(settings);
        var loaded = JsonSerializer.Deserialize<AppSettings>(json);

        Assert.NotNull(loaded);
        Assert.Equal("lib", loaded.LibraryView.LibraryId);
        Assert.Equal("剧集/第 1 季", loaded.LibraryView.FolderPath);
        Assert.False(loaded.LibraryView.Recursive);
        Assert.Equal(3, loaded.LibraryView.Page);
        Assert.Equal(100, loaded.LibraryView.PageSize);
        Assert.Equal("ModifiedDesc", loaded.LibraryView.SortMode);
        Assert.Equal(LibraryLayout.List, loaded.LibraryView.Layout);
        Assert.Equal(LibraryCardSize.Large, loaded.LibraryView.CardSize);
        Assert.Equal(LibraryInfoDisplay.Technical, loaded.LibraryView.InfoDisplay);
        Assert.False(loaded.AutoPlayNextEpisode);
        Assert.Equal(["剧集", "剧集/第 1 季"], loaded.LibraryView.ExpandedFolders);
    }

    [Fact]
    public void Missing_library_view_gets_defaults()
    {
        var loaded = JsonSerializer.Deserialize<AppSettings>("{}");

        Assert.NotNull(loaded);
        Assert.Equal(1, loaded.LibraryView.Page);
        Assert.Equal(PagedView.DefaultPageSize, loaded.LibraryView.PageSize);
        Assert.Empty(loaded.LibraryView.ExpandedFolders);
        Assert.Null(loaded.LibraryView.LibraryId);
    }
}
