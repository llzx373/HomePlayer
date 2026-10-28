namespace HomePlayer.Core;

public static class MediaItemComparer
{
    public static int Compare(MediaItem left, MediaItem right, SortMode mode)
    {
        var primary = mode switch
        {
            SortMode.FileName => string.Compare(left.FileName, right.FileName, StringComparison.OrdinalIgnoreCase),
            SortMode.ModifiedDesc => right.MtimeUtcTicks.CompareTo(left.MtimeUtcTicks),
            SortMode.Title => string.Compare(Title(left), Title(right), StringComparison.OrdinalIgnoreCase),
            SortMode.YearDesc => CompareYear(left, right),
            SortMode.AddedDesc => right.AddedUtcTicks.CompareTo(left.AddedUtcTicks),
            _ => 0
        };
        return primary != 0
            ? primary
            : string.Compare(left.RelativePath, right.RelativePath, StringComparison.OrdinalIgnoreCase);
    }

    public static string Title(MediaItem item) =>
        string.IsNullOrWhiteSpace(item.Title) ? item.FileName : item.Title;

    private static int CompareYear(MediaItem left, MediaItem right)
    {
        var year = (right.Year ?? 0).CompareTo(left.Year ?? 0);
        return year != 0
            ? year
            : string.Compare(MediaItemComparer.Title(left), MediaItemComparer.Title(right), StringComparison.CurrentCulture);
    }
}
