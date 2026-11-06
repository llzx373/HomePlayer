using System.Text;
using HomePlayer.Core;

namespace HomePlayer.AI;

public static class SubtitleCueFormatter
{
    private static readonly TimeSpan MaximumSnapDistance = TimeSpan.FromSeconds(3);

    public static IReadOnlyList<SubtitleCue> Build(
        string text,
        TimeSpan start,
        TimeSpan end,
        IReadOnlyList<(TimeSpan Start, TimeSpan End)>? pauses = null)
    {
        text = string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (text.Length == 0 || end <= start)
            return [];
        var cjk = IsCjk(text);
        var maximum = cjk ? 40 : 76;
        var pieces = SplitLongSentences(Sentences(text), maximum);
        var cues = Pack(pieces, maximum);
        var totalCharacters = Math.Max(1, cues.Sum(cue => SignificantLength(cue)));
        var used = 0;
        var span = end - start;
        var splits = new (TimeSpan PreviousEnd, TimeSpan NextStart)[Math.Max(0, cues.Count - 1)];
        for (var index = 0; index < splits.Length; index++)
        {
            used += SignificantLength(cues[index]);
            var at = start + TimeSpan.FromSeconds(span.TotalSeconds * used / totalCharacters);
            splits[index] = (at, at);
        }
        if (pauses is { Count: > 0 } && splits.Length > 0)
            SnapSplits(splits, pauses, start, end);
        var result = new List<SubtitleCue>(cues.Count);
        for (var index = 0; index < cues.Count; index++)
        {
            var cueStart = index == 0 ? start : splits[index - 1].NextStart;
            var cueEnd = index == cues.Count - 1 ? end : splits[index].PreviousEnd;
            result.Add(new SubtitleCue(cueStart, cueEnd, Wrap(cues[index], cjk)));
        }
        return result;
    }

    private static void SnapSplits(
        (TimeSpan PreviousEnd, TimeSpan NextStart)[] splits,
        IReadOnlyList<(TimeSpan Start, TimeSpan End)> pauses,
        TimeSpan start,
        TimeSpan end)
    {
        var last = -1;
        foreach (var pause in pauses)
        {
            if (pause.Start < start || pause.End > end || pause.End <= pause.Start)
                continue;
            var center = pause.Start + TimeSpan.FromTicks((pause.End - pause.Start).Ticks / 2);
            var best = -1;
            var distance = TimeSpan.MaxValue;
            for (var index = last + 1; index < splits.Length; index++)
            {
                var candidate = (splits[index].PreviousEnd - center).Duration();
                if (candidate >= distance)
                    continue;
                best = index;
                distance = candidate;
            }
            if (best < 0 || distance > MaximumSnapDistance)
                continue;
            if (best > 0 && splits[best - 1].NextStart >= pause.Start)
                continue;
            if (best + 1 < splits.Length && splits[best + 1].PreviousEnd <= pause.End)
                continue;
            splits[best] = (pause.Start, pause.End);
            last = best;
        }
    }

    private static List<string> Sentences(string text)
    {
        var result = new List<string>();
        var start = 0;
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            var punctuation = character is '。' or '！' or '？' or '；' or '!' or '?' or ';'
                || character == '.' && (index + 1 == text.Length || !char.IsDigit(text[index + 1]));
            if (!punctuation || index + 1 >= text.Length)
                continue;
            var next = text[index + 1];
            if (char.IsWhiteSpace(next) || IsCjkChar(next) || next is '"' or '\'' or ')' or '”')
            {
                Add(result, text[start..(index + 1)]);
                start = index + 1;
            }
        }
        Add(result, text[start..]);
        return result;

        static void Add(List<string> target, string value)
        {
            value = value.Trim();
            if (value.Length > 0)
                target.Add(value);
        }
    }

    private static IEnumerable<string> SplitLongSentences(IEnumerable<string> sentences, int maximum)
    {
        foreach (var sentence in sentences)
        {
            var remaining = sentence;
            while (remaining.Length > maximum)
            {
                var split = SplitPosition(remaining, maximum);
                yield return remaining[..split].Trim();
                remaining = remaining[split..].TrimStart();
            }
            if (remaining.Length > 0)
                yield return remaining;
        }
    }

    private static List<string> Pack(IEnumerable<string> pieces, int maximum)
    {
        var result = new List<string>();
        var current = "";
        foreach (var piece in pieces)
        {
            var combined = current.Length == 0 ? piece : current + " " + piece;
            if (current.Length > 0 && combined.Length > maximum)
            {
                result.Add(current);
                current = piece;
            }
            else
                current = combined;
        }
        if (current.Length > 0)
            result.Add(current);
        return result;
    }

    private static int SplitPosition(string text, int maximum)
    {
        var before = text.LastIndexOf(' ', maximum - 1, maximum);
        if (before > 0)
            return before;
        var after = text.IndexOf(' ', maximum);
        if (after > 0)
            return after;
        return maximum;
    }

    private static string Wrap(string text, bool cjk)
    {
        var lineMaximum = cjk ? 20 : 42;
        if (text.Length <= lineMaximum)
            return text;
        var midpoint = text.Length / 2;
        if (cjk)
            return text[..midpoint].TrimEnd() + "\n" + text[midpoint..].TrimStart();
        var before = text.LastIndexOf(' ', midpoint);
        var after = text.IndexOf(' ', midpoint);
        var split = before < 0 ? after : after < 0 ? before : midpoint - before <= after - midpoint ? before : after;
        if (split <= 0)
            return text;
        return text[..split].TrimEnd() + "\n" + text[split..].TrimStart();
    }

    private static int SignificantLength(string text) => text.Count(character => !char.IsWhiteSpace(character));

    private static bool IsCjk(string text)
    {
        var count = text.Count(IsCjkChar);
        return count * 3 >= SignificantLength(text);
    }

    private static bool IsCjkChar(char character) =>
        character is >= '\u3000' and <= '\u9fff' or >= '\uac00' and <= '\ud7af';
}
