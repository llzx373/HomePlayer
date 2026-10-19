using HomePlayer.Core;

namespace HomePlayer.Core.Tests;

public class MediaItemComparerTests
{
    [Fact]
    public void Raw_library_sorts_by_file_name_ignoring_case()
    {
        var alpha = new MediaItem { RelativePath = "alpha.mkv" };
        var beta = new MediaItem { RelativePath = "Beta.mkv" };

        Assert.True(MediaItemComparer.Compare(alpha, beta, LibraryKind.Raw, byDate: false) < 0);
        Assert.True(MediaItemComparer.Compare(beta, alpha, LibraryKind.Raw, byDate: false) > 0);
        Assert.Equal(0, MediaItemComparer.Compare(alpha, alpha, LibraryKind.Raw, byDate: false));
    }

    [Fact]
    public void Raw_library_sorts_by_modified_time_descending()
    {
        var older = new MediaItem { RelativePath = "a.mkv", MtimeUtcTicks = 100 };
        var newer = new MediaItem { RelativePath = "b.mkv", MtimeUtcTicks = 200 };

        Assert.True(MediaItemComparer.Compare(newer, older, LibraryKind.Raw, byDate: true) < 0);
        Assert.True(MediaItemComparer.Compare(older, newer, LibraryKind.Raw, byDate: true) > 0);
    }

    [Fact]
    public void Catalog_sorts_by_title_ignoring_case()
    {
        var alpha = new MediaItem { Title = "alpha" };
        var beta = new MediaItem { Title = "Beta" };

        Assert.True(MediaItemComparer.Compare(alpha, beta, LibraryKind.Catalog, byDate: false) < 0);
        Assert.True(MediaItemComparer.Compare(beta, alpha, LibraryKind.Catalog, byDate: false) > 0);
    }

    [Fact]
    public void Catalog_sorts_by_year_descending_then_title()
    {
        var newer = new MediaItem { Title = "Zeta", Year = 2020 };
        var older = new MediaItem { Title = "Alpha", Year = 1999 };
        var sameYearA = new MediaItem { Title = "Alpha", Year = 2020 };
        var sameYearB = new MediaItem { Title = "Beta", Year = 2020 };

        Assert.True(MediaItemComparer.Compare(newer, older, LibraryKind.Catalog, byDate: true) < 0);
        Assert.True(MediaItemComparer.Compare(sameYearA, sameYearB, LibraryKind.Catalog, byDate: true) < 0);
    }

    [Fact]
    public void Catalog_falls_back_to_file_name_when_title_is_missing()
    {
        var title = new MediaItem { Title = "Alpha", RelativePath = "z.mkv" };
        var file = new MediaItem { RelativePath = "beta.mkv" };

        Assert.True(MediaItemComparer.Compare(title, file, LibraryKind.Catalog, byDate: false) < 0);
    }
}
