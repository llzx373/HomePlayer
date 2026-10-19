namespace HomePlayer.Core;

public static class MediaItemComparer
{
    public static int Compare(MediaItem left, MediaItem right, LibraryKind kind, bool byDate)
    {
        if (kind == LibraryKind.Raw)
        {
            return byDate
                ? right.MtimeUtcTicks.CompareTo(left.MtimeUtcTicks)
                : string.Compare(left.FileName, right.FileName, StringComparison.OrdinalIgnoreCase);
        }

        if (byDate)
        {
            var year = (right.Year ?? 0).CompareTo(left.Year ?? 0);
            return year != 0
                ? year
                : string.Compare(left.Title ?? left.FileName, right.Title ?? right.FileName, StringComparison.CurrentCulture);
        }

        return string.Compare(left.Title ?? left.FileName, right.Title ?? right.FileName, StringComparison.OrdinalIgnoreCase);
    }
}
