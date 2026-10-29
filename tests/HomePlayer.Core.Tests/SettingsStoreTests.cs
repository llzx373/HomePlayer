using System.Text.Json;
using HomePlayer.Core;

namespace HomePlayer.Core.Tests;

public class SettingsStoreTests
{
    [Fact]
    public void Corrupt_main_file_recovers_the_last_backup()
    {
        var directory = NewDirectory();
        try
        {
            var path = Path.Combine(directory, "settings.json");
            var store = new SettingsStore(path);
            store.Save(new AppSettings { Volume = 0.8 });
            store.Save(new AppSettings { Volume = 0.2 });
            File.WriteAllText(path, "not json");

            var loaded = store.Load();

            Assert.Equal(0.8, loaded.Settings.Volume);
            Assert.NotNull(loaded.Warning);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Save_replaces_atomically_and_can_preserve_a_known_good_backup()
    {
        var directory = NewDirectory();
        try
        {
            var path = Path.Combine(directory, "settings.json");
            var store = new SettingsStore(path);
            store.Save(new AppSettings { Volume = 0.8 });
            store.Save(new AppSettings { Volume = 0.2 });
            File.WriteAllText(path, "not json");

            store.Save(new AppSettings { Volume = 0.4 }, preserveBackup: true);

            var main = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path));
            var backup = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path + ".bak"));
            Assert.Equal(0.4, main?.Volume);
            Assert.Equal(0.8, backup?.Volume);
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "HomePlayer.Tests." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
