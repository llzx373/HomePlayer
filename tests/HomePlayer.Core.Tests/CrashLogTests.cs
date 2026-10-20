using HomePlayer.Core;

namespace HomePlayer.Core.Tests;

public class CrashLogTests
{
    [Fact]
    public void Format_includes_kind_time_and_details()
    {
        var time = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.FromHours(8));
        var text = CrashLog.Format("未处理异常", "System.Exception: boom", time, "Version: 0.1.0");

        Assert.Contains("HomePlayer crash", text);
        Assert.Contains("Kind: 未处理异常", text);
        Assert.Contains("Time: " + time.ToString("O"), text);
        Assert.Contains("Version: 0.1.0", text);
        Assert.Contains("System.Exception: boom", text);
    }

    [Fact]
    public void Write_and_list_round_trip()
    {
        using var folder = new TempDir();
        var time = new DateTimeOffset(2026, 10, 20, 11, 49, 0, TimeSpan.FromHours(8));

        var path = CrashLog.Write(folder.Path, "未处理异常", "System.NullReferenceException: x", time, "Version: test");

        Assert.True(File.Exists(path));
        var items = CrashLog.List(folder.Path);
        Assert.Single(items);
        Assert.Equal("未处理异常", items[0].Kind);
        Assert.Equal(time, items[0].Time);
        Assert.Equal("System.NullReferenceException: x", items[0].Summary);
        Assert.Contains("Version: test", CrashLog.Read(path));
    }

    [Fact]
    public void Write_skips_duplicate_within_three_seconds()
    {
        using var folder = new TempDir();
        var time = DateTimeOffset.Now;

        var first = CrashLog.Write(folder.Path, "未处理异常", "same", time, null);
        var second = CrashLog.Write(folder.Path, "未处理异常", "same", time.AddSeconds(1), null);

        Assert.False(string.IsNullOrWhiteSpace(first));
        Assert.Equal("", second);
        Assert.Single(CrashLog.List(folder.Path));
    }

    [Fact]
    public void Trim_keeps_newest_logs_and_matching_dumps()
    {
        using var folder = new TempDir();
        for (var i = 0; i < 5; i++)
        {
            var path = Path.Combine(folder.Path, $"2026010{i}-000000-000.log");
            File.WriteAllText(path, "HomePlayer crash\nKind: old\n");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(i));
            File.WriteAllText(Path.ChangeExtension(path, ".dmp"), "dump");
        }

        CrashLog.Trim(folder.Path, 2);

        var logs = Directory.GetFiles(folder.Path, "*.log");
        var dumps = Directory.GetFiles(folder.Path, "*.dmp");
        Assert.Equal(2, logs.Length);
        Assert.Equal(2, dumps.Length);
        Assert.Contains("20260104", Path.GetFileName(logs.OrderBy(path => path).Last()));
    }

    [Fact]
    public void ImportLegacy_moves_old_crash_log()
    {
        using var folder = new TempDir();
        var legacy = Path.Combine(folder.Path, "crash.log");
        var crashes = Path.Combine(folder.Path, "crashes");
        Directory.CreateDirectory(crashes);
        File.WriteAllText(legacy, "old crash A\n\nold crash B\n");

        var imported = CrashLog.ImportLegacy(legacy, crashes);

        Assert.False(File.Exists(legacy));
        Assert.True(File.Exists(imported));
        var items = CrashLog.List(crashes);
        Assert.Single(items);
        Assert.Equal(CrashLog.LegacyKind, items[0].Kind);
        Assert.Contains("old crash A", CrashLog.Read(imported!));
    }

    [Fact]
    public void ConsumeAbandonedSession_writes_crash_and_clears_lock()
    {
        using var folder = new TempDir();
        var session = Path.Combine(folder.Path, "session.lock");
        var crashes = Path.Combine(folder.Path, "crashes");
        Directory.CreateDirectory(crashes);
        File.WriteAllText(session, "Process: 12\nExecutable: C:\\HomePlayer.exe\n");

        var path = CrashLog.ConsumeAbandonedSession(session, crashes);

        Assert.False(File.Exists(session));
        Assert.True(File.Exists(path));
        var items = CrashLog.List(crashes);
        Assert.Equal(CrashLog.AbnormalExitKind, items[0].Kind);
        Assert.Contains("Process: 12", CrashLog.Read(path!));
    }

    [Fact]
    public void Begin_and_end_session_create_and_remove_lock()
    {
        using var folder = new TempDir();
        var session = Path.Combine(folder.Path, "session.lock");

        CrashLog.BeginSession(session);
        Assert.True(File.Exists(session));
        Assert.Contains("Process:", File.ReadAllText(session));

        CrashLog.EndSession(session);
        Assert.False(File.Exists(session));
    }

    [Fact]
    public void Clear_deletes_logs_and_dumps()
    {
        using var folder = new TempDir();
        File.WriteAllText(Path.Combine(folder.Path, "a.log"), "a");
        File.WriteAllText(Path.Combine(folder.Path, "a.dmp"), "d");

        CrashLog.Clear(folder.Path);

        Assert.Empty(Directory.GetFiles(folder.Path));
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "HomePlayerCrashTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                    Directory.Delete(Path, true);
            }
            catch (IOException)
            {
            }
        }
    }
}
