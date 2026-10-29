using System.IO.Compression;
using HomePlayer.Core;

namespace HomePlayer.Core.Tests;

public class DiagnosticsPackageTests
{
    [Fact]
    public void Default_package_redacts_paths_and_secrets()
    {
        using var folder = new TempDir();
        WriteLog(folder.Path, "播放失败", @"无法打开 D:\Movies\Private\movie.mkv，key=secret-key", DateTimeOffset.Now, null);
        var destination = Path.Combine(folder.Path, "diagnostics.zip");
        var pipeline = new PipelineInfo { FilePath = @"D:\Movies\Private\movie.mkv", Engine = "FFmpeg" };

        DiagnosticsPackage.Export(destination, pipeline, folder.Path, settingsJson: "{\"key\":\"secret-key\"}", sensitiveValues: "secret-key");

        using var archive = ZipFile.OpenRead(destination);
        var text = string.Join("\n", archive.Entries.Select(Read));
        Assert.DoesNotContain(@"D:\Movies", text);
        Assert.DoesNotContain("secret-key", text);
        Assert.Null(archive.GetEntry("settings.json"));
        Assert.Contains("FFmpeg", text);
    }

    [Fact]
    public void Sensitive_package_includes_full_logs_and_settings()
    {
        using var folder = new TempDir();
        WriteLog(folder.Path, "播放失败", @"D:\Movies\movie.mkv secret-key", DateTimeOffset.Now, null);
        var destination = Path.Combine(folder.Path, "diagnostics.zip");

        DiagnosticsPackage.Export(destination, null, folder.Path, true, "{\"key\":\"secret-key\"}", "secret-key");

        using var archive = ZipFile.OpenRead(destination);
        Assert.Contains("secret-key", Read(archive.GetEntry("errors-full.log")!));
        Assert.Contains("secret-key", Read(archive.GetEntry("settings.json")!));
    }

    private static void WriteLog(string directory, string kind, string details, DateTimeOffset time, string? context) =>
        File.WriteAllText(Path.Combine(directory, "error.log"), CrashLog.Format(kind, details, time, context));

    private static string Read(ZipArchiveEntry entry)
    {
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "HomePlayer.Diagnostics." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
