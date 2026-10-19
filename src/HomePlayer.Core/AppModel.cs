namespace HomePlayer.Core;

public enum LibraryKind
{
    Raw = 0,
    Catalog = 1
}

public enum ItemStatus
{
    Pending = 0,
    Ready = 1,
    Failed = 2
}

public enum MatchStatus
{
    None = 0,
    Matched = 1,
    NeedsReview = 2,
    Failed = 3
}

public enum RepeatMode
{
    Off = 0,
    One = 1,
    All = 2
}

public enum TitleBarMode
{
    Classic = 0,
    Overlay = 1
}

public sealed class AppSettings
{
    public string? TmdbApiKey { get; set; }
    public bool CloseToTray { get; set; } = true;
    public double Volume { get; set; } = 0.8;
    public TitleBarMode TitleBar { get; set; } = TitleBarMode.Classic;
}

public sealed class MediaLibrary
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string RootPath { get; set; } = "";
    public LibraryKind Kind { get; set; }
    public DateTime? LastFullScanUtc { get; set; }
}

public sealed class MediaItem
{
    public string Id { get; set; } = "";
    public string LibraryId { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public long Size { get; set; }
    public long MtimeUtcTicks { get; set; }
    public double? DurationSeconds { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public string? VideoCodec { get; set; }
    public ItemStatus Status { get; set; }
    public string? Error { get; set; }
    public string? SheetPath { get; set; }
    public string[] FramePaths { get; set; } = [];
    public string? MediaType { get; set; }
    public int? TmdbId { get; set; }
    public int? Season { get; set; }
    public int? Episode { get; set; }
    public string? Title { get; set; }
    public string? OriginalTitle { get; set; }
    public int? Year { get; set; }
    public string? Overview { get; set; }
    public double? Rating { get; set; }
    public string? PosterPath { get; set; }
    public MatchStatus MatchStatus { get; set; }

    public string FileName => Path.GetFileName(RelativePath);

    public string FullPath(string root) => Path.Combine(root, RelativePath);
}

public sealed record MediaProbe(
    double DurationSeconds,
    int Width,
    int Height,
    string? VideoCodec,
    string? AudioCodec,
    string? Container);

public sealed class VideoFrame
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int Stride { get; init; }
    public required byte[] Data { get; init; }
    public bool BottomUp { get; init; }
}

public sealed record ParsedName(string Title, int? Year, int? Season, int? Episode, bool IsTv);

public sealed record TmdbHit(
    int Id,
    string MediaType,
    string Title,
    string? OriginalTitle,
    int? Year,
    string? Overview,
    string? PosterPath,
    double? Rating);

public static class AppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HomePlayer");

    public static string Database => Path.Combine(Root, "library.db");
    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string Cache => Path.Combine(Root, "cache");
    public static string Thumbs => Path.Combine(Cache, "thumbs");
    public static string Posters => Path.Combine(Cache, "posters");

    public static void Ensure()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Thumbs);
        Directory.CreateDirectory(Posters);
    }
}

public static class ClockText
{
    public static string Format(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
            value = TimeSpan.Zero;
        return value.TotalHours >= 1
            ? value.ToString(@"h\:mm\:ss")
            : value.ToString(@"mm\:ss");
    }

    public static string FormatSeconds(double? seconds)
    {
        if (seconds is not > 0)
            return "未知时长";
        return Format(TimeSpan.FromSeconds(seconds.Value));
    }
}
