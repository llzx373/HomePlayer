using HomePlayer.Core;

namespace HomePlayer.Core.Tests;

public class PagedViewTests
{
    [Theory]
    [InlineData(0, 60, 1)]
    [InlineData(1, 60, 1)]
    [InlineData(59, 60, 1)]
    [InlineData(60, 60, 1)]
    [InlineData(61, 60, 2)]
    [InlineData(5842, 60, 98)]
    [InlineData(5842, 30, 195)]
    [InlineData(5842, 200, 30)]
    public void Total_pages_rounds_up(int total, int pageSize, int expected)
    {
        Assert.Equal(expected, PagedView.TotalPages(total, pageSize));
    }

    [Theory]
    [InlineData(1, 100, 60, 1)]
    [InlineData(0, 100, 60, 1)]
    [InlineData(999, 100, 60, 2)]
    [InlineData(2, 100, 60, 2)]
    public void Clamp_page_stays_in_range(int page, int total, int pageSize, int expected)
    {
        Assert.Equal(expected, PagedView.ClampPage(page, total, pageSize));
    }

    [Fact]
    public void Small_lists_show_every_page()
    {
        Assert.Equal(
            Enumerable.Range(1, 9).Select(page => (int?)page),
            PagedView.PageWindow(5, 9));
    }

    [Fact]
    public void Start_of_a_long_list_shows_first_seven_pages()
    {
        Assert.Equal(
            new int?[] { 1, 2, 3, 4, 5, 6, 7, null, 100 },
            PagedView.PageWindow(3, 100));
    }

    [Fact]
    public void Middle_of_a_long_list_shows_current_window()
    {
        Assert.Equal(
            new int?[] { 1, null, 48, 49, 50, 51, 52, null, 100 },
            PagedView.PageWindow(50, 100));
    }

    [Fact]
    public void End_of_a_long_list_shows_last_seven_pages()
    {
        Assert.Equal(
            new int?[] { 1, null, 94, 95, 96, 97, 98, 99, 100 },
            PagedView.PageWindow(98, 100));
    }

    [Fact]
    public void Short_first_and_last_windows_stay_seven_pages_wide()
    {
        Assert.Equal(
            new int?[] { 1, 2, 3, 4, 5, 6, 7, null, 100 },
            PagedView.PageWindow(1, 100));
        Assert.Equal(
            new int?[] { 1, null, 94, 95, 96, 97, 98, 99, 100 },
            PagedView.PageWindow(100, 100));
    }

    [Fact]
    public void Ten_pages_show_first_window_and_last_page()
    {
        Assert.Equal(
            new int?[] { 1, 2, 3, 4, 5, 6, 7, null, 10 },
            PagedView.PageWindow(5, 10));
    }

    [Fact]
    public void Empty_list_has_no_window()
    {
        Assert.Empty(PagedView.PageWindow(1, 0));
    }
}
