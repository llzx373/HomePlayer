using System.Text;
using HomePlayer.Core;

namespace HomePlayer.AI;

public static class SubtitleCueFormatter
{
    public static IReadOnlyList<SubtitleCue> Build(string text, TimeSpan start, TimeSpan end)
    {
        text = string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (text.Length == 0 || end <= start)
            return [];
        var cjk = IsCjk(text);
        var maximum = cjk ? 32 : 76;
        var pieces = SplitLongSentences(Sentences(text), maximum, cjk);
        var cues = Pack(pieces, maximum);
        var result = new List<SubtitleCue>(cues.Count);
        var totalCharacters = Math.Max(1, cues.Sum(cue => SignificantLength(cue)));
        var used = 0;
        var span = end - start;
        for (var index = 0; index < cues.Count; index++)
        {
            var cueStart = index == 0 ? start : result[index - 1].End;
            used += SignificantLength(cues[index]);
            var cueEnd = index == cues.Count - 1
                ? end
                : start + TimeSpan.FromSeconds(span.TotalSeconds * used / totalCharacters);
            result.Add(new SubtitleCue(cueStart, cueEnd, Wrap(cues[index], cjk)));
        }
        return result;
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

    private static IEnumerable<string> SplitLongSentences(IEnumerable<string> sentences, int maximum, bool cjk)
    {
        foreach (var sentence in sentences)
        {
            var remaining = sentence;
            while (remaining.Length > maximum)
            {
                var split = SplitPosition(remaining, maximum, cjk);
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

    private static int SplitPosition(string text, int maximum, bool cjk)
    {
        if (!cjk)
        {
            var before = text.LastIndexOf(' ', maximum - 1, maximum);
            if (before > 0)
                return before;
            var after = text.IndexOf(' ', maximum);
            if (after > 0)
                return after;
        }
        return maximum;
    }

    private static string Wrap(string text, bool cjk)
    {
        var lineMaximum = cjk ? 16 : 42;
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
