using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace HomePlayer.Core;

public static partial class DiagnosticsPackage
{
    public static void Export(
        string destination,
        PipelineInfo? pipeline,
        string crashDirectory,
        bool includeSensitive = false,
        string? settingsJson = null,
        params string[] sensitiveValues)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(destination))
            ?? throw new InvalidOperationException("无法确定诊断包保存目录。");
        Directory.CreateDirectory(directory);
        using var stream = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        Write(archive, "system.txt", SystemText());
        Write(archive, "performance.txt", PerformanceMetrics.Format());
        Write(archive, "pipeline.txt", Redact(pipeline?.Format() ?? "当前没有播放中的媒体。", sensitiveValues));
        Write(archive, includeSensitive ? "errors-full.log" : "errors.log", RecentErrors(crashDirectory, includeSensitive, sensitiveValues));
        if (includeSensitive && settingsJson != null)
            Write(archive, "settings.json", settingsJson);
    }

    private static string SystemText()
    {
        var version = typeof(DiagnosticsPackage).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(DiagnosticsPackage).Assembly.GetName().Version?.ToString()
            ?? "unknown";
        return string.Join(Environment.NewLine, [
            "HomePlayer " + version,
            "OS: " + Environment.OSVersion,
            "64-bit process: " + Environment.Is64BitProcess,
            ".NET: " + Environment.Version,
            "UTC: " + DateTimeOffset.UtcNow.ToString("O")
        ]);
    }

    private static string RecentErrors(string directory, bool includeSensitive, IReadOnlyCollection<string> sensitiveValues)
    {
        var text = new StringBuilder();
        foreach (var item in CrashLog.List(directory).Take(10))
        {
            try
            {
                text.AppendLine("==== " + item.Title + " ====");
                var log = CrashLog.Read(item.Path);
                text.AppendLine(includeSensitive ? log : Redact(log, sensitiveValues));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        return text.Length == 0 ? "没有最近错误。" : text.ToString();
    }

    private static string Redact(string text, IReadOnlyCollection<string> sensitiveValues)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            foreach (var value in sensitiveValues.Where(value => !string.IsNullOrWhiteSpace(value)))
                lines[i] = lines[i].Replace(value, "<已脱敏>", StringComparison.OrdinalIgnoreCase);
            if (PathPattern().IsMatch(lines[i]))
                lines[i] = "<已省略包含完整路径的行>";
        }
        return string.Join(Environment.NewLine, lines);
    }

    private static void Write(ZipArchive archive, string name, string text)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(text);
    }

    [GeneratedRegex(@"(?:[A-Za-z]:\\|\\\\)[^\r\n]*", RegexOptions.CultureInvariant)]
    private static partial Regex PathPattern();
}
