using System.Globalization;
using HomePlayer.Core;

namespace HomePlayer.Library;

public sealed record MediaSeriesEntry(MediaItem Item, MediaLibrary Library, PlaybackState? State);

public sealed class MediaSeriesGroup
{
    public required string Key { get; init; }
    public required string Title { get; init; }
    public required IReadOnlyList<MediaSeriesEntry> Items { get; init; }
    public string? PosterPath => Items.Select(item => item.Item.PosterPath).FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
    public int? Year => Items.Select(item => item.Item.Year).FirstOrDefault(year => year != null);
    public bool IsMovieCollection => Items[0].Item.MediaType != "tv" && Items[0].Item.Season == null && Items[0].Item.Episode == null;
    public IReadOnlyList<int?> Seasons => Items.Select(item => item.Item.Season).Distinct().OrderBy(value => value, NullableSeasonComparer.Instance).ToList();
    public IReadOnlyList<MediaSeriesEntry> OrderedItems => Items.OrderBy(item => item, MediaSeriesEntryComparer.Instance).ToList();
    public IReadOnlyList<MediaSeriesEntry> CanonicalItems => OrderedItems
        .GroupBy(item => item.Item.Episode == null ? item.Item.Id : $"{item.Item.Season}:{item.Item.Episode}")
        .Select(group => group.FirstOrDefault(item => item.State?.Finished == true) ?? group.First())
        .ToList();
    public int EpisodeCount => CanonicalItems.Count;
    public int WatchedCount => CanonicalItems.Count(item => item.State?.Finished == true);
    public MediaSeriesEntry? NextUnwatched => CanonicalItems.FirstOrDefault(item => item.State?.Finished != true);

    public IReadOnlyList<MediaSeriesEntry> InSeason(int? season) =>
        OrderedItems.Where(item => item.Item.Season == season).ToList();
}

public sealed class NullableSeasonComparer : IComparer<int?>
{
    public static NullableSeasonComparer Instance { get; } = new();
    public int Compare(int? left, int? right) => (left ?? int.MaxValue).CompareTo(right ?? int.MaxValue);
}

public sealed class MediaSeriesEntryComparer : IComparer<MediaSeriesEntry>
{
    public static MediaSeriesEntryComparer Instance { get; } = new();

    public int Compare(MediaSeriesEntry? left, MediaSeriesEntry? right)
    {
        if (ReferenceEquals(left, right))
            return 0;
        if (left == null || right == null)
            return left == null ? -1 : 1;
        var season = NullableSeasonComparer.Instance.Compare(left.Item.Season, right.Item.Season);
        if (season != 0)
            return season;
        var episode = (left.Item.Episode ?? int.MaxValue).CompareTo(right.Item.Episode ?? int.MaxValue);
        return episode != 0
            ? episode
            : NaturalStringComparer.Instance.Compare(left.Item.FileName, right.Item.FileName);
    }
}

public sealed class NaturalStringComparer : IComparer<string>
{
    public static NaturalStringComparer Instance { get; } = new();

    public int Compare(string? left, string? right)
    {
        left ??= "";
        right ??= "";
        var leftIndex = 0;
        var rightIndex = 0;
        while (leftIndex < left.Length && rightIndex < right.Length)
        {
            if (char.IsDigit(left[leftIndex]) && char.IsDigit(right[rightIndex]))
            {
                var leftEnd = DigitsEnd(left, leftIndex);
                var rightEnd = DigitsEnd(right, rightIndex);
                var leftNumber = left.AsSpan(leftIndex, leftEnd - leftIndex).TrimStart('0');
                var rightNumber = right.AsSpan(rightIndex, rightEnd - rightIndex).TrimStart('0');
                var length = leftNumber.Length.CompareTo(rightNumber.Length);
                if (length != 0)
                    return length;
                var number = leftNumber.CompareTo(rightNumber, StringComparison.OrdinalIgnoreCase);
                if (number != 0)
                    return number;
                leftIndex = leftEnd;
                rightIndex = rightEnd;
                continue;
            }

            var character = char.ToUpperInvariant(left[leftIndex]).CompareTo(char.ToUpperInvariant(right[rightIndex]));
            if (character != 0)
                return character;
            leftIndex++;
            rightIndex++;
        }

        return (left.Length - leftIndex).CompareTo(right.Length - rightIndex);
    }

    private static int DigitsEnd(string value, int start)
    {
        while (start < value.Length && char.IsDigit(value[start]))
            start++;
        return start;
    }
}

public static class MediaGrouping
{
    public static IReadOnlyList<MediaSeriesGroup> Build(IEnumerable<MediaSeriesEntry> entries)
    {
        return entries
            .Select(entry => (Entry: entry, Key: GroupKey(entry.Item)))
            .Where(item => item.Key.Key != null)
            .GroupBy(item => item.Key.Key!, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var items = group.Select(item => item.Entry).ToList();
                var title = group
                    .Select(item => item.Key.Title)
                    .Where(title => !string.IsNullOrWhiteSpace(title))
                    .GroupBy(title => title, StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(title => title.Count())
                    .ThenBy(title => title.Key, NaturalStringComparer.Instance)
                    .First().Key;
                return new MediaSeriesGroup
                {
                    Key = group.Key,
                    Title = title,
                    Items = items
                };
            })
            .OrderBy(group => group.Title, NaturalStringComparer.Instance)
            .ToList();
    }

    private static (string? Key, string Title) GroupKey(MediaItem item)
    {
        var parsed = FilenameParser.Parse(item.FileName);
        var isTv = item.MediaType == "tv" || item.Season != null || item.Episode != null;
        if (isTv)
        {
            var title = !string.IsNullOrWhiteSpace(item.Title) ? item.Title! : parsed.Title;
            var key = item.TmdbId is int id
                ? "tv:id:" + id.ToString(CultureInfo.InvariantCulture)
                : "tv:title:" + Normalize(title);
            return (key, title);
        }

        if (!string.IsNullOrWhiteSpace(item.CollectionName))
        {
            var title = item.CollectionName.Trim();
            return ("movies:" + Normalize(title), title);
        }

        return (null, "");
    }

    private static string Normalize(string value) =>
        string.Join(' ', value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToUpperInvariant();
}
