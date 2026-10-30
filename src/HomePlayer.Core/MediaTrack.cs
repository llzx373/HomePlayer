namespace HomePlayer.Core;

public enum MediaTrackKind
{
    Audio,
    Subtitle
}

[Flags]
public enum MediaTrackDisposition
{
    None = 0,
    Default = 1,
    Forced = 2,
    Commentary = 4,
    HearingImpaired = 8,
    VisualImpaired = 16,
    Descriptions = 32,
    Lyrics = 64
}

public sealed record MediaTrack(int Index, MediaTrackKind Kind, string Title, string? Language, bool Selected)
{
    public string? StableId { get; init; }
    public MediaTrackDisposition Disposition { get; init; }
}

public static class MediaTrackIdentity
{
    public static string Create(MediaTrackKind kind, long? containerUid, string title, string? language, MediaTrackDisposition disposition)
    {
        var prefix = kind == MediaTrackKind.Audio ? "audio" : "subtitle";
        if (containerUid is >= 0)
            return $"{prefix}:uid:{containerUid}";
        return $"{prefix}:meta:{language?.Trim() ?? ""}|{title.Trim()}|{(int)disposition}";
    }
}
