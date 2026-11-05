using System.Globalization;
using System.Text;

namespace HomePlayer.Core;

public static class SubtitleFileWriter
{
    public static void WriteSrt(string path, IEnumerable<SubtitleCue> cues, bool bilingual = false) =>
        WriteAtomic(path, FormatSrt(cues, bilingual));

    public static void WriteVtt(string path, IEnumerable<SubtitleCue> cues) =>
        WriteAtomic(path, "WEBVTT\n\n" + Format(cues, ".", false, false));

    public static void WriteText(string path, IEnumerable<SubtitleCue> cues)
    {
        var text = string.Join(Environment.NewLine, cues.Select(cue => cue.Text.Trim()).Where(text => text.Length > 0)) + Environment.NewLine;
        WriteAtomic(path, text);
    }

    public static string FormatSrt(IEnumerable<SubtitleCue> cues, bool bilingual = false) => Format(cues, ",", true, bilingual);

    private static string Format(IEnumerable<SubtitleCue> cues, string decimalSeparator, bool numbered, bool bilingual)
    {
        var builder = new StringBuilder();
        var index = 0;
        foreach (var cue in cues)
        {
            if (cue.Start < TimeSpan.Zero || cue.End <= cue.Start)
                throw new InvalidDataException("字幕包含无效时间范围。");
            if (string.IsNullOrWhiteSpace(cue.Text))
                throw new InvalidDataException("字幕包含空文本。");
            if (numbered)
                builder.AppendLine((++index).ToString(CultureInfo.InvariantCulture));
            builder.Append(TimeText(cue.Start, decimalSeparator));
            builder.Append(" --> ");
            builder.AppendLine(TimeText(cue.End, decimalSeparator));
            var body = cue.Text.Trim();
            if (bilingual && cue.HasTranslation)
                body += "\n" + cue.Translation!.Trim();
            builder.AppendLine(body);
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static string TimeText(TimeSpan value, string decimalSeparator)
    {
        var milliseconds = (long)Math.Round(value.TotalMilliseconds, MidpointRounding.AwayFromZero);
        var hours = milliseconds / 3_600_000;
        var minutes = milliseconds / 60_000 % 60;
        var seconds = milliseconds / 1000 % 60;
        var millis = milliseconds % 1000;
        return FormattableString.Invariant($"{hours:00}:{minutes:00}:{seconds:00}{decimalSeparator}{millis:000}");
    }

    private static void WriteAtomic(string path, string text)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        var temp = path + ".tmp";
        try
        {
            File.WriteAllText(temp, text, new UTF8Encoding(false));
            File.Move(temp, path, true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }
}
