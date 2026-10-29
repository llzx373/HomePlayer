using HomePlayer.Core;
using Microsoft.Data.Sqlite;

namespace HomePlayer.Data;

public sealed partial class LibraryRepository : IMediaBookmarkStore
{
    public IReadOnlyList<MediaBookmark> ListBookmarks(string path)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT Path, PositionSeconds, Title, CreatedUtcTicks FROM MediaBookmark WHERE Path = $path ORDER BY PositionSeconds;";
            command.Parameters.AddWithValue("$path", path);
            using var reader = command.ExecuteReader();
            var result = new List<MediaBookmark>();
            while (reader.Read())
                result.Add(new MediaBookmark(reader.GetString(0), reader.GetDouble(1), reader.GetString(2), reader.GetInt64(3)));
            return result;
        }
    }

    public void SaveBookmark(MediaBookmark bookmark)
    {
        lock (_gate)
            Execute(
                """
                INSERT INTO MediaBookmark (Path, PositionSeconds, Title, CreatedUtcTicks)
                VALUES ($path, $position, $title, $created)
                ON CONFLICT(Path, PositionSeconds) DO UPDATE SET Title = excluded.Title;
                """,
                ("$path", bookmark.Path),
                ("$position", bookmark.PositionSeconds),
                ("$title", bookmark.Title),
                ("$created", bookmark.CreatedUtcTicks));
    }

    public void DeleteBookmark(string path, double positionSeconds)
    {
        lock (_gate)
            Execute(
                "DELETE FROM MediaBookmark WHERE Path = $path AND PositionSeconds = $position;",
                ("$path", path),
                ("$position", positionSeconds));
    }

    private void MigrateToVersion6()
    {
        using var tx = _connection.BeginTransaction();
        AddColumn("MediaPreference", "Volume", "REAL NULL", tx);
        AddColumn("MediaPreference", "PlaybackRate", "REAL NULL", tx);
        using var command = _connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS MediaBookmark (
                Path TEXT NOT NULL COLLATE NOCASE,
                PositionSeconds REAL NOT NULL,
                Title TEXT NOT NULL,
                CreatedUtcTicks INTEGER NOT NULL,
                PRIMARY KEY (Path, PositionSeconds)
            );
            CREATE INDEX IF NOT EXISTS IX_MediaBookmark_Path ON MediaBookmark(Path, PositionSeconds);
            """;
        command.ExecuteNonQuery();
        WriteUserVersion(6, tx);
        tx.Commit();
    }

    private void MigrateToVersion7()
    {
        using var tx = _connection.BeginTransaction();
        AddColumn("MediaPreference", "AudioDelayMs", "INTEGER NOT NULL DEFAULT 0", tx);
        WriteUserVersion(7, tx);
        tx.Commit();
    }
}
