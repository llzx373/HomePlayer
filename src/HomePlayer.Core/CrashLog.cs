using System.Globalization;
using System.Reflection;
using System.Text;

namespace HomePlayer.Core;

public sealed class CrashLogItem
{
    public required string Path { get; init; }
    public required string Name { get; init; }
    public required DateTimeOffset Time { get; init; }
    public required string Kind { get; init; }
    public required string Summary { get; init; }
    public long Bytes { get; init; }
    public string? DumpPath { get; init; }
    public long DumpBytes { get; init; }

    public string Title
    {
        get
        {
            var time = Time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            return string.IsNullOrWhiteSpace(Kind) ? time : $"{time}  {Kind}";
        }
    }

    public string Detail
    {
        get
        {
            var dump = DumpBytes > 0 ? $" · 转储 {FormatBytes(DumpBytes)}" : "";
            return $"{Summary}{dump}";
        }
    }

    private static string FormatBytes(long bytes)
    {
        double size = bytes;
        string[] units = ["B", "KB", "MB", "GB"];
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return $"{size:0.##} {units[unit]}";
    }
}

public static class CrashLog
{
    public const int KeepCount = 80;
    public const string UnhandledKind = "未处理异常";
    public const string DomainKind = "运行时异常";
    public const string TaskKind = "后台任务异常";
    public const string NativeKind = "原生崩溃";
    public const string AbnormalExitKind = "上次异常退出";
    public const string LegacyKind = "历史记录";

    private static readonly object Gate = new();
    private static string? _lastFingerprint;
    private static DateTimeOffset _lastWrite;

    public static string DirectoryPath => AppPaths.Crashes;

    public static string Write(string kind, string details) =>
        Write(DirectoryPath, kind, details, DateTimeOffset.Now, DefaultContext());

    public static string Write(string directory, string kind, string details, DateTimeOffset time, string? context)
    {
        kind = string.IsNullOrWhiteSpace(kind) ? UnhandledKind : kind.Trim();
        details = details ?? "";
        var fingerprint = directory + "\n" + kind + "\n" + (details.Length > 400 ? details[..400] : details);
        lock (Gate)
        {
            if (fingerprint == _lastFingerprint && time - _lastWrite < TimeSpan.FromSeconds(3))
                return "";

            Directory.CreateDirectory(directory);
            var name = time.ToLocalTime().ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + ".log";
            var path = UniquePath(directory, name);
            var text = Format(kind, details, time, context);
            File.WriteAllText(path, text, Encoding.UTF8);
            _lastFingerprint = fingerprint;
            _lastWrite = time;
            Trim(directory, KeepCount);
            return path;
        }
    }

    public static string Format(string kind, string details, DateTimeOffset time, string? context)
    {
        var builder = new StringBuilder();
        builder.AppendLine("HomePlayer crash");
        builder.AppendLine("Time: " + time.ToString("O"));
        builder.AppendLine("Kind: " + kind);
        if (!string.IsNullOrWhiteSpace(context))
        {
            builder.AppendLine(context.TrimEnd());
            builder.AppendLine();
        }

        builder.Append(details.TrimEnd());
        if (builder.Length == 0 || builder[^1] != '\n')
            builder.AppendLine();
        return builder.ToString();
    }

    public static string DefaultContext()
    {
        var version = typeof(CrashLog).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(CrashLog).Assembly.GetName().Version?.ToString()
            ?? "unknown";
        return string.Join(Environment.NewLine, [
            "Version: " + version,
            "OS: " + Environment.OSVersion,
            "64Bit: " + Environment.Is64BitProcess,
            "Process: " + Environment.ProcessId,
            "Thread: " + Environment.CurrentManagedThreadId,
            "Executable: " + (Environment.ProcessPath ?? "")
        ]);
    }

    public static IReadOnlyList<CrashLogItem> List() => List(DirectoryPath);

