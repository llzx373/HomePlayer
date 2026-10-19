namespace HomePlayer.Core;

public enum MediaTrackKind
{
    Audio,
    Subtitle
}

public sealed record MediaTrack(int Index, MediaTrackKind Kind, string Title, string? Language, bool Selected);
