using System.Globalization;
using System.Text.RegularExpressions;

namespace HomePlayer.Core;

public sealed record SiblingPlaylist(IReadOnlyList<string> Paths, int Index);

public static partial class SiblingEpisodes
{
    private sealed record FileEntry(
        string Path,
        string FileName,
        StrongIdentity? Strong,
        IReadOnlyList<WeakIdentity> Weak);

    private sealed record StrongIdentity(string Key, int? Season, int? Episode);

    private sealed record WeakIdentity(string Key, string Suffix, int? Season, int Episode, int Slot);

    private sealed record Candidate(string Key, string? Suffix, int Slot);

    private sealed record MatchedFile(
        FileEntry Entry,
        string Key,
        int? Season,
        int? Episode,
        string Suffix,
        int Slot);

    public static SiblingPlaylist Find(string anchorPath)
    {
        string anchor;
        try
        {
            anchor = Path.GetFullPath(anchorPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return new([anchorPath], 0);
        }

        var directory = Path.GetDirectoryName(anchor);
        if (directory == null)
            return new([anchor], 0);

        try
        {
            return Build(anchor, Directory.EnumerateFiles(directory));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new([anchor], 0);
        }
    }

    public static SiblingPlaylist Build(string anchorPath, IEnumerable<string> folderFiles)
    {
        var anchor = Path.GetFullPath(anchorPath);
        var directory = Path.GetDirectoryName(anchor);
        var files = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in folderFiles)
        {
            string full;
            try
            {
                full = Path.GetFullPath(path);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                continue;
            }

            if (!VideoFiles.IsVideo(full))
                continue;
            if (directory != null && !string.Equals(Path.GetDirectoryName(full), directory, StringComparison.OrdinalIgnoreCase))
                continue;
            if (seen.Add(full))
                files.Add(full);
        }

        if (seen.Add(anchor))
            files.Add(anchor);

        var entries = files
            .Select(path => new FileEntry(path, Path.GetFileName(path), StrongIdentityFor(path), WeakIdentities(path)))
            .ToList();
        var anchorEntry = entries.FirstOrDefault(entry => PathEquals(entry.Path, anchor));
        if (anchorEntry == null)
            return new([anchor], 0);

        var knownSeasons = entries
            .Select(entry => entry.Strong?.Season)
            .Where(season => season != null)
            .Distinct()
            .ToList();
        var fallbackSeason = anchorEntry.Strong?.Season
            ?? FilenameParser.SeasonFromDirectory(anchor)
            ?? (knownSeasons.Count == 1 ? knownSeasons[0] : null);
        var candidates = new List<Candidate>();
        if (anchorEntry.Strong is { } strong)
            candidates.Add(new(strong.Key, null, -1));
        else
            candidates.AddRange(anchorEntry.Weak.Select(weak => new Candidate(weak.Key, weak.Suffix, weak.Slot)));

        Candidate? bestCandidate = null;
        List<MatchedFile>? bestMatches = null;
        var bestScore = (NoDuplicatePairs: false, Matched: 0, SameSuffix: 0, DistinctPairs: 0, KeyLength: 0, Slot: int.MinValue);

        foreach (var candidate in candidates)
        {
            var matches = new List<MatchedFile>();
            foreach (var entry in entries)
            {
                if (TryMatch(entry, candidate, fallbackSeason) is { } match)
                    matches.Add(match);
            }

            if (matches.Count < 2)
                continue;

            var pairs = matches
                .Where(match => match.Episode is not null)
                .Select(match => (match.Season, Episode: match.Episode!.Value))
                .ToList();
            var distinctPairs = pairs.Distinct().Count();
            if (distinctPairs < 2)
                continue;

            var score = (
                NoDuplicatePairs: pairs.Count == distinctPairs,
                Matched: matches.Count,
                SameSuffix: candidate.Suffix is { Length: > 0 }
                    ? matches.Count(match => string.Equals(match.Suffix, candidate.Suffix, StringComparison.OrdinalIgnoreCase))
                    : 0,
                DistinctPairs: distinctPairs,
                KeyLength: candidate.Key.Length,
                Slot: candidate.Slot);
            if (bestCandidate == null || score.CompareTo(bestScore) > 0)
            {
                bestCandidate = candidate;
                bestMatches = matches;
                bestScore = score;
            }
        }

        if (bestCandidate == null || bestMatches == null)
            return new([anchor], 0);

        var anchorMatch = bestMatches.FirstOrDefault(match => PathEquals(match.Entry.Path, anchor));
        if (anchorMatch == null)
            return new([anchor], 0);

        var ordered = new List<MatchedFile> { anchorMatch };
        var usedEpisodes = new HashSet<(int? Season, int Episode)>();
        if (anchorMatch.Episode is int anchorEpisode)
            usedEpisodes.Add((anchorMatch.Season, anchorEpisode));

        foreach (var match in bestMatches
                     .Where(match => !PathEquals(match.Entry.Path, anchor))
                     .OrderBy(match => match.Entry.FileName, NaturalStringComparer.Instance))
        {
            if (match.Episode is int episode && !usedEpisodes.Add((match.Season, episode)))
                continue;
            ordered.Add(match);
        }

        ordered.Sort(CompareMatches);
        if (ordered.Count < 2)
            return new([anchor], 0);

        var index = ordered.FindIndex(match => PathEquals(match.Entry.Path, anchor));
        return new(ordered.Select(match => match.Entry.Path).ToList(), index < 0 ? 0 : index);
    }

