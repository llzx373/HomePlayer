using System.Globalization;
using System.Text.Json;
using HomePlayer.Core;
using Microsoft.Data.Sqlite;

namespace HomePlayer.Data;

public sealed partial class LibraryRepository : IDisposable, IPlaybackStateStore, IMediaPreferenceStore
{
    private readonly SqliteConnection _connection;
    private readonly object _gate = new();
    private readonly object _stateGate = new();
    private readonly Dictionary<string, PlaybackState> _pendingStates = new(StringComparer.OrdinalIgnoreCase);

    public LibraryRepository(string databasePath)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false
        };
        _connection = new SqliteConnection(builder.ConnectionString);
        _connection.Open();
        Migrate();
    }

    public void Dispose() => _connection.Dispose();

    public IReadOnlyList<MediaLibrary> ListLibraries()
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT Id, Name, RootPath, Kind, LastFullScanUtcTicks FROM Library ORDER BY Name;";
            using var reader = command.ExecuteReader();
            var list = new List<MediaLibrary>();
            while (reader.Read())
            {
                list.Add(new MediaLibrary
                {
                    Id = reader.GetString(0),
                    Name = reader.GetString(1),
                    RootPath = reader.GetString(2),
                    Kind = (LibraryKind)reader.GetInt32(3),
                    LastFullScanUtc = reader.IsDBNull(4) ? null : new DateTime(reader.GetInt64(4), DateTimeKind.Utc)
                });
            }

            return list;
        }
    }

    public MediaLibrary? GetLibrary(string id)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT Id, Name, RootPath, Kind, LastFullScanUtcTicks FROM Library WHERE Id = $id;";
            command.Parameters.AddWithValue("$id", id);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
                return null;
            return new MediaLibrary
            {
                Id = reader.GetString(0),
                Name = reader.GetString(1),
                RootPath = reader.GetString(2),
                Kind = (LibraryKind)reader.GetInt32(3),
                LastFullScanUtc = reader.IsDBNull(4) ? null : new DateTime(reader.GetInt64(4), DateTimeKind.Utc)
            };
        }
    }

    public void InsertLibrary(MediaLibrary library)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO Library (Id, Name, RootPath, Kind, LastFullScanUtcTicks)
                VALUES ($id, $name, $root, $kind, NULL);
                """;
            command.Parameters.AddWithValue("$id", library.Id);
            command.Parameters.AddWithValue("$name", library.Name);
            command.Parameters.AddWithValue("$root", library.RootPath);
            command.Parameters.AddWithValue("$kind", (int)library.Kind);
            command.ExecuteNonQuery();
        }
    }

    public void DeleteLibrary(string id)
    {
        lock (_gate)
        {
            using var tx = _connection.BeginTransaction();
            Execute("DELETE FROM WorkItem WHERE MediaItemId IN (SELECT Id FROM MediaItem WHERE LibraryId = $id);", ("$id", id));
            Execute("DELETE FROM MediaTag WHERE MediaItemId IN (SELECT Id FROM MediaItem WHERE LibraryId = $id);", ("$id", id));
            Execute("DELETE FROM MediaCollectionItem WHERE MediaItemId IN (SELECT Id FROM MediaItem WHERE LibraryId = $id);", ("$id", id));
            Execute("DELETE FROM MediaItem WHERE LibraryId = $id;", ("$id", id));
            Execute("DELETE FROM Folder WHERE LibraryId = $id;", ("$id", id));
            Execute("DELETE FROM Library WHERE Id = $id;", ("$id", id));
            tx.Commit();
        }
    }

    public void TouchScan(string id, DateTime utc)
    {
        lock (_gate)
        {
            Execute(
                "UPDATE Library SET LastFullScanUtcTicks = $ticks WHERE Id = $id;",
                ("$ticks", utc.Ticks),
                ("$id", id));
        }
    }

    public IReadOnlyList<MediaItem> ListItems(string libraryId)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT * FROM MediaItem WHERE LibraryId = $id;";
            command.Parameters.AddWithValue("$id", libraryId);
            using var reader = command.ExecuteReader();
            var list = new List<MediaItem>();
            while (reader.Read())
                list.Add(ReadItem(reader));
            return list;
        }
    }

    public IReadOnlyList<MediaItem> ListItemsInDirectory(string libraryId, string relativePath)
    {
        var path = relativePath.Replace('\\', '/').Trim('/');
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT * FROM MediaItem
                WHERE LibraryId = $library
                  AND (replace(RelativePath, '\', '/') = $path COLLATE NOCASE
                       OR replace(RelativePath, '\', '/') LIKE $prefix ESCAPE '\');
                """;
            command.Parameters.AddWithValue("$library", libraryId);
            command.Parameters.AddWithValue("$path", path);
            command.Parameters.AddWithValue("$prefix", EscapeLike(path) + "/%");
            using var reader = command.ExecuteReader();
            var list = new List<MediaItem>();
            while (reader.Read())
                list.Add(ReadItem(reader));
            return list;
        }
    }

    public int CountItems(
        string libraryId,
        string? relativeDir,
        bool recursive,
        string? search,
        LibraryFilter filter) => CountItems(new MediaLibraryQuery
    {
        LibraryId = libraryId,
        RelativeDir = relativeDir,
        Recursive = recursive,
        Search = search,
        Filter = filter
    });

    public int CountItems(MediaLibraryQuery query)
    {
        using var measure = PerformanceMetrics.Measure("library.search-count");
        lock (_gate)
        {
            var sql = BuildItemQuery(query);
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM MediaItem m" + sql.Where + ";";
            AddParameters(command, sql);
            return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
    }

    public IReadOnlyList<MediaItem> ListItemsPage(
        string libraryId,
        string? relativeDir,
        bool recursive,
        string? search,
        LibraryFilter filter,
        SortMode sort,
        int offset,
        int limit) => ListItemsPage(new MediaLibraryQuery
    {
        LibraryId = libraryId,
        RelativeDir = relativeDir,
        Recursive = recursive,
        Search = search,
        Filter = filter
    }, sort, offset, limit);

    public IReadOnlyList<MediaItem> ListItemsPage(MediaLibraryQuery query, SortMode sort, int offset, int limit)
    {
        using var measure = PerformanceMetrics.Measure("library.page");
        if (limit < 1)
            return [];
        if (offset < 0)
            offset = 0;
        lock (_gate)
        {
            var sql = BuildItemQuery(query);
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT m.* FROM MediaItem m" + sql.Where + " ORDER BY " + OrderBy(sort) + " LIMIT $limit OFFSET $offset;";
            AddParameters(command, sql);
            command.Parameters.AddWithValue("$limit", limit);
            command.Parameters.AddWithValue("$offset", offset);
            using var reader = command.ExecuteReader();
            var list = new List<MediaItem>(Math.Min(limit, 256));
            while (reader.Read())
                list.Add(ReadItem(reader));
            return list;
        }
    }

    public IReadOnlyList<string> ListAllPaths(
        string libraryId,
        string? relativeDir,
        bool recursive,
        string? search,
        LibraryFilter filter,
        SortMode sort) => ListAllPaths(new MediaLibraryQuery
    {
        LibraryId = libraryId,
        RelativeDir = relativeDir,
        Recursive = recursive,
        Search = search,
        Filter = filter
    }, sort);

    public IReadOnlyList<string> ListAllPaths(MediaLibraryQuery query, SortMode sort)
    {
        lock (_gate)
        {
            var sql = BuildItemQuery(query);
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT l.RootPath, m.RelativePath FROM MediaItem m
                JOIN Library l ON l.Id = m.LibraryId
                """ + sql.Where + " ORDER BY " + OrderBy(sort) + ";";
            AddParameters(command, sql);
            using var reader = command.ExecuteReader();
            var list = new List<string>();
            while (reader.Read())
                list.Add(Path.Combine(reader.GetString(0), reader.GetString(1).Replace('/', Path.DirectorySeparatorChar)));
            return list;
        }
    }

    public MediaFolder? GetFolder(string libraryId, string relativePath)
    {
        var path = NormalizeDir(relativePath);
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT f.LibraryId, f.RelativePath, f.ParentPath, f.Name, f.DirectCount, f.RecursiveCount,
                       f.CoverPath, f.CoverStamp,
                       EXISTS (SELECT 1 FROM Folder c WHERE c.LibraryId = f.LibraryId AND c.ParentPath = f.RelativePath) AS HasChildren
                FROM Folder f
                WHERE f.LibraryId = $library AND f.RelativePath = $path;
                """;
            command.Parameters.AddWithValue("$library", libraryId);
            command.Parameters.AddWithValue("$path", path);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadFolder(reader) : null;
        }
    }

    public IReadOnlyList<MediaFolder> ListChildFolders(string libraryId, string? parentPath)
    {
        var parent = NormalizeDir(parentPath);
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT f.LibraryId, f.RelativePath, f.ParentPath, f.Name, f.DirectCount, f.RecursiveCount,
                       f.CoverPath, f.CoverStamp,
                       EXISTS (SELECT 1 FROM Folder c WHERE c.LibraryId = f.LibraryId AND c.ParentPath = f.RelativePath) AS HasChildren
                FROM Folder f
                WHERE f.LibraryId = $library AND f.RelativePath <> $parent AND IFNULL(f.ParentPath, '') = $parent
                ORDER BY f.Name COLLATE NOCASE;
                """;
            command.Parameters.AddWithValue("$library", libraryId);
            command.Parameters.AddWithValue("$parent", parent);
            using var reader = command.ExecuteReader();
            var list = new List<MediaFolder>();
            while (reader.Read())
                list.Add(ReadFolder(reader));
            return list;
        }
    }

    public IReadOnlyList<MediaFolder> ListFolders(string libraryId)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT f.LibraryId, f.RelativePath, f.ParentPath, f.Name, f.DirectCount, f.RecursiveCount,
                       f.CoverPath, f.CoverStamp,
                       EXISTS (SELECT 1 FROM Folder c WHERE c.LibraryId = f.LibraryId AND c.ParentPath = f.RelativePath) AS HasChildren
                FROM Folder f
                WHERE f.LibraryId = $library
                ORDER BY f.RelativePath COLLATE NOCASE;
                """;
            command.Parameters.AddWithValue("$library", libraryId);
            using var reader = command.ExecuteReader();
            var list = new List<MediaFolder>();
            while (reader.Read())
                list.Add(ReadFolder(reader));
            return list;
        }
    }

    /// <summary>
    /// 把扫描出的目录树写回 <c>Folder</c> 表，保留已有封面列。返回被删除的目录。
    /// </summary>
    public IReadOnlyList<string> SyncFolders(string libraryId, IReadOnlyList<MediaFolder> folders)
    {
        lock (_gate)
        {
            using var tx = _connection.BeginTransaction();
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var read = _connection.CreateCommand())
            {
                read.Transaction = tx;
                read.CommandText = "SELECT RelativePath FROM Folder WHERE LibraryId = $library;";
                read.Parameters.AddWithValue("$library", libraryId);
                using var reader = read.ExecuteReader();
                while (reader.Read())
                    existing.Add(reader.GetString(0));
            }

            using (var write = _connection.CreateCommand())
            {
                write.Transaction = tx;
                write.CommandText = """
                    INSERT INTO Folder (LibraryId, RelativePath, ParentPath, Name, DirectCount, RecursiveCount)
                    VALUES ($library, $path, $parent, $name, $direct, $recursive)
                    ON CONFLICT(LibraryId, RelativePath) DO UPDATE SET
                        ParentPath = excluded.ParentPath,
                        Name = excluded.Name,
                        DirectCount = excluded.DirectCount,
                        RecursiveCount = excluded.RecursiveCount;
                    """;
                var library = write.Parameters.Add("$library", SqliteType.Text);
                var path = write.Parameters.Add("$path", SqliteType.Text);
                var parent = write.Parameters.Add("$parent", SqliteType.Text);
                var name = write.Parameters.Add("$name", SqliteType.Text);
                var direct = write.Parameters.Add("$direct", SqliteType.Integer);
                var recursive = write.Parameters.Add("$recursive", SqliteType.Integer);
                foreach (var folder in folders)
                {
                    library.Value = libraryId;
                    path.Value = folder.RelativePath;
                    parent.Value = (object?)folder.ParentPath ?? DBNull.Value;
                    name.Value = folder.Name;
                    direct.Value = folder.DirectCount;
                    recursive.Value = folder.RecursiveCount;
                    write.ExecuteNonQuery();
                    existing.Remove(folder.RelativePath);
                }
            }

            var removed = existing.ToList();
            if (removed.Count > 0)
            {
                using var delete = _connection.CreateCommand();
                delete.Transaction = tx;
                delete.CommandText = "DELETE FROM Folder WHERE LibraryId = $library AND RelativePath = $path;";
                var library = delete.Parameters.Add("$library", SqliteType.Text);
                var path = delete.Parameters.Add("$path", SqliteType.Text);
                foreach (var folder in removed)
                {
                    library.Value = libraryId;
                    path.Value = folder;
                    delete.ExecuteNonQuery();
                }
            }

            tx.Commit();
            return removed;
        }
    }

    public void SaveFolderCover(string libraryId, string relativePath, string? coverPath, string? stamp)
    {
        lock (_gate)
        {
            Execute(
                "UPDATE Folder SET CoverPath = $cover, CoverStamp = $stamp WHERE LibraryId = $library AND RelativePath = $path;",
                ("$cover", (object?)coverPath ?? DBNull.Value),
                ("$stamp", (object?)stamp ?? DBNull.Value),
                ("$library", libraryId),
                ("$path", NormalizeDir(relativePath)));
        }
    }

    public void ClearFolderCovers(string libraryId)
    {
        lock (_gate)
        {
            Execute(
                "UPDATE Folder SET CoverPath = NULL, CoverStamp = NULL WHERE LibraryId = $library;",
                ("$library", libraryId));
        }
    }

    public IReadOnlyDictionary<string, PlaybackState> LoadAll(int limit)
    {
        if (limit < 1)
            return new Dictionary<string, PlaybackState>(StringComparer.OrdinalIgnoreCase);
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT Path, PositionSeconds, DurationSeconds, Finished, UpdatedUtcTicks
                FROM PlaybackState
                ORDER BY UpdatedUtcTicks DESC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$limit", limit);
            using var reader = command.ExecuteReader();
            var map = new Dictionary<string, PlaybackState>(StringComparer.OrdinalIgnoreCase);
            while (reader.Read())
            {
                var state = ReadPlaybackState(reader);
                map[state.Path] = state;
            }

            return map;
        }
    }

    public IReadOnlyList<PlaybackState> ListRecentStates(int limit)
    {
        if (limit < 1)
            return [];
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT Path, PositionSeconds, DurationSeconds, Finished, UpdatedUtcTicks
                FROM PlaybackState
                ORDER BY UpdatedUtcTicks DESC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$limit", limit);
            using var reader = command.ExecuteReader();
            var list = new List<PlaybackState>(Math.Min(limit, 128));
            while (reader.Read())
                list.Add(ReadPlaybackState(reader));
            return list;
        }
    }

    public IReadOnlyList<MediaItem> ListRecentlyAdded(int limit)
    {
        if (limit < 1)
            return [];
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT * FROM MediaItem
                ORDER BY AddedUtcTicks DESC, FileName COLLATE NOCASE
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$limit", limit);
            using var reader = command.ExecuteReader();
            var list = new List<MediaItem>(Math.Min(limit, 128));
            while (reader.Read())
                list.Add(ReadItem(reader));
            return list;
        }
    }

    public MediaItem? FindItemByPath(string path)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT m.* FROM MediaItem m
                JOIN Library l ON l.Id = m.LibraryId
                WHERE (l.RootPath || '\' || replace(m.RelativePath, '/', '\')) = $path COLLATE NOCASE
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("$path", path);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadItem(reader) : null;
        }
    }

    public IReadOnlyDictionary<string, PlaybackState> LoadPlaybackStates(IReadOnlyCollection<string> paths)
    {
        using var measure = PerformanceMetrics.Measure("library.progress-load");
        var map = new Dictionary<string, PlaybackState>(StringComparer.OrdinalIgnoreCase);
        if (paths.Count == 0)
            return map;
        var values = paths as IList<string> ?? [.. paths];
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            for (var offset = 0; offset < values.Count; offset += MaxQueryIds)
            {
                var count = Math.Min(MaxQueryIds, values.Count - offset);
                command.Parameters.Clear();
                var names = new string[count];
                for (var i = 0; i < count; i++)
                {
                    names[i] = "$path" + i;
                    command.Parameters.AddWithValue(names[i], values[offset + i]);
                }

                command.CommandText =
                    "SELECT Path, PositionSeconds, DurationSeconds, Finished, UpdatedUtcTicks FROM PlaybackState WHERE Path IN ("
                    + string.Join(",", names) + ");";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var state = ReadPlaybackState(reader);
                    map[state.Path] = state;
                }
            }
        }

        return map;
    }

    public PlaybackState? Load(string path) => LoadPlaybackStates([path]).GetValueOrDefault(path);

    public void Save(PlaybackState state)
    {
        lock (_stateGate)
            _pendingStates[state.Path] = state;
    }

    public void Flush()
    {
        PlaybackState[] pending;
        lock (_stateGate)
        {
            if (_pendingStates.Count == 0)
                return;
            pending = [.. _pendingStates.Values];
            _pendingStates.Clear();
        }

        lock (_gate)
        {
            using var tx = _connection.BeginTransaction();
            using var command = _connection.CreateCommand();
            command.Transaction = tx;
            command.CommandText = """
                INSERT INTO PlaybackState (Path, PositionSeconds, DurationSeconds, Finished, UpdatedUtcTicks)
                VALUES ($path, $position, $duration, $finished, $updated)
                ON CONFLICT(Path) DO UPDATE SET
                    PositionSeconds = excluded.PositionSeconds,
                    DurationSeconds = excluded.DurationSeconds,
                    Finished = excluded.Finished,
                    UpdatedUtcTicks = excluded.UpdatedUtcTicks;
                """;
            var path = command.Parameters.Add("$path", SqliteType.Text);
            var position = command.Parameters.Add("$position", SqliteType.Real);
            var duration = command.Parameters.Add("$duration", SqliteType.Real);
            var finished = command.Parameters.Add("$finished", SqliteType.Integer);
            var updated = command.Parameters.Add("$updated", SqliteType.Integer);
            foreach (var state in pending)
            {
                path.Value = state.Path;
                position.Value = state.PositionSeconds;
                duration.Value = state.DurationSeconds;
                finished.Value = state.Finished ? 1 : 0;
                updated.Value = state.UpdatedUtcTicks;
                command.ExecuteNonQuery();
            }

            tx.Commit();
        }
    }

    public void DeletePlaybackState(string path)
    {
        lock (_stateGate)
            _pendingStates.Remove(path);
        lock (_gate)
            Execute("DELETE FROM PlaybackState WHERE Path = $path;", ("$path", path));
    }

    public void ClearPlaybackStates()
    {
        lock (_stateGate)
            _pendingStates.Clear();
        lock (_gate)
            Execute("DELETE FROM PlaybackState;");
    }

    public MediaPreference? LoadPreference(string path)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT * FROM MediaPreference WHERE Path = $path;";
            command.Parameters.AddWithValue("$path", path);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
                return null;
            return new MediaPreference(
                reader.GetString(reader.GetOrdinal("Path")),
                reader.IsDBNull(reader.GetOrdinal("AudioTrackIndex")) ? null : reader.GetInt32(reader.GetOrdinal("AudioTrackIndex")),
                reader.IsDBNull(reader.GetOrdinal("AudioTrackTitle")) ? null : reader.GetString(reader.GetOrdinal("AudioTrackTitle")),
                reader.IsDBNull(reader.GetOrdinal("AudioTrackLanguage")) ? null : reader.GetString(reader.GetOrdinal("AudioTrackLanguage")),
                (SubtitlePreferenceMode)reader.GetInt32(reader.GetOrdinal("SubtitleMode")),
                reader.IsDBNull(reader.GetOrdinal("SubtitleTrackIndex")) ? null : reader.GetInt32(reader.GetOrdinal("SubtitleTrackIndex")),
                reader.IsDBNull(reader.GetOrdinal("SubtitleTrackTitle")) ? null : reader.GetString(reader.GetOrdinal("SubtitleTrackTitle")),
                reader.IsDBNull(reader.GetOrdinal("SubtitleTrackLanguage")) ? null : reader.GetString(reader.GetOrdinal("SubtitleTrackLanguage")),
                reader.IsDBNull(reader.GetOrdinal("SubtitlePath")) ? null : reader.GetString(reader.GetOrdinal("SubtitlePath")),
                reader.GetInt32(reader.GetOrdinal("SubtitleDelayMs")),
                reader.IsDBNull(reader.GetOrdinal("Volume")) ? null : reader.GetDouble(reader.GetOrdinal("Volume")),
                reader.IsDBNull(reader.GetOrdinal("PlaybackRate")) ? null : reader.GetDouble(reader.GetOrdinal("PlaybackRate")),
                reader.GetInt32(reader.GetOrdinal("AudioDelayMs")));
        }
    }

    public void SavePreference(MediaPreference preference)
    {
        lock (_gate)
        {
            Execute(
                """
                INSERT INTO MediaPreference (
                    Path, AudioTrackIndex, AudioTrackTitle, AudioTrackLanguage,
                    SubtitleMode, SubtitleTrackIndex, SubtitleTrackTitle, SubtitleTrackLanguage,
                    SubtitlePath, SubtitleDelayMs, Volume, PlaybackRate, AudioDelayMs)
                VALUES ($path, $audioIndex, $audioTitle, $audioLanguage,
                    $subtitleMode, $subtitleIndex, $subtitleTitle, $subtitleLanguage,
                    $subtitlePath, $subtitleDelay, $volume, $rate, $audioDelay)
                ON CONFLICT(Path) DO UPDATE SET
                    AudioTrackIndex = excluded.AudioTrackIndex,
                    AudioTrackTitle = excluded.AudioTrackTitle,
                    AudioTrackLanguage = excluded.AudioTrackLanguage,
                    SubtitleMode = excluded.SubtitleMode,
                    SubtitleTrackIndex = excluded.SubtitleTrackIndex,
                    SubtitleTrackTitle = excluded.SubtitleTrackTitle,
                    SubtitleTrackLanguage = excluded.SubtitleTrackLanguage,
                    SubtitlePath = excluded.SubtitlePath,
                    SubtitleDelayMs = excluded.SubtitleDelayMs,
                    Volume = excluded.Volume,
                    PlaybackRate = excluded.PlaybackRate,
                    AudioDelayMs = excluded.AudioDelayMs;
                """,
                ("$path", preference.Path),
                ("$audioIndex", (object?)preference.AudioTrackIndex ?? DBNull.Value),
                ("$audioTitle", (object?)preference.AudioTrackTitle ?? DBNull.Value),
                ("$audioLanguage", (object?)preference.AudioTrackLanguage ?? DBNull.Value),
                ("$subtitleMode", (int)preference.SubtitleMode),
                ("$subtitleIndex", (object?)preference.SubtitleTrackIndex ?? DBNull.Value),
                ("$subtitleTitle", (object?)preference.SubtitleTrackTitle ?? DBNull.Value),
                ("$subtitleLanguage", (object?)preference.SubtitleTrackLanguage ?? DBNull.Value),
                ("$subtitlePath", (object?)preference.SubtitlePath ?? DBNull.Value),
                ("$subtitleDelay", preference.SubtitleDelayMs),
                ("$volume", (object?)preference.Volume ?? DBNull.Value),
                ("$rate", (object?)preference.PlaybackRate ?? DBNull.Value),
                ("$audioDelay", preference.AudioDelayMs));
        }
    }

    private static PlaybackState ReadPlaybackState(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetDouble(1),
        reader.GetDouble(2),
        reader.GetInt32(3) != 0,
        reader.GetInt64(4));

    private static MediaFolder ReadFolder(SqliteDataReader reader) => new()
    {
        LibraryId = reader.GetString(0),
        RelativePath = reader.GetString(1),
        ParentPath = reader.IsDBNull(2) ? null : reader.GetString(2),
        Name = reader.GetString(3),
        DirectCount = reader.GetInt32(4),
        RecursiveCount = reader.GetInt32(5),
        CoverPath = reader.IsDBNull(6) ? null : reader.GetString(6),
        CoverStamp = reader.IsDBNull(7) ? null : reader.GetString(7),
        HasChildren = reader.GetInt32(8) != 0
    };

    private sealed record ItemQuery(string Where, IReadOnlyList<(string Name, object Value)> Parameters);

    private static ItemQuery BuildItemQuery(MediaLibraryQuery query)
    {
        var clauses = new List<string>();
        var parameters = new List<(string Name, object Value)>();
        if (!string.IsNullOrWhiteSpace(query.LibraryId))
        {
            clauses.Add("m.LibraryId = $library");
            parameters.Add(("$library", query.LibraryId));
        }

        var dir = NormalizeDir(query.RelativeDir);
        if (query.LibraryId != null)
        {
            if (dir.Length == 0)
            {
                if (!query.Recursive)
                    clauses.Add("m.RelativeDir = ''");
            }
            else if (query.Recursive)
            {
                clauses.Add("(m.RelativeDir = $dir OR m.RelativeDir LIKE $prefix ESCAPE '\\')");
                parameters.Add(("$dir", dir));
                parameters.Add(("$prefix", EscapeLike(dir) + "/%"));
            }
            else
            {
                clauses.Add("m.RelativeDir = $dir");
                parameters.Add(("$dir", dir));
            }
        }

        var term = query.Search?.Trim() ?? "";
        if (term.Length > 0)
        {
            clauses.Add("(COALESCE(m.Title, '') LIKE $search ESCAPE '\\' OR m.FileName LIKE $search ESCAPE '\\')");
            parameters.Add(("$search", "%" + EscapeLike(term) + "%"));
        }

        switch (query.Filter)
        {
            case LibraryFilter.Failed:
                clauses.Add("m.Status = " + (int)ItemStatus.Failed);
                break;
            case LibraryFilter.NeedsReview:
                clauses.Add("m.MatchStatus = " + (int)MatchStatus.NeedsReview);
                break;
        }

        const string statePath = "(SELECT l.RootPath || '\\' || replace(m.RelativePath, '/', '\\') FROM Library l WHERE l.Id = m.LibraryId)";
        var watch = query.Watch == WatchFilter.All && query.Filter == LibraryFilter.Unwatched
            ? WatchFilter.Unwatched
            : query.Watch;
        switch (watch)
        {
            case WatchFilter.Unwatched:
                clauses.Add($"NOT EXISTS (SELECT 1 FROM PlaybackState ps WHERE ps.Path = {statePath} AND (ps.Finished = 1 OR ps.PositionSeconds >= 5))");
                break;
            case WatchFilter.InProgress:
                clauses.Add($"EXISTS (SELECT 1 FROM PlaybackState ps WHERE ps.Path = {statePath} AND ps.Finished = 0 AND ps.PositionSeconds >= 5 AND ps.DurationSeconds - ps.PositionSeconds > 5)");
                break;
            case WatchFilter.Watched:
                clauses.Add($"EXISTS (SELECT 1 FROM PlaybackState ps WHERE ps.Path = {statePath} AND ps.Finished = 1)");
                break;
        }

        switch (query.MediaType)
        {
            case MediaTypeFilter.Movie:
                clauses.Add("m.MediaType = 'movie'");
                break;
            case MediaTypeFilter.Episode:
                clauses.Add("(m.MediaType = 'tv' OR m.Season IS NOT NULL OR m.Episode IS NOT NULL)");
                break;
            case MediaTypeFilter.Unknown:
                clauses.Add("(m.MediaType IS NULL OR m.MediaType NOT IN ('movie', 'tv'))");
                break;
        }

        switch (query.Resolution)
        {
            case ResolutionFilter.UltraHd:
                clauses.Add("(m.Width >= 3800 OR m.Height >= 2000)");
                break;
            case ResolutionFilter.FullHd:
                clauses.Add("(m.Width >= 1900 OR m.Height >= 1000) AND NOT (m.Width >= 3800 OR m.Height >= 2000)");
                break;
            case ResolutionFilter.Hd:
                clauses.Add("(m.Width >= 1280 OR m.Height >= 700) AND m.Width < 1900 AND m.Height < 1000");
                break;
            case ResolutionFilter.StandardDefinition:
                clauses.Add("m.Width IS NOT NULL AND m.Height IS NOT NULL AND m.Width < 1280 AND m.Height < 700");
                break;
            case ResolutionFilter.Unknown:
                clauses.Add("(m.Width IS NULL OR m.Height IS NULL)");
                break;
        }

        const string h264 = "lower(COALESCE(m.VideoCodec, '')) IN ('h264', 'avc1')";
        const string h265 = "lower(COALESCE(m.VideoCodec, '')) IN ('h265', 'hevc')";
        const string av1 = "lower(COALESCE(m.VideoCodec, '')) = 'av1'";
        const string vp9 = "lower(COALESCE(m.VideoCodec, '')) = 'vp9'";
        switch (query.Codec)
        {
            case CodecFilter.H264:
                clauses.Add(h264);
                break;
            case CodecFilter.H265:
                clauses.Add(h265);
                break;
            case CodecFilter.Av1:
                clauses.Add(av1);
                break;
            case CodecFilter.Vp9:
                clauses.Add(vp9);
                break;
            case CodecFilter.Other:
                clauses.Add($"m.VideoCodec IS NOT NULL AND NOT ({h264} OR {h265} OR {av1} OR {vp9})");
                break;
        }

        switch (query.Duration)
        {
            case DurationFilter.Short:
                clauses.Add("m.DurationSeconds > 0 AND m.DurationSeconds < 1200");
                break;
            case DurationFilter.Medium:
                clauses.Add("m.DurationSeconds >= 1200 AND m.DurationSeconds <= 3600");
                break;
            case DurationFilter.Long:
                clauses.Add("m.DurationSeconds > 3600");
                break;
            case DurationFilter.Unknown:
                clauses.Add("(m.DurationSeconds IS NULL OR m.DurationSeconds <= 0)");
                break;
        }

        if (query.Year is int year)
        {
            clauses.Add("m.Year = $year");
            parameters.Add(("$year", year));
        }
        if (query.Subtitles == PresenceFilter.Present)
            clauses.Add("m.SubtitleStreamCount > 0");
        else if (query.Subtitles == PresenceFilter.Missing)
            clauses.Add("m.SubtitleStreamCount = 0");
        if (query.MultipleAudioTracks)
            clauses.Add("m.AudioStreamCount > 1");
        if (query.FavoritesOnly)
            clauses.Add("EXISTS (SELECT 1 FROM Favorite f WHERE f.MediaItemId = m.Id)");
        if (!string.IsNullOrWhiteSpace(query.Tag))
        {
            clauses.Add("EXISTS (SELECT 1 FROM MediaTag mt JOIN Tag t ON t.Id = mt.TagId WHERE mt.MediaItemId = m.Id AND t.Name = $tag COLLATE NOCASE)");
            parameters.Add(("$tag", query.Tag));
        }
        if (!string.IsNullOrWhiteSpace(query.CollectionId))
        {
            clauses.Add("EXISTS (SELECT 1 FROM MediaCollectionItem ci WHERE ci.MediaItemId = m.Id AND ci.CollectionId = $collection)");
            parameters.Add(("$collection", query.CollectionId));
        }
        if (!string.IsNullOrWhiteSpace(query.AdditionalSearch))
        {
            clauses.Add("(COALESCE(m.Title, '') LIKE $additionalSearch ESCAPE '\\' OR m.FileName LIKE $additionalSearch ESCAPE '\\')");
            parameters.Add(("$additionalSearch", "%" + EscapeLike(query.AdditionalSearch.Trim()) + "%"));
        }
        if (!string.IsNullOrWhiteSpace(query.AdditionalTag))
        {
            clauses.Add("EXISTS (SELECT 1 FROM MediaTag mt JOIN Tag t ON t.Id = mt.TagId WHERE mt.MediaItemId = m.Id AND t.Name = $additionalTag COLLATE NOCASE)");
            parameters.Add(("$additionalTag", query.AdditionalTag));
        }

        return new ItemQuery(clauses.Count == 0 ? "" : " WHERE " + string.Join(" AND ", clauses), parameters);
    }

    private static void AddParameters(SqliteCommand command, ItemQuery query)
    {
        foreach (var (name, value) in query.Parameters)
            command.Parameters.AddWithValue(name, value);
    }

    private static string OrderBy(SortMode sort) => sort switch
    {
        SortMode.FileName => "m.FileName COLLATE NOCASE ASC, m.RelativePath COLLATE NOCASE ASC",
        SortMode.ModifiedDesc => "m.MtimeUtcTicks DESC, m.RelativePath COLLATE NOCASE ASC",
        SortMode.Title => "COALESCE(NULLIF(m.Title, ''), m.FileName) COLLATE NOCASE ASC, m.RelativePath COLLATE NOCASE ASC",
        SortMode.YearDesc => "m.Year IS NULL, m.Year DESC, COALESCE(NULLIF(m.Title, ''), m.FileName) COLLATE NOCASE ASC, m.RelativePath COLLATE NOCASE ASC",
        SortMode.AddedDesc => "m.AddedUtcTicks DESC, m.RelativePath COLLATE NOCASE ASC",
        SortMode.Episode => "COALESCE(NULLIF(m.Title, ''), m.FileName) COLLATE NOCASE ASC, m.Season IS NULL, m.Season ASC, m.Episode IS NULL, m.Episode ASC, m.FileName COLLATE NOCASE ASC",
        _ => "m.RelativePath COLLATE NOCASE ASC"
    };

    private static string NormalizeDir(string? relativeDir) =>
        (relativeDir ?? "").Replace('\\', '/').Trim('/');

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    public MediaItem? GetItem(string id)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT * FROM MediaItem WHERE Id = $id;";
            command.Parameters.AddWithValue("$id", id);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadItem(reader) : null;
        }
    }

    /// <summary>
    /// 批量读取条目，避免扫描时对每个刷新项各发一条 SQL。
    /// </summary>
    public IReadOnlyList<MediaItem> GetItems(IReadOnlyCollection<string> ids)
    {
        if (ids.Count == 0)
            return [];

        var list = new List<MediaItem>(ids.Count);
        var values = ids as IList<string> ?? [.. ids];
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            for (var offset = 0; offset < values.Count; offset += MaxQueryIds)
            {
                var count = Math.Min(MaxQueryIds, values.Count - offset);
                command.Parameters.Clear();
                var names = new string[count];
                for (var i = 0; i < count; i++)
                {
                    names[i] = "$id" + i;
                    command.Parameters.AddWithValue(names[i], values[offset + i]);
                }

                command.CommandText = "SELECT * FROM MediaItem WHERE Id IN (" + string.Join(",", names) + ");";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                    list.Add(ReadItem(reader));
            }
        }

        return list;
    }

    public bool IsFavorite(string itemId)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT EXISTS (SELECT 1 FROM Favorite WHERE MediaItemId = $id);";
            command.Parameters.AddWithValue("$id", itemId);
            return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
        }
    }

    public IReadOnlyDictionary<string, bool> FavoriteStates(IReadOnlyCollection<string> itemIds)
    {
        var result = itemIds.Distinct(StringComparer.Ordinal).ToDictionary(id => id, _ => false, StringComparer.Ordinal);
        if (result.Count == 0)
            return result;
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            var ids = result.Keys.ToList();
            for (var offset = 0; offset < ids.Count; offset += MaxQueryIds)
            {
                command.Parameters.Clear();
                var names = new List<string>();
                foreach (var id in ids.Skip(offset).Take(MaxQueryIds))
                {
                    var name = "$id" + names.Count;
                    names.Add(name);
                    command.Parameters.AddWithValue(name, id);
                }
                command.CommandText = "SELECT MediaItemId FROM Favorite WHERE MediaItemId IN (" + string.Join(',', names) + ");";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                    result[reader.GetString(0)] = true;
            }
        }
        return result;
    }

    public void SetFavorite(string itemId, bool favorite) => SetFavorites([itemId], favorite);

    public void SetFavorites(IReadOnlyCollection<string> itemIds, bool favorite)
    {
        if (itemIds.Count == 0)
            return;
        lock (_gate)
        {
            using var tx = _connection.BeginTransaction();
            using var command = _connection.CreateCommand();
            command.Transaction = tx;
            command.CommandText = favorite
                ? "INSERT INTO Favorite (MediaItemId, AddedUtcTicks) VALUES ($id, $added) ON CONFLICT(MediaItemId) DO NOTHING;"
                : "DELETE FROM Favorite WHERE MediaItemId = $id;";
            var id = command.Parameters.Add("$id", SqliteType.Text);
            if (favorite)
                command.Parameters.AddWithValue("$added", DateTime.UtcNow.Ticks);
            foreach (var itemId in itemIds.Distinct(StringComparer.Ordinal))
            {
                id.Value = itemId;
                command.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    public void DeleteItems(IReadOnlyCollection<string> itemIds)
    {
        if (itemIds.Count == 0)
            return;
        lock (_gate)
        {
            using var tx = _connection.BeginTransaction();
            foreach (var id in itemIds.Distinct(StringComparer.Ordinal))
            {
                Execute("DELETE FROM WorkItem WHERE MediaItemId = $id;", ("$id", id));
                Execute("DELETE FROM Favorite WHERE MediaItemId = $id;", ("$id", id));
                Execute("DELETE FROM MediaTag WHERE MediaItemId = $id;", ("$id", id));
                Execute("DELETE FROM MediaCollectionItem WHERE MediaItemId = $id;", ("$id", id));
                Execute("""
                    DELETE FROM PlaybackState WHERE Path IN (
                        SELECT l.RootPath || '\' || replace(m.RelativePath, '/', '\')
                        FROM MediaItem m JOIN Library l ON l.Id = m.LibraryId WHERE m.Id = $id);
                    """, ("$id", id));
                Execute("DELETE FROM MediaItem WHERE Id = $id;", ("$id", id));
            }
            tx.Commit();
        }
    }

    private const int MaxQueryIds = 400;

    public void InsertItem(MediaItem item)
    {
        lock (_gate)
        {
            var split = RelativePaths.Split(item.RelativePath);
            item.AddedUtcTicks = item.AddedUtcTicks > 0 ? item.AddedUtcTicks : DateTime.UtcNow.Ticks;
            var parsed = FilenameParser.Parse(split.FileName);
            item.Season ??= parsed.Season;
            item.Episode ??= parsed.Episode;
            if (parsed.IsTv)
                item.MediaType = "tv";
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO MediaItem (
                    Id, LibraryId, RelativePath, RelativeDir, FileName, AddedUtcTicks,
                    Size, MtimeUtcTicks, DurationSeconds, Width, Height, VideoCodec,
                    Status, Error, SheetPath, FramePathsJson, MediaType, TmdbId, Season, Episode, Title, OriginalTitle,
                    Year, Overview, Rating, PosterPath, MatchStatus)
                VALUES (
                    $id, $library, $path, $dir, $name, $added,
                    $size, $mtime, NULL, NULL, NULL, NULL,
                    $status, NULL, NULL, '[]', $type, NULL, $season, $episode, NULL, NULL,
                    NULL, NULL, NULL, NULL, $match);
                """;
            command.Parameters.AddWithValue("$id", item.Id);
            command.Parameters.AddWithValue("$library", item.LibraryId);
            command.Parameters.AddWithValue("$path", item.RelativePath);
            command.Parameters.AddWithValue("$dir", split.Directory);
            command.Parameters.AddWithValue("$name", split.FileName);
            command.Parameters.AddWithValue("$added", item.AddedUtcTicks);
            command.Parameters.AddWithValue("$size", item.Size);
            command.Parameters.AddWithValue("$mtime", item.MtimeUtcTicks);
            command.Parameters.AddWithValue("$status", (int)item.Status);
            command.Parameters.AddWithValue("$type", (object?)item.MediaType ?? DBNull.Value);
            command.Parameters.AddWithValue("$season", (object?)item.Season ?? DBNull.Value);
            command.Parameters.AddWithValue("$episode", (object?)item.Episode ?? DBNull.Value);
            command.Parameters.AddWithValue("$match", (int)item.MatchStatus);
            command.ExecuteNonQuery();
        }
    }

    public void MarkChanged(MediaItem item, long size, long mtimeTicks)
    {
        lock (_gate)
        {
            ReprocessItem(item, size, mtimeTicks);
            Execute(
                """
                DELETE FROM PlaybackState WHERE Path = (
                    SELECT l.RootPath || '\' || replace($relative, '/', '\')
                    FROM Library l WHERE l.Id = $library);
                """,
                ("$relative", item.RelativePath),
                ("$library", item.LibraryId));
        }
    }

    public void ReprocessItem(MediaItem item, long size, long mtimeTicks)
    {
        lock (_gate)
        {
            Execute(
                """
                UPDATE MediaItem
                SET Size = $size, MtimeUtcTicks = $mtime, DurationSeconds = NULL, Width = NULL, Height = NULL,
                    VideoCodec = NULL, AudioCodec = NULL, AudioStreamCount = 0, SubtitleStreamCount = 0,
                    Status = $pending, Error = NULL, SheetPath = NULL, FramePathsJson = '[]',
                    MediaType = NULL, TmdbId = NULL, Season = NULL, Episode = NULL, Title = NULL, OriginalTitle = NULL,
                    Year = NULL, Overview = NULL, Rating = NULL, PosterPath = NULL, CollectionName = NULL,
                    MetadataEdited = 0, MatchStatus = $none
                WHERE Id = $id;
                """,
                ("$size", size),
                ("$mtime", mtimeTicks),
                ("$pending", (int)ItemStatus.Pending),
                ("$none", (int)MatchStatus.None),
                ("$id", item.Id));
        }
    }

    public void DeleteItem(string id)
    {
        lock (_gate)
        {
            using var tx = _connection.BeginTransaction();
            Execute("DELETE FROM WorkItem WHERE MediaItemId = $id;", ("$id", id));
            Execute("DELETE FROM Favorite WHERE MediaItemId = $id;", ("$id", id));
            Execute("DELETE FROM MediaTag WHERE MediaItemId = $id;", ("$id", id));
            Execute("DELETE FROM MediaCollectionItem WHERE MediaItemId = $id;", ("$id", id));
            Execute(
                """
                DELETE FROM PlaybackState WHERE Path IN (
                    SELECT l.RootPath || '\' || replace(m.RelativePath, '/', '\')
                    FROM MediaItem m JOIN Library l ON l.Id = m.LibraryId
                    WHERE m.Id = $id);
                """,
                ("$id", id));
            Execute("DELETE FROM MediaItem WHERE Id = $id;", ("$id", id));
            tx.Commit();
        }
    }

    public void SaveProbe(string id, MediaProbe probe)
    {
        lock (_gate)
        {
            Execute(
                """
                UPDATE MediaItem
                SET DurationSeconds = $duration, Width = $width, Height = $height, VideoCodec = $codec,
                    AudioCodec = $audioCodec, AudioStreamCount = $audioCount, SubtitleStreamCount = $subtitleCount
                WHERE Id = $id;
                """,
                ("$duration", probe.DurationSeconds),
                ("$width", probe.Width),
                ("$height", probe.Height),
                ("$codec", (object?)probe.VideoCodec ?? DBNull.Value),
                ("$audioCodec", (object?)probe.AudioCodec ?? DBNull.Value),
                ("$audioCount", probe.AudioStreamCount),
                ("$subtitleCount", probe.SubtitleStreamCount),
                ("$id", id));
        }
    }

    public void SaveThumbs(string id, IReadOnlyList<string> frames, string sheetPath)
    {
        lock (_gate)
        {
            Execute(
                "UPDATE MediaItem SET FramePathsJson = $frames, SheetPath = $sheet, Status = $ready, Error = NULL WHERE Id = $id;",
                ("$frames", JsonSerializer.Serialize(frames)),
                ("$sheet", sheetPath),
                ("$ready", (int)ItemStatus.Ready),
                ("$id", id));
        }
    }

    public void SaveMetadata(MediaItem item)
    {
        lock (_gate)
        {
            Execute(
                """
                UPDATE MediaItem
                SET MediaType = $type, TmdbId = $tmdb, Season = $season, Episode = $episode, Title = $title,
                    OriginalTitle = $original, Year = $year, Overview = $overview, Rating = $rating,
                    PosterPath = $poster, CollectionName = $collection, MetadataEdited = $edited,
                    MatchStatus = $match, Status = $status, Error = $error
                WHERE Id = $id;
                """,
                ("$type", (object?)item.MediaType ?? DBNull.Value),
                ("$tmdb", (object?)item.TmdbId ?? DBNull.Value),
                ("$season", (object?)item.Season ?? DBNull.Value),
                ("$episode", (object?)item.Episode ?? DBNull.Value),
                ("$title", (object?)item.Title ?? DBNull.Value),
                ("$original", (object?)item.OriginalTitle ?? DBNull.Value),
                ("$year", (object?)item.Year ?? DBNull.Value),
                ("$overview", (object?)item.Overview ?? DBNull.Value),
                ("$rating", (object?)item.Rating ?? DBNull.Value),
                ("$poster", (object?)item.PosterPath ?? DBNull.Value),
                ("$collection", (object?)item.CollectionName ?? DBNull.Value),
                ("$edited", item.MetadataEdited ? 1 : 0),
                ("$match", (int)item.MatchStatus),
                ("$status", (int)item.Status),
                ("$error", (object?)item.Error ?? DBNull.Value),
                ("$id", item.Id));
        }
    }

    public void SetStatus(string id, ItemStatus status, string? error)
    {
        lock (_gate)
        {
            Execute(
                "UPDATE MediaItem SET Status = $status, Error = $error WHERE Id = $id;",
                ("$status", (int)status),
                ("$error", (object?)error ?? DBNull.Value),
                ("$id", id));
        }
    }

    public void Enqueue(string itemId, LibraryKind kind)
    {
        lock (_gate)
        {
            Execute(
                """
                INSERT INTO WorkItem (MediaItemId, Kind, State, UpdatedUtc)
                VALUES ($id, $kind, 0, $utc)
                ON CONFLICT(MediaItemId) DO UPDATE SET Kind = $kind, State = 0, UpdatedUtc = $utc;
                """,
                ("$id", itemId),
                ("$kind", (int)kind),
                ("$utc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)));
        }
    }

    public IReadOnlyList<string> ListQueued()
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT MediaItemId FROM WorkItem WHERE State IN (0, 1) ORDER BY UpdatedUtc;";
            using var reader = command.ExecuteReader();
            var list = new List<string>();
            while (reader.Read())
                list.Add(reader.GetString(0));
            return list;
        }
    }

    public void MarkWork(string itemId, int state)
    {
        lock (_gate)
        {
            if (state == 2)
            {
                Execute("DELETE FROM WorkItem WHERE MediaItemId = $id;", ("$id", itemId));
                return;
            }

            Execute(
                "UPDATE WorkItem SET State = $state, UpdatedUtc = $utc WHERE MediaItemId = $id;",
                ("$state", state),
                ("$utc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)),
                ("$id", itemId));
        }
    }

    public int PendingCount()
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM WorkItem WHERE State IN (0, 1);";
            return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
    }

    public string? ReadCache(string key, TimeSpan? maxAge = null)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT Payload, StoredUtc FROM TmdbCache WHERE Key = $key;";
            command.Parameters.AddWithValue("$key", key);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
                return null;
            var payload = reader.GetString(0);
            if (maxAge is TimeSpan age
                && DateTimeOffset.TryParse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var stored)
                && DateTimeOffset.UtcNow - stored > age)
                return null;
            return payload;
        }
    }

    public void WriteCache(string key, string payload)
    {
        lock (_gate)
        {
            Execute(
                """
                INSERT INTO TmdbCache (Key, Payload, StoredUtc) VALUES ($key, $payload, $utc)
                ON CONFLICT(Key) DO UPDATE SET Payload = $payload, StoredUtc = $utc;
                """,
                ("$key", key),
                ("$payload", payload),
                ("$utc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)));
        }
    }

    public const int SchemaVersion = 8;

    private void Migrate()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS Library (
                Id TEXT PRIMARY KEY,
                Name TEXT NOT NULL,
                RootPath TEXT NOT NULL,
                Kind INTEGER NOT NULL,
                LastFullScanUtcTicks INTEGER NULL
            );
            CREATE TABLE IF NOT EXISTS MediaItem (
                Id TEXT PRIMARY KEY,
                LibraryId TEXT NOT NULL,
                RelativePath TEXT NOT NULL,
                Size INTEGER NOT NULL,
                MtimeUtcTicks INTEGER NOT NULL,
                DurationSeconds REAL NULL,
                Width INTEGER NULL,
                Height INTEGER NULL,
                VideoCodec TEXT NULL,
                Status INTEGER NOT NULL,
                Error TEXT NULL,
                SheetPath TEXT NULL,
                FramePathsJson TEXT NOT NULL DEFAULT '[]',
                MediaType TEXT NULL,
                TmdbId INTEGER NULL,
                Season INTEGER NULL,
                Episode INTEGER NULL,
                Title TEXT NULL,
                OriginalTitle TEXT NULL,
                Year INTEGER NULL,
                Overview TEXT NULL,
                Rating REAL NULL,
                PosterPath TEXT NULL,
                MatchStatus INTEGER NOT NULL DEFAULT 0,
                UNIQUE(LibraryId, RelativePath)
            );
            CREATE TABLE IF NOT EXISTS WorkItem (
                MediaItemId TEXT PRIMARY KEY,
                Kind INTEGER NOT NULL,
                State INTEGER NOT NULL,
                UpdatedUtc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS TmdbCache (
                Key TEXT PRIMARY KEY,
                Payload TEXT NOT NULL,
                StoredUtc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS PlaybackState (
                Path TEXT PRIMARY KEY COLLATE NOCASE,
                PositionSeconds REAL NOT NULL,
                DurationSeconds REAL NOT NULL,
                Finished INTEGER NOT NULL,
                UpdatedUtcTicks INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS MediaPreference (
                Path TEXT PRIMARY KEY COLLATE NOCASE,
                AudioTrackIndex INTEGER NULL,
                AudioTrackTitle TEXT NULL,
                AudioTrackLanguage TEXT NULL,
                SubtitleMode INTEGER NOT NULL DEFAULT 0,
                SubtitleTrackIndex INTEGER NULL,
                SubtitleTrackTitle TEXT NULL,
                SubtitleTrackLanguage TEXT NULL,
                SubtitlePath TEXT NULL,
                SubtitleDelayMs INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS Folder (
                LibraryId TEXT NOT NULL,
                RelativePath TEXT NOT NULL,
                ParentPath TEXT,
                Name TEXT NOT NULL,
                DirectCount INTEGER NOT NULL,
                RecursiveCount INTEGER NOT NULL,
                CoverPath TEXT,
                CoverStamp TEXT,
                PRIMARY KEY (LibraryId, RelativePath)
            );
            CREATE INDEX IF NOT EXISTS IX_Folder_Parent ON Folder(LibraryId, ParentPath);
            """;
        command.ExecuteNonQuery();

        var version = ReadUserVersion();
        if (version == 0)
        {
            // 上面这套 CREATE TABLE IF NOT EXISTS 就是 v1 的结构，新库和老库都从这里起步。
            WriteUserVersion(1);
            version = 1;
        }

        if (version < 2)
        {
            MigrateToVersion2();
            version = 2;
        }

        if (version < 3)
        {
            MigrateToVersion3();
            version = 3;
        }

        if (version < 4)
        {
            MigrateToVersion4();
            version = 4;
        }

        if (version < 5)
        {
            MigrateToVersion5();
            version = 5;
        }

        if (version < 6)
        {
            MigrateToVersion6();
            version = 6;
        }

        if (version < 7)
        {
            MigrateToVersion7();
            version = 7;
        }

        if (version < SchemaVersion)
            MigrateToVersion8();
    }

    private int ReadUserVersion()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private void WriteUserVersion(int version, SqliteTransaction? transaction = null)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA user_version = " + version.ToString(CultureInfo.InvariantCulture) + ";";
        command.ExecuteNonQuery();
    }

    private void MigrateToVersion2()
    {
        using var tx = _connection.BeginTransaction();

        if (!HasColumn("MediaItem", "RelativeDir", tx))
        {
            using var columns = _connection.CreateCommand();
            columns.Transaction = tx;
            columns.CommandText = """
                ALTER TABLE MediaItem ADD COLUMN RelativeDir TEXT NOT NULL DEFAULT '';
                ALTER TABLE MediaItem ADD COLUMN FileName TEXT NOT NULL DEFAULT '';
                ALTER TABLE MediaItem ADD COLUMN AddedUtcTicks INTEGER NOT NULL DEFAULT 0;
                """;
            columns.ExecuteNonQuery();
        }

        using (var indexes = _connection.CreateCommand())
        {
            indexes.Transaction = tx;
            indexes.CommandText = """
                CREATE INDEX IF NOT EXISTS IX_MediaItem_Library_Dir ON MediaItem(LibraryId, RelativeDir);
                CREATE INDEX IF NOT EXISTS IX_MediaItem_Library_File ON MediaItem(LibraryId, FileName);
                """;
            indexes.ExecuteNonQuery();
        }

        BackfillRelativeColumns(tx);
        WriteUserVersion(2, tx);
        tx.Commit();
    }

    private void MigrateToVersion3()
    {
        using var tx = _connection.BeginTransaction();
        using var command = _connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS Folder (
                LibraryId TEXT NOT NULL,
                RelativePath TEXT NOT NULL,
                ParentPath TEXT,
                Name TEXT NOT NULL,
                DirectCount INTEGER NOT NULL,
                RecursiveCount INTEGER NOT NULL,
                CoverPath TEXT,
                CoverStamp TEXT,
                PRIMARY KEY (LibraryId, RelativePath)
            );
            CREATE INDEX IF NOT EXISTS IX_Folder_Parent ON Folder(LibraryId, ParentPath);
            """;
        command.ExecuteNonQuery();
        WriteUserVersion(3, tx);
        tx.Commit();
    }

    private void MigrateToVersion4()
    {
        using var tx = _connection.BeginTransaction();
        using var command = _connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS MediaPreference (
                Path TEXT PRIMARY KEY COLLATE NOCASE,
                AudioTrackIndex INTEGER NULL,
                AudioTrackTitle TEXT NULL,
                AudioTrackLanguage TEXT NULL,
                SubtitleMode INTEGER NOT NULL DEFAULT 0,
                SubtitleTrackIndex INTEGER NULL,
                SubtitleTrackTitle TEXT NULL,
                SubtitleTrackLanguage TEXT NULL,
                SubtitlePath TEXT NULL,
                SubtitleDelayMs INTEGER NOT NULL DEFAULT 0
            );
            """;
        command.ExecuteNonQuery();
        WriteUserVersion(4, tx);
        tx.Commit();
    }

    private void MigrateToVersion5()
    {
        using var tx = _connection.BeginTransaction();
        using var command = _connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS Favorite (
                MediaItemId TEXT PRIMARY KEY,
                AddedUtcTicks INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_Favorite_Added ON Favorite(AddedUtcTicks DESC);
            """;
        command.ExecuteNonQuery();
        AddColumn("MediaItem", "AudioCodec", "TEXT NULL", tx);
        AddColumn("MediaItem", "AudioStreamCount", "INTEGER NOT NULL DEFAULT 0", tx);
        AddColumn("MediaItem", "SubtitleStreamCount", "INTEGER NOT NULL DEFAULT 0", tx);
        AddColumn("MediaItem", "CollectionName", "TEXT NULL", tx);
        AddColumn("MediaItem", "MetadataEdited", "INTEGER NOT NULL DEFAULT 0", tx);
        WriteUserVersion(5, tx);
        tx.Commit();
    }

    private void AddColumn(string table, string column, string declaration, SqliteTransaction transaction)
    {
        if (HasColumn(table, column, transaction))
            return;
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {declaration};";
        command.ExecuteNonQuery();
    }

    private bool HasColumn(string table, string column, SqliteTransaction? transaction = null)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info($table) WHERE name = $column;";
        command.Parameters.AddWithValue("$table", table);
        command.Parameters.AddWithValue("$column", column);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }

    /// <summary>
    /// v1 的条目没有 <c>RelativeDir</c> / <c>FileName</c> / <c>AddedUtcTicks</c>，这里一次性回填。
    /// 老数据没有真实入库时间，用文件修改时间近似。
    /// </summary>
    private void BackfillRelativeColumns(SqliteTransaction transaction)
    {
        var rows = new List<(string Id, string RelativePath, long AddedTicks, long MtimeTicks)>();
        using (var command = _connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT Id, RelativePath, AddedUtcTicks, MtimeUtcTicks FROM MediaItem;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
                rows.Add((reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3)));
        }

        if (rows.Count == 0)
            return;

        using var update = _connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE MediaItem SET RelativeDir = $dir, FileName = $name, AddedUtcTicks = $added
            WHERE Id = $id;
            """;
        var dir = update.Parameters.Add("$dir", SqliteType.Text);
        var name = update.Parameters.Add("$name", SqliteType.Text);
        var added = update.Parameters.Add("$added", SqliteType.Integer);
        var id = update.Parameters.Add("$id", SqliteType.Text);

        foreach (var row in rows)
        {
            var split = RelativePaths.Split(row.RelativePath);
            dir.Value = split.Directory;
            name.Value = split.FileName;
            added.Value = row.AddedTicks > 0 ? row.AddedTicks : row.MtimeTicks;
            id.Value = row.Id;
            update.ExecuteNonQuery();
        }
    }

    private void Execute(string sql, params (string Name, object Value)[] parameters)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        command.ExecuteNonQuery();
    }

    private static MediaItem ReadItem(SqliteDataReader reader)
    {
        var frames = reader.IsDBNull(reader.GetOrdinal("FramePathsJson"))
            ? []
            : JsonSerializer.Deserialize<string[]>(reader.GetString(reader.GetOrdinal("FramePathsJson"))) ?? [];
        return new MediaItem
        {
            Id = reader.GetString(reader.GetOrdinal("Id")),
            LibraryId = reader.GetString(reader.GetOrdinal("LibraryId")),
            RelativePath = reader.GetString(reader.GetOrdinal("RelativePath")),
            Size = reader.GetInt64(reader.GetOrdinal("Size")),
            MtimeUtcTicks = reader.GetInt64(reader.GetOrdinal("MtimeUtcTicks")),
            DurationSeconds = reader.IsDBNull(reader.GetOrdinal("DurationSeconds")) ? null : reader.GetDouble(reader.GetOrdinal("DurationSeconds")),
            Width = reader.IsDBNull(reader.GetOrdinal("Width")) ? null : reader.GetInt32(reader.GetOrdinal("Width")),
            Height = reader.IsDBNull(reader.GetOrdinal("Height")) ? null : reader.GetInt32(reader.GetOrdinal("Height")),
            VideoCodec = reader.IsDBNull(reader.GetOrdinal("VideoCodec")) ? null : reader.GetString(reader.GetOrdinal("VideoCodec")),
            AudioCodec = reader.IsDBNull(reader.GetOrdinal("AudioCodec")) ? null : reader.GetString(reader.GetOrdinal("AudioCodec")),
            AudioStreamCount = reader.GetInt32(reader.GetOrdinal("AudioStreamCount")),
            SubtitleStreamCount = reader.GetInt32(reader.GetOrdinal("SubtitleStreamCount")),
            Status = (ItemStatus)reader.GetInt32(reader.GetOrdinal("Status")),
            Error = reader.IsDBNull(reader.GetOrdinal("Error")) ? null : reader.GetString(reader.GetOrdinal("Error")),
            SheetPath = reader.IsDBNull(reader.GetOrdinal("SheetPath")) ? null : reader.GetString(reader.GetOrdinal("SheetPath")),
            FramePaths = frames,
            MediaType = reader.IsDBNull(reader.GetOrdinal("MediaType")) ? null : reader.GetString(reader.GetOrdinal("MediaType")),
            TmdbId = reader.IsDBNull(reader.GetOrdinal("TmdbId")) ? null : reader.GetInt32(reader.GetOrdinal("TmdbId")),
            Season = reader.IsDBNull(reader.GetOrdinal("Season")) ? null : reader.GetInt32(reader.GetOrdinal("Season")),
            Episode = reader.IsDBNull(reader.GetOrdinal("Episode")) ? null : reader.GetInt32(reader.GetOrdinal("Episode")),
            Title = reader.IsDBNull(reader.GetOrdinal("Title")) ? null : reader.GetString(reader.GetOrdinal("Title")),
            OriginalTitle = reader.IsDBNull(reader.GetOrdinal("OriginalTitle")) ? null : reader.GetString(reader.GetOrdinal("OriginalTitle")),
            Year = reader.IsDBNull(reader.GetOrdinal("Year")) ? null : reader.GetInt32(reader.GetOrdinal("Year")),
            Overview = reader.IsDBNull(reader.GetOrdinal("Overview")) ? null : reader.GetString(reader.GetOrdinal("Overview")),
            Rating = reader.IsDBNull(reader.GetOrdinal("Rating")) ? null : reader.GetDouble(reader.GetOrdinal("Rating")),
            PosterPath = reader.IsDBNull(reader.GetOrdinal("PosterPath")) ? null : reader.GetString(reader.GetOrdinal("PosterPath")),
            CollectionName = reader.IsDBNull(reader.GetOrdinal("CollectionName")) ? null : reader.GetString(reader.GetOrdinal("CollectionName")),
            MetadataEdited = reader.GetInt32(reader.GetOrdinal("MetadataEdited")) != 0,
            MatchStatus = (MatchStatus)reader.GetInt32(reader.GetOrdinal("MatchStatus")),
            AddedUtcTicks = reader.GetInt64(reader.GetOrdinal("AddedUtcTicks"))
        };
    }
}
