using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace HomePlayer.Data;

public static class CachePathMigration
{
    public static int Rewrite(string databasePath, string oldRoot, string newRoot)
    {
        if (!File.Exists(databasePath))
            return 0;
        oldRoot = Normalize(oldRoot);
        newRoot = Normalize(newRoot);
        if (string.Equals(oldRoot, newRoot, StringComparison.OrdinalIgnoreCase))
            return 0;

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        };
        using var connection = new SqliteConnection(builder.ConnectionString);
        connection.Open();
        if (!HasOldPaths(connection, oldRoot))
            return 0;

        var updated = 0;
        using var transaction = connection.BeginTransaction();

        var items = new List<(string Id, string? Sheet, string Frames, string? Poster)>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT Id, SheetPath, FramePathsJson, PosterPath FROM MediaItem;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                items.Add((
                    reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? "[]" : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3)));
            }
        }

        foreach (var item in items)
        {
            var sheet = Remap(item.Sheet, oldRoot, newRoot);
            var poster = Remap(item.Poster, oldRoot, newRoot);
            var frames = RemapFrames(item.Frames, oldRoot, newRoot);
            if (sheet == item.Sheet && poster == item.Poster && frames == item.Frames)
                continue;
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE MediaItem SET SheetPath = $sheet, FramePathsJson = $frames, PosterPath = $poster WHERE Id = $id;";
            command.Parameters.AddWithValue("$sheet", (object?)sheet ?? DBNull.Value);
            command.Parameters.AddWithValue("$frames", frames);
            command.Parameters.AddWithValue("$poster", (object?)poster ?? DBNull.Value);
            command.Parameters.AddWithValue("$id", item.Id);
            updated += command.ExecuteNonQuery();
        }

        var folders = new List<(string LibraryId, string Path, string Cover)>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT LibraryId, RelativePath, CoverPath FROM Folder WHERE CoverPath IS NOT NULL;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
                folders.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        foreach (var folder in folders)
        {
            var cover = Remap(folder.Cover, oldRoot, newRoot);
            if (cover == folder.Cover)
                continue;
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE Folder SET CoverPath = $cover WHERE LibraryId = $library AND RelativePath = $path;";
            command.Parameters.AddWithValue("$cover", cover);
            command.Parameters.AddWithValue("$library", folder.LibraryId);
            command.Parameters.AddWithValue("$path", folder.Path);
            updated += command.ExecuteNonQuery();
        }

        transaction.Commit();
        return updated;
    }

    private static bool HasOldPaths(SqliteConnection connection, string oldRoot)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1 FROM MediaItem
                WHERE instr(SheetPath, $root) > 0 OR instr(PosterPath, $root) > 0 OR instr(FramePathsJson, $root) > 0
            ) OR EXISTS (
                SELECT 1 FROM Folder WHERE instr(CoverPath, $root) > 0
            );
            """;
        command.Parameters.AddWithValue("$root", oldRoot);
        return Convert.ToInt64(command.ExecuteScalar()) != 0;
    }

    private static string? Remap(string? path, string oldRoot, string newRoot)
    {
        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith(oldRoot, StringComparison.OrdinalIgnoreCase))
            return path;
        var rest = path[oldRoot.Length..];
        if (rest.Length > 0 && rest[0] is not ('\\' or '/'))
            return path;
        return newRoot + rest;
    }

    private static string RemapFrames(string json, string oldRoot, string newRoot)
    {
        string[]? frames;
        try
        {
            frames = JsonSerializer.Deserialize<string[]>(json);
        }
        catch (JsonException)
        {
            return json;
        }

        if (frames == null || frames.Length == 0)
            return json;
        var changed = false;
        for (var i = 0; i < frames.Length; i++)
        {
            var mapped = Remap(frames[i], oldRoot, newRoot);
            if (mapped != frames[i])
            {
                frames[i] = mapped!;
                changed = true;
            }
        }

        return changed ? JsonSerializer.Serialize(frames) : json;
    }

    private static string Normalize(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
