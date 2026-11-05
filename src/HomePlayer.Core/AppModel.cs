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

public enum AspectMode
{
    Fit = 0,
    Fill = 1,
    Stretch = 2,
    Ratio16x9 = 3,
    Ratio4x3 = 4
}

public enum TitleBarMode
{
    Classic = 0,
    Overlay = 1
}

public enum SubtitleVerticalPosition
{
    Top = 0,
    Center = 1,
    Bottom = 2
}

public enum SubtitleTextColor
{
    White = 0,
    Yellow = 1,
    LightGreen = 2
}

public enum SortMode
{
    FileName = 0,
    ModifiedDesc = 1,
    Title = 2,
    YearDesc = 3,
    AddedDesc = 4,
    Episode = 5
}

public enum LibraryFilter
{
    All = 0,
    Failed = 1,
    NeedsReview = 2,
    Unwatched = 3
}

public enum LibraryLayout
{
    Cards = 0,
    CompactCards = 1,
    List = 2
}

public enum LibraryCardSize
{
    Small = 0,
    Medium = 1,
    Large = 2
}

public enum LibraryInfoDisplay
{
    Title = 0,
    Path = 1,
    Technical = 2
}

public sealed class CodecPreference
{
    public string Id { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

public sealed partial class AppSettings
{
    public string? TmdbApiKey { get; set; }
    public bool CloseToTray { get; set; } = true;
    public double Volume { get; set; } = 0.8;
    public TitleBarMode TitleBar { get; set; } = TitleBarMode.Classic;
    public bool FitWindow { get; set; }
    public bool AlwaysOnTop { get; set; }
    public bool RecordPlaybackHistory { get; set; } = true;
    public bool AutoPlayNextEpisode { get; set; } = true;
    public double SubtitleFontSize { get; set; } = 28;
    public SubtitleTextColor SubtitleColor { get; set; } = SubtitleTextColor.White;
    public SubtitleVerticalPosition SubtitlePosition { get; set; } = SubtitleVerticalPosition.Bottom;
    public double SubtitleBackgroundOpacity { get; set; } = 0.6;
    public string? AudioOutputId { get; set; }
    public List<CodecPreference> SplitterOrder { get; set; } = [];
    public List<CodecPreference> VideoDecoderOrder { get; set; } = [];
    public List<CodecPreference> AudioDecoderOrder { get; set; } = [];
    public Dictionary<string, string> Hotkeys { get; set; } = [];
    public LibraryViewState LibraryView { get; set; } = new();
}

public sealed class LibraryViewState
{
    public string? LibraryId { get; set; }
    public string? FolderPath { get; set; }
    public bool Recursive { get; set; } = true;
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = PagedView.DefaultPageSize;
    public string SortMode { get; set; } = "";
    public LibraryLayout Layout { get; set; } = LibraryLayout.Cards;
    public LibraryCardSize CardSize { get; set; } = LibraryCardSize.Medium;
    public LibraryInfoDisplay InfoDisplay { get; set; } = LibraryInfoDisplay.Title;
    public List<string> ExpandedFolders { get; set; } = [];
}

public sealed class MediaLibrary
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string RootPath { get; set; } = "";
    public LibraryKind Kind { get; set; }
    public DateTime? LastFullScanUtc { get; set; }
}

public sealed class MediaFolder
{
    public string LibraryId { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public string? ParentPath { get; set; }
    public string Name { get; set; } = "";
    public int DirectCount { get; set; }
    public int RecursiveCount { get; set; }
    public string? CoverPath { get; set; }
    public string? CoverStamp { get; set; }
    public bool HasChildren { get; set; }
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
    public string? AudioCodec { get; set; }
    public int AudioStreamCount { get; set; }
    public int SubtitleStreamCount { get; set; }
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
    public string? CollectionName { get; set; }
    public bool MetadataEdited { get; set; }
    public MatchStatus MatchStatus { get; set; }
    public long AddedUtcTicks { get; set; }

    public string RelativeDir => RelativePaths.Split(RelativePath).Directory;

    public string FileName => RelativePaths.Split(RelativePath).FileName;

    public string FullPath(string root) => Path.Combine(root, RelativePath.Replace('/', Path.DirectorySeparatorChar));
}

public sealed record MediaProbe(
    double DurationSeconds,
    int Width,
    int Height,
    string? VideoCodec,
    string? AudioCodec,
    string? Container)
{
    public int AudioStreamCount { get; init; }
    public int SubtitleStreamCount { get; init; }
}

public sealed class VideoFrame : CpuFrameLease
{
    public VideoFrame()
    {
        PixelFormat = VideoPixelFormat.Bgra32;
        ZeroCopyLevel = ZeroCopyLevel.Z1;
    }

