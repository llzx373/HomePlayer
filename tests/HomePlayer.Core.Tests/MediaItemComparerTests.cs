using HomePlayer.Core;

namespace HomePlayer.Core.Tests;

public class MediaItemComparerTests
{
    [Fact]
    public void File_name_sort_ignores_case()
    {
        var alpha = new MediaItem { RelativePath = "alpha.mkv" };
        var beta = new MediaItem { RelativePath = "Beta.mkv" };

        Assert.True(MediaItemComparer.Compare(alpha, beta, SortMode.FileName) < 0);
        Assert.True(MediaItemComparer.Compare(beta, alpha, SortMode.FileName) > 0);
        Assert.Equal(0, MediaItemComparer.Compare(alpha, alpha, SortMode.FileName));
    }

    [Fact]
    public void Modified_sort_is_descending()
    {
        var older = new MediaItem { RelativePath = "a.mkv", MtimeUtcTicks = 100 };
        var newer = new MediaItem { RelativePath = "b.mkv", MtimeUtcTicks = 200 };

        Assert.True(MediaItemComparer.Compare(newer, older, SortMode.ModifiedDesc) < 0);
        Assert.True(MediaItemComparer.Compare(older, newer, SortMode.ModifiedDesc) > 0);
    }

    [Fact]
    public void Title_sort_ignores_case()
    {
        var alpha = new MediaItem { Title = "alpha" };
        var beta = new MediaItem { Title = "Beta" };

        Assert.True(MediaItemComparer.Compare(alpha, beta, SortMode.Title) < 0);
        Assert.True(MediaItemComparer.Compare(beta, alpha, SortMode.Title) > 0);
    }

    [Fact]
    public void Year_sort_is_descending_then_title()
    {
        var newer = new MediaItem { Title = "Zeta", Year = 2020 };
        var older = new MediaItem { Title = "Alpha", Year = 1999 };
        var sameYearA = new MediaItem { Title = "Alpha", Year = 2020 };
        var sameYearB = new MediaItem { Title = "Beta", Year = 2020 };

        Assert.True(MediaItemComparer.Compare(newer, older, SortMode.YearDesc) < 0);
        Assert.True(MediaItemComparer.Compare(sameYearA, sameYearB, SortMode.YearDesc) < 0);
    }

    [Fact]
    public void Title_sort_falls_back_to_file_name()
    {
        var title = new MediaItem { Title = "Alpha", RelativePath = "z.mkv" };
        var file = new MediaItem { RelativePath = "beta.mkv" };

        Assert.True(MediaItemComparer.Compare(title, file, SortMode.Title) < 0);
    }

    [Fact]
    public void Ties_are_broken_by_relative_path()
    {
        var first = new MediaItem { RelativePath = "a/x.mkv", MtimeUtcTicks = 5 };
        var second = new MediaItem { RelativePath = "b/x.mkv", MtimeUtcTicks = 5 };

        Assert.True(MediaItemComparer.Compare(first, second, SortMode.ModifiedDesc) < 0);
    }
}
