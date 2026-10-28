namespace HomePlayer.Core;

/// <summary>
/// 页码分页的纯逻辑：总页数、页码裁剪和页码窗口。
/// </summary>
public static class PagedView
{
    public const int DefaultPageSize = 60;

    public static readonly int[] PageSizes = [30, 60, 100, 200];

    public static int TotalPages(int total, int pageSize)
    {
        if (pageSize < 1)
            pageSize = DefaultPageSize;
        if (total <= 0)
            return 1;
        return (total + pageSize - 1) / pageSize;
    }

    public static int ClampPage(int page, int total, int pageSize)
    {
        var pages = TotalPages(total, pageSize);
        if (page < 1)
            return 1;
        return page > pages ? pages : page;
    }

    /// <summary>
    /// 返回要显示的页码，<c>null</c> 表示省略号。只显示 current ± 2、首页和末页，
    /// 相邻页码之间只缺一个数字时直接连上，不画省略号。
    /// </summary>
    public static IReadOnlyList<int?> PageWindow(int current, int total)
    {
        if (total <= 0)
            return [];
        if (current < 1)
            current = 1;
        else if (current > total)
            current = total;

        if (total <= 9)
            return Enumerable.Range(1, total).Select(page => (int?)page).ToList();

        int start;
        int end;
        if (current <= 5)
        {
            start = 1;
            end = Math.Min(total, 7);
        }
        else if (current >= total - 4)
        {
            start = Math.Max(1, total - 6);
            end = total;
        }
        else
        {
            start = current - 2;
            end = current + 2;
        }

        var pages = new List<int?>();
        if (start > 1)
        {
            pages.Add(1);
            if (start == 3)
                pages.Add(2);
            else if (start > 3)
                pages.Add(null);
        }

        for (var page = start; page <= end; page++)
            pages.Add(page);

        if (end < total)
        {
            if (end == total - 2)
                pages.Add(total - 1);
            else if (end < total - 1)
                pages.Add(null);
            pages.Add(total);
        }

        return pages;
    }
}