    public required int Stride { get; init; }
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
    public static string DefaultRoot { get; } = ResolveDefaultRoot();
    public static string Root { get; private set; } = DefaultRoot;
    public static string? MigrationWarning { get; private set; }
    public static string SettingsFile => Path.Combine(DefaultRoot, "settings.json");

    private static string ResolveDefaultRoot()
    {
        var custom = Environment.GetEnvironmentVariable("HOMEPLAYER_DATA_DIR");
        return !string.IsNullOrWhiteSpace(custom)
            ? Path.GetFullPath(custom)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HomePlayer");
    }

    public static void Initialize()
    {
        Root = ReadConfiguredDataDirectory() ?? DefaultRoot;
    }

    public static void MigratePendingData()
    {
        MigrationWarning = null;
        var store = new SettingsStore(SettingsFile);
        AppSettings settings;
        try
        {
            settings = store.Load().Settings;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        var source = settings.PendingDataMigrationSource;
        if (string.IsNullOrWhiteSpace(source))
            return;
        try
        {
            DataDirectoryMigration.MoveData(source, Root, Path.GetFileName(SettingsFile));
            settings.PendingDataMigrationSource = "";
            settings.PreviousDataDirectory = Path.GetFullPath(source);
            store.Save(settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Root = Path.GetFullPath(source);
            MigrationWarning = "数据目录迁移未完成，继续使用 " + Root + "：" + ex.Message;
        }
    }

    private static string? ReadConfiguredDataDirectory()
    {
        try
        {
            if (!File.Exists(SettingsFile))
                return null;
            var settings = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsFile));
            var directory = settings?.DataDirectory;
            return string.IsNullOrWhiteSpace(directory) ? null : Path.GetFullPath(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException)
        {
            return null;
        }
    }

    public static string Database => Path.Combine(Root, "library.db");
    public static string Cache => Path.Combine(Root, "cache");
    public static string Thumbs => Path.Combine(Cache, "thumbs");
    public static string Posters => Path.Combine(Cache, "posters");
    public static string FolderThumbs => Path.Combine(Cache, "folder-thumbs");
    public static string Crashes => Path.Combine(Root, "crashes");
    public static string AiModels => Path.Combine(Root, "ai-models");
    public static string ResolveAiModels(string? customDirectory) =>
        string.IsNullOrWhiteSpace(customDirectory) ? AiModels : Path.GetFullPath(customDirectory);
    public static string AiSubtitles => Path.Combine(Root, "ai-subtitles");
    public static string AiTemp => Path.Combine(Cache, "ai-temp");
    public static string Voices => Path.Combine(Root, "voices");
    public static string DubbingCache => Path.Combine(Cache, "dubbing");
    public static string SessionLock => Path.Combine(Root, "session.lock");
    public static string LegacyCrashLog => Path.Combine(Root, "crash.log");

    public static void Ensure()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Thumbs);
        Directory.CreateDirectory(Posters);
        Directory.CreateDirectory(FolderThumbs);
        Directory.CreateDirectory(Crashes);
        Directory.CreateDirectory(AiTemp);
        Directory.CreateDirectory(Voices);
        Directory.CreateDirectory(DubbingCache);
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
