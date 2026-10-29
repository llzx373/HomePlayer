namespace HomePlayer.Core;

public enum WatchFilter
{
    All = 0,
    Unwatched = 1,
    InProgress = 2,
    Watched = 3
}

public enum MediaTypeFilter
{
    All = 0,
    Movie = 1,
    Episode = 2,
    Unknown = 3
}

public enum ResolutionFilter
{
    All = 0,
    UltraHd = 1,
    FullHd = 2,
    Hd = 3,
    StandardDefinition = 4,
    Unknown = 5
}

public enum CodecFilter
{
    All = 0,
    H264 = 1,
    H265 = 2,
    Av1 = 3,
    Vp9 = 4,
    Other = 5
}

public enum DurationFilter
{
    All = 0,
    Short = 1,
    Medium = 2,
    Long = 3,
    Unknown = 4
}

public enum PresenceFilter
{
    All = 0,
    Present = 1,
    Missing = 2
}

public sealed record MediaLibraryQuery
{
    public string? LibraryId { get; init; }
    public string? RelativeDir { get; init; }
    public bool Recursive { get; init; } = true;
    public string? Search { get; init; }
    public LibraryFilter Filter { get; init; }
    public WatchFilter Watch { get; init; }
    public MediaTypeFilter MediaType { get; init; }
    public ResolutionFilter Resolution { get; init; }
    public CodecFilter Codec { get; init; }
    public DurationFilter Duration { get; init; }
    public int? Year { get; init; }
    public PresenceFilter Subtitles { get; init; }
    public bool MultipleAudioTracks { get; init; }
    public bool FavoritesOnly { get; init; }

    public static MediaLibraryQuery ForLibrary(string libraryId) => new() { LibraryId = libraryId };
}
