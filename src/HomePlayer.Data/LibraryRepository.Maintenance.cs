using HomePlayer.Core;
using Microsoft.Data.Sqlite;

namespace HomePlayer.Data;

public sealed record OrphanCleanupResult(int DatabaseRows, int PlaybackStates, int Preferences)
{
    public int Total => DatabaseRows + PlaybackStates + Preferences;
}

public sealed partial class LibraryRepository
{
    public OrphanCleanupResult CleanupOrphanRecords()
    {
        Flush();
        (string Path, bool Preference)[] candidates;
        var libraryRoots = new List<string>();
        var knownPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        lock (_gate)
        {
            candidates = ListStatePaths().ToArray();
            var libraries = ListLibraries();
            libraryRoots.AddRange(libraries.Select(library => library.RootPath));
            foreach (var library in libraries)
            {
                foreach (var item in ListItems(library.Id))
                    knownPaths.Add(item.FullPath(library.RootPath));
            }
        }

        var states = new List<string>();
        var preferences = new List<string>();
        foreach (var (path, preference) in candidates)
        {
            if (knownPaths.Contains(path) || File.Exists(path) || IsUnavailable(path, libraryRoots))
                continue;
            if (preference)
                preferences.Add(path);
            else
                states.Add(path);
        }

        lock (_gate)
        {
            using var tx = _connection.BeginTransaction();
            var databaseRows = CountOrphans("WorkItem", tx) + CountOrphans("Favorite", tx);
            Execute("DELETE FROM WorkItem WHERE NOT EXISTS (SELECT 1 FROM MediaItem m WHERE m.Id = WorkItem.MediaItemId);");
            Execute("DELETE FROM Favorite WHERE NOT EXISTS (SELECT 1 FROM MediaItem m WHERE m.Id = Favorite.MediaItemId);");
            DeletePaths("PlaybackState", states, tx);
            DeletePaths("MediaPreference", preferences, tx);
            tx.Commit();
            return new OrphanCleanupResult(databaseRows, states.Count, preferences.Count);
        }
    }

    public void UpdateLibraryRoot(string libraryId, string newRoot)
    {
        Flush();
        lock (_gate)
        {
            var library = GetLibrary(libraryId) ?? throw new InvalidOperationException("媒体库不存在。");
            var oldPrefix = library.RootPath.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            var newPrefix = newRoot.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            using var tx = _connection.BeginTransaction();
            Execute(
                "UPDATE Library SET RootPath = $root WHERE Id = $id;",
                ("$root", newRoot),
                ("$id", libraryId));
            MigratePaths("PlaybackState", oldPrefix, newPrefix, tx);
            MigratePaths("MediaPreference", oldPrefix, newPrefix, tx);
            tx.Commit();
        }
    }

    private IEnumerable<(string Path, bool Preference)> ListStatePaths()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT Path, 0 FROM PlaybackState
            UNION
            SELECT Path, 1 FROM MediaPreference;
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
            yield return (reader.GetString(0), reader.GetInt32(1) == 1);
    }

    private static bool IsUnavailable(string path, IReadOnlyCollection<string> libraryRoots)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
            return true;
        foreach (var root in libraryRoots)
        {
            var prefix = root.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        var drive = Path.GetPathRoot(path);
        return string.IsNullOrWhiteSpace(drive) || !Directory.Exists(drive);
    }

    private int CountOrphans(string table, SqliteTransaction tx)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = $"SELECT COUNT(*) FROM {table} WHERE NOT EXISTS (SELECT 1 FROM MediaItem m WHERE m.Id = {table}.MediaItemId);";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private void DeletePaths(string table, IReadOnlyCollection<string> paths, SqliteTransaction tx)
    {
        if (paths.Count == 0)
            return;
        using var command = _connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = $"DELETE FROM {table} WHERE Path = $path;";
        var path = command.Parameters.Add("$path", SqliteType.Text);
        foreach (var value in paths)
        {
            path.Value = value;
            command.ExecuteNonQuery();
        }
    }

    private void MigratePaths(string table, string oldPrefix, string newPrefix, SqliteTransaction tx)
    {
        using (var command = _connection.CreateCommand())
        {
            command.Transaction = tx;
            command.CommandText = $"""
                UPDATE OR IGNORE {table}
                SET Path = $newPrefix || substr(Path, $oldLength + 1)
                WHERE substr(Path, 1, $oldLength) = $oldPrefix COLLATE NOCASE;
                """;
            command.Parameters.AddWithValue("$newPrefix", newPrefix);
            command.Parameters.AddWithValue("$oldPrefix", oldPrefix);
            command.Parameters.AddWithValue("$oldLength", oldPrefix.Length);
            command.ExecuteNonQuery();
        }
        using var delete = _connection.CreateCommand();
        delete.Transaction = tx;
        delete.CommandText = $"DELETE FROM {table} WHERE substr(Path, 1, $oldLength) = $oldPrefix COLLATE NOCASE;";
        delete.Parameters.AddWithValue("$oldPrefix", oldPrefix);
        delete.Parameters.AddWithValue("$oldLength", oldPrefix.Length);
        delete.ExecuteNonQuery();
    }
}
