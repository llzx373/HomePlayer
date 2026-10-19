using Windows.System;

namespace HomePlayer;

internal static class HotkeyChord
{
    private const VirtualKey PeriodKey = (VirtualKey)190;

    public static bool TryParse(string? text, out VirtualKey key, out VirtualKeyModifiers modifiers)
    {
        key = VirtualKey.None;
        modifiers = VirtualKeyModifiers.None;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        foreach (var part in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || part.Equals("Control", StringComparison.OrdinalIgnoreCase))
                modifiers |= VirtualKeyModifiers.Control;
            else if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase) || part.Equals("Menu", StringComparison.OrdinalIgnoreCase))
                modifiers |= VirtualKeyModifiers.Menu;
            else if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase))
                modifiers |= VirtualKeyModifiers.Shift;
            else if (part.Equals("Win", StringComparison.OrdinalIgnoreCase) || part.Equals("Windows", StringComparison.OrdinalIgnoreCase))
                modifiers |= VirtualKeyModifiers.Windows;
            else if (TryParseKey(part, out var parsed))
                key = parsed;
            else
                return false;
        }

        return key != VirtualKey.None && !IsModifier(key);
    }

    public static string Format(VirtualKey key, VirtualKeyModifiers modifiers)
    {
        var parts = new List<string>(4);
        if (modifiers.HasFlag(VirtualKeyModifiers.Control))
            parts.Add("Ctrl");
        if (modifiers.HasFlag(VirtualKeyModifiers.Menu))
            parts.Add("Alt");
        if (modifiers.HasFlag(VirtualKeyModifiers.Shift))
            parts.Add("Shift");
        if (modifiers.HasFlag(VirtualKeyModifiers.Windows))
            parts.Add("Win");
        parts.Add(KeyName(key));
        return string.Join("+", parts);
    }

    public static string Display(string? chord)
        => string.IsNullOrWhiteSpace(chord) ? "无" : chord.Replace("Space", "空格", StringComparison.OrdinalIgnoreCase)
            .Replace("Escape", "Esc", StringComparison.OrdinalIgnoreCase);

    public static bool IsModifier(VirtualKey key) => key is VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl
        or VirtualKey.Shift or VirtualKey.LeftShift or VirtualKey.RightShift
        or VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu
        or VirtualKey.LeftWindows or VirtualKey.RightWindows;

    private static bool TryParseKey(string text, out VirtualKey key)
    {
        if (text.Equals("空格", StringComparison.OrdinalIgnoreCase) || text.Equals("Space", StringComparison.OrdinalIgnoreCase))
        {
            key = VirtualKey.Space;
            return true;
        }

        if (text.Equals("Esc", StringComparison.OrdinalIgnoreCase))
        {
            key = VirtualKey.Escape;
            return true;
        }

        if (text.Length == 1)
        {
            var ch = char.ToUpperInvariant(text[0]);
            if (ch is >= 'A' and <= 'Z')
            {
                key = (VirtualKey)ch;
                return true;
            }

            if (ch is >= '0' and <= '9')
            {
                key = VirtualKey.Number0 + (ch - '0');
                return true;
            }

            if (text == ".")
            {
                key = PeriodKey;
                return true;
            }
        }

        if (text.Equals("OemPeriod", StringComparison.OrdinalIgnoreCase) || text == "190")
        {
            key = PeriodKey;
            return true;
        }

        if (text.StartsWith("Number", StringComparison.OrdinalIgnoreCase) && text.Length == 7 && char.IsDigit(text[6]))
        {
            key = VirtualKey.Number0 + (text[6] - '0');
            return true;
        }

        return Enum.TryParse(text, true, out key) && key != VirtualKey.None;
    }

    private static string KeyName(VirtualKey key)
    {
        if (key == VirtualKey.Space)
            return "Space";
        if (key == PeriodKey)
            return ".";
        if (key is >= VirtualKey.Number0 and <= VirtualKey.Number9)
            return ((char)('0' + (key - VirtualKey.Number0))).ToString();
        if (key is >= VirtualKey.A and <= VirtualKey.Z)
            return ((char)('A' + (key - VirtualKey.A))).ToString();
        return key.ToString();
    }
}
