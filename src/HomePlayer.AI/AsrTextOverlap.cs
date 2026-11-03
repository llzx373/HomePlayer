using System.Text;

namespace HomePlayer.AI;

public static class AsrTextOverlap
{
    public static string Remove(string? previous, string? current)
    {
        if (string.IsNullOrWhiteSpace(current))
            return "";
        if (string.IsNullOrWhiteSpace(previous))
            return current.Trim();
        var before = Normalized(previous);
        var after = Normalized(current, out var rawIndices);
        var maximum = Math.Min(before.Length, after.Length);
        for (var length = maximum; length >= 2; length--)
        {
            if (!before.EndsWith(after.AsSpan(0, length), StringComparison.OrdinalIgnoreCase))
                continue;
            return current[(rawIndices[length - 1] + 1)..]
                .TrimStart().TrimStart('.', '。', ',', '，', ';', '；', '?', '？', '!', '！').TrimStart();
        }
        return current.Trim();
    }

    private static string Normalized(string text) => Normalized(text, out _);

    private static string Normalized(string text, out List<int> rawIndices)
    {
        var builder = new StringBuilder(text.Length);
        rawIndices = [];
        for (var index = 0; index < text.Length; index++)
        {
            if (!char.IsLetterOrDigit(text[index]))
                continue;
            builder.Append(char.ToLowerInvariant(text[index]));
            rawIndices.Add(index);
        }
        return builder.ToString();
    }
}
