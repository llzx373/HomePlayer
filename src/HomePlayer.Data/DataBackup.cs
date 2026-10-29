using System.IO.Compression;
using System.Text.Json;
using HomePlayer.Core;
using Microsoft.Data.Sqlite;

namespace HomePlayer.Data;

public static class DataBackup
{
    private const int FormatVersion = 1;
    private const string DatabaseName = "library.db";
    private const string SettingsName = "settings.json";
    private const string ManifestName = "manifest.json";

    public static void CreateAutomaticBackup(string root)
    {
        var database = Path.Combine(root, DatabaseName);
        var settings = Path.Combine(root, SettingsName);
        if (!File.Exists(database) && !File.Exists(settings))
            return;
        var backups = Path.Combine(root, "backups");
        var temporary = Path.Combine(backups, ".automatic-tmp");
        var automatic = Path.Combine(backups, "automatic");
        var previous = Path.Combine(backups, "previous");
        DeleteDirectory(temporary);
        Directory.CreateDirectory(temporary);
        try
        {
            if (File.Exists(database))
                SnapshotDatabase(database, Path.Combine(temporary, DatabaseName));
            if (File.Exists(settings))
                File.Copy(settings, Path.Combine(temporary, SettingsName), true);
            DeleteDirectory(previous);
            if (Directory.Exists(automatic))
                Directory.Move(automatic, previous);
            Directory.Move(temporary, automatic);
        }
        finally
        {
            DeleteDirectory(temporary);
        }
    }

    public static void Export(string database, string settings, string destination)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(destination))
            ?? throw new InvalidOperationException("无法确定备份保存目录。");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, ".homeplayer-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            var snapshot = Path.Combine(temporary, DatabaseName);
            SnapshotDatabase(database, snapshot);
            var manifest = JsonSerializer.Serialize(new BackupManifest(
                FormatVersion, DateTime.UtcNow, LibraryRepository.SchemaVersion));
            File.WriteAllText(Path.Combine(temporary, ManifestName), manifest);
            if (File.Exists(settings))
                File.Copy(settings, Path.Combine(temporary, SettingsName), true);
            if (File.Exists(destination))
                File.Delete(destination);
            ZipFile.CreateFromDirectory(temporary, destination, CompressionLevel.Optimal, false);
        }
        finally
        {
            DeleteDirectory(temporary);
        }
    }

    public static void StageRestore(string archive, string root)
    {
        var temporary = Path.Combine(root, ".restore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            using (var zip = ZipFile.OpenRead(archive))
            {
                var manifestEntry = RequiredEntry(zip, ManifestName);
                BackupManifest? manifest;
                using (var reader = new StreamReader(manifestEntry.Open()))
                    manifest = JsonSerializer.Deserialize<BackupManifest>(reader.ReadToEnd());
                if (manifest == null || manifest.Format != FormatVersion)
                    throw new InvalidDataException("这不是受支持的 HomePlayer 备份包。");
                if (manifest.SchemaVersion > LibraryRepository.SchemaVersion)
                    throw new InvalidDataException("备份来自更新版本，请先升级 HomePlayer。");

                RequiredEntry(zip, DatabaseName).ExtractToFile(Path.Combine(temporary, DatabaseName));
                if (zip.GetEntry(SettingsName) is { } settingsEntry)
                    settingsEntry.ExtractToFile(Path.Combine(temporary, SettingsName));
            }

            ValidateDatabase(Path.Combine(temporary, DatabaseName));
            var settings = Path.Combine(temporary, SettingsName);
            if (File.Exists(settings))
                _ = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(settings))
                    ?? throw new InvalidDataException("备份中的设置文件无效。");
            var pending = Path.Combine(root, "pending-restore");
            DeleteDirectory(pending);
            Directory.Move(temporary, pending);
        }
        finally
        {
            DeleteDirectory(temporary);
        }
    }

    public static bool ApplyPendingRestore(string root)
    {
        var pending = Path.Combine(root, "pending-restore");
        var sourceDatabase = Path.Combine(pending, DatabaseName);
        if (!File.Exists(sourceDatabase))
            return false;
        ValidateDatabase(sourceDatabase);
        var sourceSettings = Path.Combine(pending, SettingsName);
        if (File.Exists(sourceSettings))
            _ = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(sourceSettings))
                ?? throw new InvalidDataException("待恢复设置无效。");

        var safety = Path.Combine(root, "backups", "before-restore-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
        Directory.CreateDirectory(safety);
        var database = Path.Combine(root, DatabaseName);
        var settings = Path.Combine(root, SettingsName);
        if (File.Exists(database))
            SnapshotDatabase(database, Path.Combine(safety, DatabaseName));
        if (File.Exists(settings))
            File.Copy(settings, Path.Combine(safety, SettingsName), true);

        DeleteFile(database + "-wal");
        DeleteFile(database + "-shm");
        ReplaceFile(sourceDatabase, database);
        if (File.Exists(sourceSettings))
            ReplaceFile(sourceSettings, settings);
        DeleteDirectory(pending);
        return true;
    }

    private static void SnapshotDatabase(string source, string destination)
    {
        if (!File.Exists(source))
            throw new FileNotFoundException("找不到媒体库数据库。", source);
        DeleteFile(destination);
        var builder = new SqliteConnectionStringBuilder { DataSource = source, Mode = SqliteOpenMode.ReadWrite, Pooling = false };
        using var connection = new SqliteConnection(builder.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "VACUUM INTO '" + destination.Replace("'", "''", StringComparison.Ordinal) + "';";
        command.ExecuteNonQuery();
    }

    private static void ValidateDatabase(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;PRAGMA user_version;";
        using var reader = command.ExecuteReader();
        if (!reader.Read() || !string.Equals(reader.GetString(0), "ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("备份数据库未通过完整性检查。");
        if (!reader.NextResult() || !reader.Read() || reader.GetInt32(0) > LibraryRepository.SchemaVersion)
            throw new InvalidDataException("备份数据库版本不受支持。");
    }

    private static ZipArchiveEntry RequiredEntry(ZipArchive zip, string name) =>
        zip.GetEntry(name) ?? throw new InvalidDataException("备份包缺少 " + name + "。");

    private static void ReplaceFile(string source, string destination)
    {
        var temporary = destination + ".restore-tmp";
        try
        {
            File.Copy(source, temporary, true);
            File.Move(temporary, destination, true);
        }
        finally
        {
            DeleteFile(temporary);
        }
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, true);
    }

    private static void DeleteFile(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    private sealed record BackupManifest(int Format, DateTime CreatedUtc, int SchemaVersion);
}
