using System.Globalization;
using System.Text.RegularExpressions;

namespace HomePlayer.Core;

public sealed partial record AssSubtitleStyle(
    string? FontName,
    double? FontSize,
    uint? BgrColor,
    int? Alignment,
    double? X,
    double? Y,
    bool Bold,
    bool Italic)
{
    public static AssSubtitleStyle? Parse(string? assEvent)
    {
        if (string.IsNullOrWhiteSpace(assEvent) || assEvent.IndexOf('{') < 0)
            return null;
        string? font = null;
        double? size = null;
        uint? color = null;
        int? alignment = null;
        double? x = null;
        double? y = null;
        var bold = false;
        var italic = false;
        foreach (Match match in OverrideBlock().Matches(assEvent))
        {
            foreach (Match tag in Tag().Matches(match.Groups[1].Value))
            {
                var name = tag.Groups[1].Value;
                var value = tag.Groups[2].Value;
                switch (name)
                {
                    case "fn":
                        font = value;
                        break;
                    case "fs" when Number(value, out var fontSize):
                        size = fontSize;
                        break;
                    case "c" when Color(value, out var bgr):
                        color = bgr;
                        break;
                    case "an" when int.TryParse(value, out var an):
                        alignment = an;
                        break;
                    case "pos":
                        var coordinates = value.Trim('(', ')').Split(',');
                        if (coordinates.Length >= 2 && Number(coordinates[0], out var px) && Number(coordinates[1], out var py))
                        {
                            x = px;
                            y = py;
                        }
                        break;
                    case "b":
                        bold = value != "0";
                        break;
                    case "i":
                        italic = value != "0";
                        break;
                }
            }
        }
        return new AssSubtitleStyle(font, size, color, alignment, x, y, bold, italic);
    }

    private static bool Number(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static bool Color(string text, out uint color)
    {
        color = 0;
        var hex = text.TrimStart('&', 'H', 'h').TrimEnd('&');
        return uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out color);
    }

    [GeneratedRegex(@"\{([^}]*)\}", RegexOptions.CultureInvariant)]
    private static partial Regex OverrideBlock();

    [GeneratedRegex(@"\\(fn|fs|an|pos|b|i|c)([^\\{]*)", RegexOptions.CultureInvariant)]
    private static partial Regex Tag();
}
