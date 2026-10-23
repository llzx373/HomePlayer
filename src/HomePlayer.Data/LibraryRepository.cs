using System.Globalization;
using System.Text.Json;
using HomePlayer.Core;
using Microsoft.Data.Sqlite;

namespace HomePlayer.Data;

public sealed class LibraryRepository : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly object _gate = new();

    public LibraryRepository(string databasePath)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
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
            Execute("DELETE FROM MediaItem WHERE LibraryId = $id;", ("$id", id));
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

    private const int MaxQueryIds = 400;

    public void InsertItem(MediaItem item)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO MediaItem (
                    Id, LibraryId, RelativePath, Size, MtimeUtcTicks, DurationSeconds, Width, Height, VideoCodec,
                    Status, Error, SheetPath, FramePathsJson, MediaType, TmdbId, Season, Episode, Title, OriginalTitle,
                    Year, Overview, Rating, PosterPath, MatchStatus)
                VALUES (
                    $id, $library, $path, $size, $mtime, NULL, NULL, NULL, NULL,
                    $status, NULL, NULL, '[]', NULL, NULL, NULL, NULL, NULL, NULL,
                    NULL, NULL, NULL, NULL, $match);
                """;
            command.Parameters.AddWithValue("$id", item.Id);
            command.Parameters.AddWithValue("$library", item.LibraryId);
            command.Parameters.AddWithValue("$path", item.RelativePath);
            command.Parameters.AddWithValue("$size", item.Size);
            command.Parameters.AddWithValue("$mtime", item.MtimeUtcTicks);
            command.Parameters.AddWithValue("$status", (int)item.Status);
            command.Parameters.AddWithValue("$match", (int)item.MatchStatus);
            command.ExecuteNonQuery();
        }
    }

    public void MarkChanged(MediaItem item, long size, long mtimeTicks)
    {
        lock (_gate)
        {
            Execute(
                """
                UPDATE MediaItem
                SET Size = $size, MtimeUtcTicks = $mtime, DurationSeconds = NULL, Width = NULL, Height = NULL,
                    VideoCodec = NULL, Status = $pending, Error = NULL, SheetPath = NULL, FramePathsJson = '[]',
                    MediaType = NULL, TmdbId = NULL, Season = NULL, Episode = NULL, Title = NULL, OriginalTitle = NULL,
                    Year = NULL, Overview = NULL, Rating = NULL, PosterPath = NULL, MatchStatus = $none
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
                SET DurationSeconds = $duration, Width = $width, Height = $height, VideoCodec = $codec
                WHERE Id = $id;
                """,
                ("$duration", probe.DurationSeconds),
                ("$width", probe.Width),
                ("$height", probe.Height),
                ("$codec", (object?)probe.VideoCodec ?? DBNull.Value),
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
                    PosterPath = $poster, MatchStatus = $match, Status = $status, Error = $error
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
            """;
        command.ExecuteNonQuery();
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
            MatchStatus = (MatchStatus)reader.GetInt32(reader.GetOrdinal("MatchStatus"))
        };
    }
}
