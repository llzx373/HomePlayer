namespace HomePlayer.Core;

public sealed record SubtitleBitmap(int X, int Y, int Width, int Height, byte[] Bgra);

public sealed record SubtitleCue(
    TimeSpan Start,
    TimeSpan End,
    string Text,
    IReadOnlyList<SubtitleBitmap>? Bitmaps = null,
    string? AssEvent = null,
    string? Translation = null)
{
    public IReadOnlyList<SubtitleBitmap> Bitmaps { get; init; } = Bitmaps ?? [];

    public bool HasTranslation => !string.IsNullOrWhiteSpace(Translation);
}

public static class SubtitleCues
{
    public static SubtitleCue? At(IReadOnlyList<SubtitleCue> cues, TimeSpan time)
    {
        for (var i = 0; i < cues.Count; i++)
        {
            var cue = cues[i];
            if (time >= cue.Start && time < cue.End)
                return cue;
        }

        return null;
    }
}
