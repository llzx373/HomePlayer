namespace HomePlayer.Core;

public static class LaunchArgs
{
    public static List<string> Paths(IEnumerable<string>? commandLine, string? launchArguments)
    {
        var raw = new List<string>();
        if (commandLine != null)
        {
            var index = 0;
            foreach (var arg in commandLine)
            {
                if (index++ == 0 && IsAppFile(arg))
                    continue;
                TryAdd(raw, arg);
            }
        }

        if (!string.IsNullOrWhiteSpace(launchArguments))
        {
            foreach (var arg in Split(launchArguments))
                TryAdd(raw, arg);
        }

        return raw;
    }

    public static List<string> Split(string value)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        foreach (var ch in value)
        {
            if (ch == '"')
            {
                quoted = !quoted;
                continue;
            }

            if (char.IsWhiteSpace(ch) && !quoted)
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(ch);
        }

        if (current.Length > 0)
            result.Add(current.ToString());
        return result;
    }

    private static void TryAdd(List<string> raw, string arg)
    {
        arg = arg.Trim().Trim('"');
        if (arg.Length == 0 || IsAppFile(arg))
            return;
        if (arg[0] == '-' && !Path.IsPathRooted(arg))
            return;
        if (!raw.Contains(arg, StringComparer.OrdinalIgnoreCase))
            raw.Add(arg);
    }

    private static bool IsAppFile(string arg)
    {
        var name = Path.GetFileName(arg.Trim().Trim('"'));
        return name.Equals("HomePlayer.exe", StringComparison.OrdinalIgnoreCase)
            || name.Equals("HomePlayer.dll", StringComparison.OrdinalIgnoreCase);
    }
}
