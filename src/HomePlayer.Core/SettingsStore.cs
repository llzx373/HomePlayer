using System.Text.Json;

namespace HomePlayer.Core;

public sealed record SettingsLoadResult(AppSettings Settings, string? Warning);

public sealed class SettingsStore(string path)
{
    private readonly string _path = path;
    private readonly string _backupPath = path + ".bak";
    private readonly string _tempPath = path + ".tmp";

    public SettingsLoadResult Load()
    {
        if (TryLoad(_path, out var settings))
            return new SettingsLoadResult(settings, null);
        if (TryLoad(_backupPath, out settings))
            return new SettingsLoadResult(settings, "设置文件无法读取，已恢复最近可用的备份。");
        if (!File.Exists(_path) && !File.Exists(_backupPath))
            return new SettingsLoadResult(WithHotkeys(new AppSettings()), null);
        return new SettingsLoadResult(
            WithHotkeys(new AppSettings()),
            "设置文件和备份都无法读取，已暂时使用默认设置。原文件未被删除。");
    }

    public void Save(AppSettings settings, bool preserveBackup = false)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        try
        {
            File.WriteAllText(_tempPath, json);
            if (!File.Exists(_path))
                File.Move(_tempPath, _path);
            else if (preserveBackup)
                File.Move(_tempPath, _path, true);
            else
                File.Replace(_tempPath, _path, _backupPath, true);
        }
        finally
        {
            if (File.Exists(_tempPath))
                File.Delete(_tempPath);
        }
    }

    private static bool TryLoad(string path, out AppSettings settings)
    {
        try
        {
            if (!File.Exists(path))
            {
                settings = new AppSettings();
                return false;
            }

            settings = WithHotkeys(JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new AppSettings());
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            settings = new AppSettings();
            return false;
        }
    }

    private static AppSettings WithHotkeys(AppSettings settings)
    {
        settings.Hotkeys = HotkeyCatalog.Merge(settings.Hotkeys);
        return settings;
    }
}
