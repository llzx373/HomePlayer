using HomePlayer.Core;
using HomePlayer.Data;

namespace HomePlayer.Library.Tests;

public class DataBackupTests
{
    [Fact]
    public void Backup_package_restores_database_and_settings()
    {
        var root = TempDirectory();
        try
        {
            var database = Path.Combine(root, "library.db");
            var settings = Path.Combine(root, "settings.json");
            var path = @"D:\lib\movie.mkv";
            using (var repository = new LibraryRepository(database))
            {
                repository.Save(new PlaybackState(path, 42, 100, false, 10));
                repository.Flush();
                repository.SavePreference(MediaPreference.Empty(path) with { AudioTrackIndex = 2 });
            }
            new SettingsStore(settings).Save(new AppSettings { CloseToTray = false, SubtitleFontSize = 36 });
            var archive = Path.Combine(root, "backup.homeplayer");
            DataBackup.Export(database, settings, archive);

            using (var repository = new LibraryRepository(database))
                repository.ClearPlaybackStates();
            new SettingsStore(settings).Save(new AppSettings { CloseToTray = true, SubtitleFontSize = 20 });
            DataBackup.StageRestore(archive, root);

            Assert.True(DataBackup.ApplyPendingRestore(root));
            Assert.False(DataBackup.ApplyPendingRestore(root));
            using (var restored = new LibraryRepository(database))
            {
                Assert.Equal(42, restored.Load(path)?.PositionSeconds);
                Assert.Equal(2, restored.LoadPreference(path)?.AudioTrackIndex);
            }
            var restoredSettings = new SettingsStore(settings).Load().Settings;
            Assert.False(restoredSettings.CloseToTray);
            Assert.Equal(36, restoredSettings.SubtitleFontSize);
            Assert.Contains(Directory.EnumerateDirectories(Path.Combine(root, "backups")),
                directory => Path.GetFileName(directory).StartsWith("before-restore-", StringComparison.Ordinal));
        }
        finally
        {
            Delete(root);
        }
    }

    [Fact]
    public void Automatic_backup_keeps_current_and_previous_generations()
    {
        var root = TempDirectory();
        try
        {
            var database = Path.Combine(root, "library.db");
            using (var repository = new LibraryRepository(database))
            {
                repository.Save(new PlaybackState(@"D:\lib\a.mkv", 20, 100, false, 1));
                repository.Flush();
            }
            var settings = Path.Combine(root, "settings.json");
            new SettingsStore(settings).Save(new AppSettings { SubtitleFontSize = 30 });

            DataBackup.CreateAutomaticBackup(root);
            new SettingsStore(settings).Save(new AppSettings { SubtitleFontSize = 40 });
            DataBackup.CreateAutomaticBackup(root);

            Assert.True(File.Exists(Path.Combine(root, "backups", "automatic", "library.db")));
            Assert.True(File.Exists(Path.Combine(root, "backups", "previous", "settings.json")));
            Assert.Equal(30, new SettingsStore(Path.Combine(root, "backups", "previous", "settings.json")).Load().Settings.SubtitleFontSize);
            Assert.Equal(40, new SettingsStore(Path.Combine(root, "backups", "automatic", "settings.json")).Load().Settings.SubtitleFontSize);
        }
        finally
        {
            Delete(root);
        }
    }

    [Fact]
    public void Invalid_package_is_rejected_without_staging()
    {
        var root = TempDirectory();
        try
        {
            var archive = Path.Combine(root, "invalid.homeplayer");
            using (var stream = File.Create(archive))
            using (var zip = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry("readme.txt");
                using var writer = new StreamWriter(entry.Open());
                writer.Write("not a backup");
            }

            Assert.Throws<InvalidDataException>(() => DataBackup.StageRestore(archive, root));
            Assert.False(Directory.Exists(Path.Combine(root, "pending-restore")));
        }
        finally
        {
            Delete(root);
        }
    }

    private static string TempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "HomePlayer.Backup." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Delete(string path)
    {
        try
        {
            Directory.Delete(path, true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