    public static IReadOnlyList<CrashLogItem> List(string directory)
    {
        if (!Directory.Exists(directory))
            return [];

        var items = new List<CrashLogItem>();
        foreach (var path in Directory.GetFiles(directory, "*.log"))
        {
            try
            {
                items.Add(ReadItem(path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return items.OrderByDescending(item => item.Time).ToList();
    }

    public static string Read(string path) => File.ReadAllText(path, Encoding.UTF8);

    public static void Clear() => Clear(DirectoryPath);

    public static void Clear(string directory)
    {
        if (!Directory.Exists(directory))
            return;
        foreach (var path in Directory.GetFiles(directory))
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    public static string? ImportLegacy() => ImportLegacy(AppPaths.LegacyCrashLog, DirectoryPath);

    public static string? ImportLegacy(string legacyFile, string directory)
    {
        try
        {
            if (!File.Exists(legacyFile))
                return null;
            var text = File.ReadAllText(legacyFile);
            if (string.IsNullOrWhiteSpace(text))
            {
                File.Delete(legacyFile);
                return null;
            }

            var path = Write(directory, LegacyKind, text.TrimEnd(), File.GetLastWriteTime(legacyFile), "Imported: " + legacyFile);
            File.Delete(legacyFile);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static string? ConsumeAbandonedSession() => ConsumeAbandonedSession(AppPaths.SessionLock, DirectoryPath);

    public static string? ConsumeAbandonedSession(string sessionFile, string directory)
    {
        try
        {
            if (!File.Exists(sessionFile))
                return null;
            var text = File.ReadAllText(sessionFile);
            File.Delete(sessionFile);
            if (string.IsNullOrWhiteSpace(text))
                return null;
            return Write(directory, AbnormalExitKind, "上次进程没有正常退出。\n\n" + text.TrimEnd(), DateTimeOffset.Now, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void BeginSession() => BeginSession(AppPaths.SessionLock);

    public static void BeginSession(string sessionFile)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(sessionFile)!);
            File.WriteAllText(sessionFile, DefaultContext() + Environment.NewLine + "Started: " + DateTimeOffset.Now.ToString("O") + Environment.NewLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public static void EndSession() => EndSession(AppPaths.SessionLock);

    public static void EndSession(string sessionFile)
    {
        try
        {
            if (File.Exists(sessionFile))
                File.Delete(sessionFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public static void Trim(string directory, int keep)
    {
        if (keep < 1 || !Directory.Exists(directory))
            return;
        var files = Directory.GetFiles(directory, "*.log")
            .Select(path => (Path: path, Time: File.GetLastWriteTimeUtc(path)))
            .OrderByDescending(item => item.Time)
            .Skip(keep)
            .ToList();
        foreach (var file in files)
        {
            TryDelete(file.Path);
            TryDelete(System.IO.Path.ChangeExtension(file.Path, ".dmp"));
        }
    }

    private static CrashLogItem ReadItem(string path)
    {
        var info = new FileInfo(path);
        var dump = System.IO.Path.ChangeExtension(path, ".dmp");
        var dumpInfo = new FileInfo(dump);
        var header = ReadHeader(path);
        return new CrashLogItem
        {
            Path = path,
            Name = info.Name,
            Time = header.Time ?? new DateTimeOffset(info.LastWriteTime),
            Kind = header.Kind ?? "",
            Summary = header.Summary ?? info.Name,
            Bytes = info.Length,
            DumpPath = dumpInfo.Exists ? dump : null,
            DumpBytes = dumpInfo.Exists ? dumpInfo.Length : 0
        };
    }

    private static (DateTimeOffset? Time, string? Kind, string? Summary) ReadHeader(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        DateTimeOffset? time = null;
        string? kind = null;
        string? summary = null;
        for (var i = 0; i < 24; i++)
        {
            var line = reader.ReadLine();
            if (line == null)
                break;
            if (line.StartsWith("Time: ", StringComparison.Ordinal) &&
                DateTimeOffset.TryParse(line["Time: ".Length..], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
                time = parsed;
            else if (line.StartsWith("Kind: ", StringComparison.Ordinal))
                kind = line["Kind: ".Length..].Trim();
            else if (!string.IsNullOrWhiteSpace(line)
                && !line.StartsWith("HomePlayer crash", StringComparison.Ordinal)
                && !line.StartsWith("Version: ", StringComparison.Ordinal)
                && !line.StartsWith("OS: ", StringComparison.Ordinal)
                && !line.StartsWith("64Bit: ", StringComparison.Ordinal)
                && !line.StartsWith("Process: ", StringComparison.Ordinal)
                && !line.StartsWith("Thread: ", StringComparison.Ordinal)
                && !line.StartsWith("Executable: ", StringComparison.Ordinal)
                && !line.StartsWith("Imported: ", StringComparison.Ordinal)
                && !line.StartsWith("Started: ", StringComparison.Ordinal)
                && summary == null)
                summary = TrimSummary(line);
        }

        return (time, kind, summary);
    }

    private static string TrimSummary(string line)
    {
        line = line.Trim();
        return line.Length <= 120 ? line : line[..117] + "…";
    }

    private static string UniquePath(string directory, string name)
    {
        var path = Path.Combine(directory, name);
        if (!File.Exists(path) && !File.Exists(Path.ChangeExtension(path, ".dmp")))
            return path;
        for (var i = 2; i < 100; i++)
        {
            var candidate = Path.Combine(directory, Path.GetFileNameWithoutExtension(name) + "-" + i + ".log");
            if (!File.Exists(candidate) && !File.Exists(Path.ChangeExtension(candidate, ".dmp")))
                return candidate;
        }

        return Path.Combine(directory, Path.GetFileNameWithoutExtension(name) + "-" + Guid.NewGuid().ToString("N")[..8] + ".log");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