    private static StrongIdentity? StrongIdentityFor(string path)
    {
        var parsed = FilenameParser.Parse(path);
        if (!parsed.IsTv)
            return null;
        var key = Normalize(parsed.Title);
        return IsValidKey(key) ? new StrongIdentity(key, parsed.Season, parsed.Episode) : null;
    }

    private static IReadOnlyList<WeakIdentity> WeakIdentities(string path)
    {
        var stem = NormalizeStem(Path.GetFileNameWithoutExtension(path));
        var identities = new List<WeakIdentity>();
        foreach (Match slot in NumberSlot().Matches(stem))
        {
            if (!int.TryParse(slot.Groups["e"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var episode))
                continue;
            if (!IsPlausibleEpisode(episode))
                continue;

            var prefix = stem[..slot.Index];
            var suffix = stem[(slot.Index + slot.Length)..];
            var key = Normalize(FilenameParser.CleanTitle(prefix, fallbackToRaw: false));
            if (!IsValidKey(key))
                key = Normalize(FilenameParser.CleanTitle(suffix, fallbackToRaw: false));
            if (!IsValidKey(key))
                continue;

            identities.Add(new WeakIdentity(
                key,
                Normalize(FilenameParser.CleanTitle(suffix, fallbackToRaw: false)),
                FilenameParser.SeasonFromDirectory(path),
                episode,
                slot.Index));
        }

        return identities;
    }

    private static MatchedFile? TryMatch(FileEntry entry, Candidate candidate, int? fallbackSeason)
    {
        if (entry.Strong is { } strong && string.Equals(strong.Key, candidate.Key, StringComparison.OrdinalIgnoreCase))
            return new MatchedFile(entry, candidate.Key, strong.Season ?? fallbackSeason, strong.Episode, "", -1);

        var options = entry.Weak
            .Where(weak => string.Equals(weak.Key, candidate.Key, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (options.Count == 0)
            return null;

        var chosen = candidate.Suffix is { Length: > 0 }
            ? options.LastOrDefault(weak => string.Equals(weak.Suffix, candidate.Suffix, StringComparison.OrdinalIgnoreCase)) ?? options[^1]
            : options[^1];
        return new MatchedFile(
            entry,
            candidate.Key,
            chosen.Season ?? fallbackSeason,
            chosen.Episode,
            chosen.Suffix,
            chosen.Slot);
    }

    private static bool IsPlausibleEpisode(int value)
    {
        if (value < 0 || value > 9999)
            return false;
        var maxYear = DateTime.UtcNow.Year + 1;
        return value < 1900 || value > maxYear;
    }

    private static bool IsValidKey(string key) => key.Length >= 2 && key.Any(char.IsLetter);

    private static string NormalizeStem(string stem)
    {
        var value = stem.Replace('_', ' ');
        value = DottedCodec().Replace(value, " ");
        value = DottedTag().Replace(value, " ");
        return value.Replace('.', ' ');
    }

    private static string Normalize(string value) =>
        string.Join(' ', value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToUpperInvariant();

    private static bool PathEquals(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static int CompareMatches(MatchedFile left, MatchedFile right)
    {
        var season = (left.Season ?? int.MaxValue).CompareTo(right.Season ?? int.MaxValue);
        if (season != 0)
            return season;
        var episode = (left.Episode ?? int.MaxValue).CompareTo(right.Episode ?? int.MaxValue);
        return episode != 0
            ? episode
            : NaturalStringComparer.Instance.Compare(left.Entry.FileName, right.Entry.FileName);
    }

    [GeneratedRegex(@"(?<![\p{L}\p{N}])(?:[HhXx]|AVC|HEVC)\.?26[45](?![\p{L}\p{N}])", RegexOptions.IgnoreCase)]
    private static partial Regex DottedCodec();

    [GeneratedRegex(@"(?<![\p{L}\p{N}])(?:[257]\.1|2\.0|1\.0|5\.0|\d{1,2}\.bit)(?![\p{L}\p{N}])", RegexOptions.IgnoreCase)]
    private static partial Regex DottedTag();

    [GeneratedRegex(@"(?<![\p{L}\p{N}])(?<e>[0-9]{1,4})(?:[vV][0-9]+)?(?![\p{L}\p{N}])")]
    private static partial Regex NumberSlot();
}
