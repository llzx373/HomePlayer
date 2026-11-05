namespace HomePlayer.Core;

public enum SubtitleDisplayMode
{
    Both = 0,
    Original = 1,
    Translation = 2
}

public static class SubtitleDisplay
{
    public static (string Primary, string? Secondary) Split(SubtitleCue? cue, SubtitleDisplayMode mode)
    {
        if (cue == null)
            return ("", null);
        var original = cue.Text;
        if (!cue.HasTranslation)
            return (original, null);
        var translation = cue.Translation!.Trim();
        return mode switch
        {
            SubtitleDisplayMode.Original => (original, null),
            SubtitleDisplayMode.Translation => (translation, null),
            _ => (original, translation)
        };
    }
}
