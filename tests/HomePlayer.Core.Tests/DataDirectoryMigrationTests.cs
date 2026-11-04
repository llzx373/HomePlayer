using HomePlayer.Core;

namespace HomePlayer.Core.Tests;

public class DataDirectoryMigrationTests
{
    [Fact]
    public void Moves_data_and_keeps_settings_file_in_place()
    {
        var root = NewDirectory();
        var source = Path.Combine(root, "source");
        var target = Path.Combine(root, "target");
        Directory.CreateDirectory(Path.Combine(source, "cache"));
        File.WriteAllText(Path.Combine(source, "library.db"), "db");
        File.WriteAllText(Path.Combine(source, "settings.json"), "settings");
        File.WriteAllText(Path.Combine(source, "settings.json.bak"), "backup");
        File.WriteAllText(Path.Combine(source, "session.lock"), "stale");
        File.WriteAllText(Path.Combine(source, "cache", "thumb.bin"), "thumb");
        try
        {
            DataDirectoryMigration.MoveData(source, target, "settings.json");

            Assert.Equal("db", File.ReadAllText(Path.Combine(target, "library.db")));
            Assert.Equal("thumb", File.ReadAllText(Path.Combine(target, "cache", "thumb.bin")));
            Assert.False(File.Exists(Path.Combine(target, "settings.json")));
            Assert.False(File.Exists(Path.Combine(target, "session.lock")));
            Assert.Equal("settings", File.ReadAllText(Path.Combine(source, "settings.json")));
            Assert.Equal("backup", File.ReadAllText(Path.Combine(source, "settings.json.bak")));
            Assert.False(File.Exists(Path.Combine(source, "library.db")));
            Assert.False(Directory.Exists(Path.Combine(source, "cache")));
            Assert.False(File.Exists(Path.Combine(source, "session.lock")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Rejects_nested_targets()
    {
        var root = NewDirectory();
        var source = Path.Combine(root, "source");
        Directory.CreateDirectory(source);
        try
        {
            Assert.Throws<IOException>(() => DataDirectoryMigration.MoveData(source, Path.Combine(source, "nested"), null));
            Assert.Throws<IOException>(() => DataDirectoryMigration.MoveData(Path.Combine(source, "nested"), source, null));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "HomePlayer-data-migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
