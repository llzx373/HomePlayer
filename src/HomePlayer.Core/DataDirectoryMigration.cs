namespace HomePlayer.Core;

public static class DataDirectoryMigration
{
    private const string SessionFileName = "session.lock";

    public static void MoveData(string source, string target, string? settingsFileName = null)
    {
        source = Normalize(source);
        target = Normalize(target);
        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
            return;
        if (IsWithin(source, target) || IsWithin(target, source))
            throw new IOException("数据目录不能互相包含。");
        Directory.CreateDirectory(target);
        if (!Directory.Exists(source))
            return;

        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            var name = Path.GetFileName(entry);
            if (IsPreserved(name, settingsFileName))
                continue;
            var destination = Path.Combine(target, name);
            if (Directory.Exists(entry))
                CopyDirectory(entry, destination);
            else
                CopyFile(entry, destination);
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            var name = Path.GetFileName(entry);
            if (IsSettingsFile(name, settingsFileName))
                continue;
            if (Directory.Exists(entry))
                Directory.Delete(entry, true);
            else
                File.Delete(entry);
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            var name = Path.GetFileName(entry);
            var destination = Path.Combine(target, name);
            if (Directory.Exists(entry))
                CopyDirectory(entry, destination);
            else
                CopyFile(entry, destination);
        }
    }

    private static void CopyFile(string source, string target)
    {
        var info = new FileInfo(source);
        if (File.Exists(target) && new FileInfo(target).Length == info.Length)
            return;
        File.Copy(source, target, true);
    }

    private static bool IsPreserved(string name, string? settingsFileName) =>
        string.Equals(name, SessionFileName, StringComparison.OrdinalIgnoreCase)
        || IsSettingsFile(name, settingsFileName);

    private static bool IsSettingsFile(string name, string? settingsFileName) =>
        !string.IsNullOrEmpty(settingsFileName)
        && (string.Equals(name, settingsFileName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, settingsFileName + ".bak", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, settingsFileName + ".tmp", StringComparison.OrdinalIgnoreCase));

    private static string Normalize(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool IsWithin(string parent, string child) =>
        child.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
