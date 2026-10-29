using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace HomePlayer.Core;

public static partial class SubtitleText
{
    public static IReadOnlyList<SubtitleCue> ParseFile(string path, string? encodingName = null)
        => Parse(ReadAll(path, encodingName), Path.GetExtension(path));

    public static IReadOnlyList<SubtitleCue> Parse(string text, string extension)
    {
        var ext = extension.StartsWith('.') ? extension : "." + extension;
        if (ext.Equals(".ass", StringComparison.OrdinalIgnoreCase) || ext.Equals(".ssa", StringComparison.OrdinalIgnoreCase))
            return ParseAss(text);
        if (ext.Equals(".vtt", StringComparison.OrdinalIgnoreCase))
            return ParseVtt(text);
        return ParseSrt(text);
    }

    public static string Plain(string text)
    {
        var stripped = OverrideTags().Replace(text, "");
        return stripped
            .Replace("\\N", "\n", StringComparison.Ordinal)
            .Replace("\\n", "\n", StringComparison.Ordinal)
            .Replace("\\h", " ", StringComparison.Ordinal)
            .Trim();
    }

    public static string FromAssEvent(string line)
    {
        var text = DialogueText(line, 9);
        return Plain(text ?? line);
    }

    private static IReadOnlyList<SubtitleCue> ParseSrt(string text)
    {
        var lines = SplitLines(text);
        var cues = new List<SubtitleCue>();
        var i = 0;
        while (i < lines.Length)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
            {
                i++;
                continue;
            }

            if (IsIndexLine(lines[i]) && i + 1 < lines.Length && TrySplitTimes(lines[i + 1], out _, out _))
                i++;
            if (i >= lines.Length || !TrySplitTimes(lines[i], out var start, out var end))
            {
                i++;
                continue;
            }

            i++;
            var body = new StringBuilder();
            while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i]) && !TrySplitTimes(lines[i], out _, out _))
            {
                if (body.Length > 0)
                    body.Append('\n');
                body.Append(StripMarkup(lines[i]));
                i++;
            }

            AddCue(cues, start, end, body.ToString());
        }

        return cues;
    }

    private static IReadOnlyList<SubtitleCue> ParseVtt(string text)
    {
        var lines = SplitLines(text);
        var cues = new List<SubtitleCue>();
        var i = 0;
        if (i < lines.Length && lines[i].StartsWith("WEBVTT", StringComparison.OrdinalIgnoreCase))
        {
            i++;
            while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i]))
                i++;
        }

        while (i < lines.Length)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
            {
                i++;
                continue;
            }

            if (IsVttBlock(lines[i], "NOTE") || IsVttBlock(lines[i], "STYLE") || IsVttBlock(lines[i], "REGION"))
            {
                while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i]))
                    i++;
                continue;
            }

            if (!TrySplitTimes(lines[i], out var start, out var end))
            {
                i++;
                if (i >= lines.Length || !TrySplitTimes(lines[i], out start, out end))
                    continue;
            }

            i++;
            var body = new StringBuilder();
            while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i]) && !TrySplitTimes(lines[i], out _, out _))
            {
                if (body.Length > 0)
                    body.Append('\n');
                body.Append(StripMarkup(lines[i]));
                i++;
            }

            AddCue(cues, start, end, body.ToString());
        }

        return cues;
    }

    private static IReadOnlyList<SubtitleCue> ParseAss(string text)
    {
        var cues = new List<SubtitleCue>();
        var textField = 9;
        foreach (var raw in SplitLines(text))
        {
            var line = raw.Trim();
            if (line.StartsWith("Format:", StringComparison.OrdinalIgnoreCase))
            {
                var fields = line["Format:".Length..].Split(',');
                for (var i = 0; i < fields.Length; i++)
                {
                    if (fields[i].Trim().Equals("Text", StringComparison.OrdinalIgnoreCase))
                    {
                        textField = i;
                        break;
                    }
                }

                continue;
            }

            if (!line.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase))
                continue;
            var payload = line["Dialogue:".Length..].TrimStart();
            if (!TryDialogueTimes(payload, textField, out var start, out var end, out var body))
                continue;
            AddCue(cues, start, end, Plain(body));
        }

        return cues;
    }

    private static bool TryDialogueTimes(string payload, int textField, out TimeSpan start, out TimeSpan end, out string body)
    {
        start = default;
        end = default;
        body = "";
        var commas = 0;
        var startAt = -1;
        var endAt = -1;
        var textAt = -1;
        for (var i = 0; i < payload.Length; i++)
        {
            if (payload[i] != ',')
                continue;
            commas++;
            if (commas == 1)
                startAt = i + 1;
            else if (commas == 2)
                endAt = i + 1;
            if (commas == textField)
            {
                textAt = i + 1;
                break;
            }
        }

        if (startAt < 0 || endAt < 0 || textAt < 0)
            return false;
        var startText = payload[startAt..(endAt - 1)];
        var endText = payload[endAt..payload.IndexOf(',', endAt)];
        if (!TryParseAssTime(startText, out start) || !TryParseAssTime(endText, out end))
            return false;
        body = payload[textAt..];
        return true;
    }

    private static string? DialogueText(string line, int textField)
    {
        var trimmed = line.Trim();
        if (trimmed.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed["Dialogue:".Length..].TrimStart();
        var commas = 0;
        for (var i = 0; i < trimmed.Length; i++)
        {
            if (trimmed[i] != ',')
                continue;
            commas++;
            if (commas == textField)
                return trimmed[(i + 1)..];
        }

        return null;
    }

    private static void AddCue(List<SubtitleCue> cues, TimeSpan start, TimeSpan end, string text)
    {
        text = text.Trim();
        if (text.Length == 0 || end <= start)
            return;
        cues.Add(new SubtitleCue(start, end, text));
    }

    private static bool TrySplitTimes(string line, out TimeSpan start, out TimeSpan end)
    {
        start = default;
        end = default;
        var arrow = line.IndexOf("-->", StringComparison.Ordinal);
        if (arrow < 0)
            return false;
        var left = line[..arrow].Trim();
        var right = line[(arrow + 3)..].Trim();
        var rightToken = right.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries)[0];
        return TryParseCueTime(left, out start) && TryParseCueTime(rightToken, out end);
    }

    private static bool TryParseCueTime(string text, out TimeSpan time)
    {
        time = default;
        var token = text.Trim().Replace(',', '.');
        var parts = token.Split(':');
        if (parts.Length is < 2 or > 3)
            return false;
        if (parts.Length == 2)
        {
            if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes))
                return false;
            if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
                return false;
            time = TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds);
            return true;
        }

        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hours))
            return false;
        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var mins))
            return false;
        if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var secs))
            return false;
        time = TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(mins) + TimeSpan.FromSeconds(secs);
        return true;
    }

    private static bool TryParseAssTime(string text, out TimeSpan time)
        => TryParseCueTime(text.Trim(), out time);

    private static bool IsIndexLine(string line)
    {
        for (var i = 0; i < line.Length; i++)
        {
            if (!char.IsDigit(line[i]))
                return false;
        }

        return line.Length > 0;
    }

    private static bool IsVttBlock(string line, string name)
        => line.Equals(name, StringComparison.OrdinalIgnoreCase)
            || line.StartsWith(name + " ", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith(name + "\t", StringComparison.OrdinalIgnoreCase);

    private static string StripMarkup(string line) => MarkupTags().Replace(line, "");

    private static string[] SplitLines(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal)
        .Replace('\r', '\n')
        .Split('\n');

    private static string ReadAll(string path, string? encodingName)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        if (string.IsNullOrWhiteSpace(encodingName))
            return Encoding.UTF8.GetString(bytes);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(encodingName).GetString(bytes);
    }

    [GeneratedRegex(@"\{[^}]*\}", RegexOptions.CultureInvariant)]
    private static partial Regex OverrideTags();

    [GeneratedRegex(@"</?[^>]+>", RegexOptions.CultureInvariant)]
    private static partial Regex MarkupTags();
}
