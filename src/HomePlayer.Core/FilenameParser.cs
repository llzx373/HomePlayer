using System.Text.RegularExpressions;

namespace HomePlayer.Core;

public static partial class FilenameParser
{
    private static readonly HashSet<string> Noise = new(StringComparer.OrdinalIgnoreCase)
    {
        "2160p", "1080p", "720p", "576p", "480p", "4320p",
        "bluray", "bdrip", "brrip", "webrip", "webdl", "web-dl", "hdtv", "dvdrip", "hdrip",
        "x264", "x265", "h264", "h265", "hevc", "av1", "xvid",
        "aac", "ac3", "eac3", "dts", "truehd", "flac",
        "hdr", "hdr10", "dv", "sdr", "10bit", "8bit",
        "proper", "repack", "extended", "remux", "internal",
        "amzn", "nf", "dsnp", "atvp", "hulu",
        "ddp", "ddp5", "5.1", "7.1", "2.0"
    };

    public static ParsedName Parse(string path)
    {
        var spaced = Whitespace().Replace(
            Path.GetFileNameWithoutExtension(path).Replace('.', ' ').Replace('_', ' '),
            " ").Trim();

        var tv = SeasonEpisode().Match(spaced);
        if (!tv.Success)
            tv = CrossEpisode().Match(spaced);

        if (tv.Success)
        {
            var title = tv.Groups["title"].Value;
            var year = TakeYear(ref title);
            title = Clean(title);
            return new ParsedName(
                title,
                year,
                int.Parse(tv.Groups["s"].Value),
                int.Parse(tv.Groups["e"].Value),
                true);
        }

        var movie = spaced;
        var movieYear = TakeYear(ref movie);
        return new ParsedName(Clean(movie), movieYear, null, null, false);
    }

    private static int? TakeYear(ref string text)
    {
        var matches = YearToken().Matches(text);
        if (matches.Count == 0)
            return null;

        var last = matches[^1];
        var year = int.Parse(last.Value);
        var max = DateTime.UtcNow.Year + 1;
        if (year < 1900 || year > max)
            return null;

        text = text[..last.Index];
        return year;
    }

    private static string Clean(string raw)
    {
        var withoutGroups = BracketGroup().Replace(raw, " ");
        var kept = new List<string>();
        foreach (var part in withoutGroups.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var token = part.Trim('-', '.', ',');
            if (token.Length == 0 || Noise.Contains(token))
                continue;
            if (Resolution().IsMatch(token))
                continue;
            kept.Add(token);
        }

        var title = string.Join(' ', kept).Trim();
        return title.Length == 0 ? raw.Trim() : title;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"^(?<title>.+?)\s+[Ss](?<s>\d{1,2})\s*[Ee](?<e>\d{1,3})\b")]
    private static partial Regex SeasonEpisode();

    [GeneratedRegex(@"^(?<title>.+?)\s+(?<s>\d{1,2})x(?<e>\d{2,3})\b", RegexOptions.IgnoreCase)]
    private static partial Regex CrossEpisode();

    [GeneratedRegex(@"\b(?:19|20)\d{2}\b")]
    private static partial Regex YearToken();

    [GeneratedRegex(@"[\[\(].*?[\]\)]")]
    private static partial Regex BracketGroup();

    [GeneratedRegex(@"^\d{3,4}p$", RegexOptions.IgnoreCase)]
    private static partial Regex Resolution();
}
