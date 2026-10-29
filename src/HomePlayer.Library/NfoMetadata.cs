using System.Xml.Linq;
using HomePlayer.Core;

namespace HomePlayer.Library;

public static class NfoMetadata
{
    public static bool Apply(MediaItem item, string videoPath)
    {
        var nfo = FindNfo(videoPath);
        if (nfo == null)
            return false;
        try
        {
            var document = XDocument.Load(nfo, LoadOptions.PreserveWhitespace);
            var root = document.Root;
            if (root == null)
                return false;
            item.Title = Value(root, "title") ?? item.Title;
            item.OriginalTitle = Value(root, "originaltitle") ?? item.OriginalTitle;
            item.Overview = Value(root, "plot") ?? Value(root, "outline") ?? item.Overview;
            item.Year = Integer(root, "year") ?? item.Year;
            item.Season = Integer(root, "season") ?? item.Season;
            item.Episode = Integer(root, "episode") ?? item.Episode;
            item.CollectionName = root.Descendants().FirstOrDefault(node => node.Name.LocalName == "set")?
                .Elements().FirstOrDefault(node => node.Name.LocalName == "name")?.Value.Trim() ?? item.CollectionName;
            if (root.Name.LocalName == "tvshow" || item.Season != null || item.Episode != null)
                item.MediaType = "tv";
            else if (root.Name.LocalName == "movie")
                item.MediaType = "movie";
            item.PosterPath = FindPoster(videoPath) ?? item.PosterPath;
            item.MatchStatus = MatchStatus.Matched;
            item.Status = ItemStatus.Ready;
            item.Error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return false;
        }
    }

    private static string? FindNfo(string videoPath)
    {
        var directory = Path.GetDirectoryName(videoPath);
        if (directory == null)
            return null;
        var candidates = new[]
        {
            Path.ChangeExtension(videoPath, ".nfo"),
            Path.Combine(directory, "movie.nfo"),
            Path.Combine(directory, "tvshow.nfo")
        };
        var parent = Directory.GetParent(directory)?.FullName;
        if (parent != null)
            candidates = [.. candidates, Path.Combine(parent, "tvshow.nfo")];
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? FindPoster(string videoPath)
    {
        var directory = Path.GetDirectoryName(videoPath);
        if (directory == null)
            return null;
        var stem = Path.GetFileNameWithoutExtension(videoPath);
        var candidates = new[]
        {
            Path.Combine(directory, stem + "-poster.jpg"),
            Path.Combine(directory, "poster.jpg"),
            Path.Combine(directory, "folder.jpg"),
            Path.Combine(directory, stem + "-poster.png"),
            Path.Combine(directory, "poster.png"),
            Path.Combine(directory, "folder.png")
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? Value(XElement root, string name)
    {
        var value = root.Elements().FirstOrDefault(node => node.Name.LocalName == name)?.Value.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static int? Integer(XElement root, string name) =>
        int.TryParse(Value(root, name), out var value) ? value : null;
}
