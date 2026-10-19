using System.Text.RegularExpressions;

namespace HomePlayer.Core;

public static partial class SubtitleFiles
{
    public static readonly string[] Extensions = [".srt", ".ass", ".ssa", ".vtt"];

    public static IReadOnlyList<string> Find(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return [];
        string? dir;
        try
        {
            dir = Path.GetDirectoryName(path);
        }
        catch (ArgumentException)
        {
            return [];
        }

        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            return [];

        var found = new List<string>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                if (IsSidecar(path, file))
                    found.Add(Path.GetFullPath(file));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        found.Sort(StringComparer.OrdinalIgnoreCase);
        return found;
    }

    public static bool IsSidecar(string videoPath, string subtitlePath)
    {
        if (string.IsNullOrWhiteSpace(videoPath) || string.IsNullOrWhiteSpace(subtitlePath))
            return false;
        var extension = Path.GetExtension(subtitlePath);
        if (!Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            return false;
        var stem = Path.GetFileNameWithoutExtension(videoPath);
        var name = Path.GetFileNameWithoutExtension(subtitlePath);
        if (string.IsNullOrEmpty(stem) || string.IsNullOrEmpty(name))
            return false;
        if (name.Equals(stem, StringComparison.OrdinalIgnoreCase))
            return true;
        var prefix = stem + ".";
        if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;
        return LanguageTag().IsMatch(name[prefix.Length..]);
    }

    [GeneratedRegex(@"^[A-Za-z]{2,3}(-[A-Za-z0-9]{1,8})*$", RegexOptions.CultureInvariant)]
    private static partial Regex LanguageTag();
}
