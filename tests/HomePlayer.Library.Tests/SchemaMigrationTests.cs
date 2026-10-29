using HomePlayer.Core;
using HomePlayer.Data;
using Microsoft.Data.Sqlite;

namespace HomePlayer.Library.Tests;

public class SchemaMigrationTests
{
    [Fact]
    public void New_database_is_migrated_to_current_version()
    {
        using var temp = new TempDatabase();
        using var repository = new LibraryRepository(temp.DatabasePath);

        Assert.Equal(LibraryRepository.SchemaVersion, temp.UserVersion());
        Assert.True(temp.HasColumn("MediaItem", "RelativeDir"));
        Assert.True(temp.HasColumn("MediaItem", "FileName"));
        Assert.True(temp.HasColumn("MediaItem", "AddedUtcTicks"));
        Assert.True(temp.HasColumn("MediaPreference", "SubtitleDelayMs"));
        Assert.True(temp.HasColumn("MediaPreference", "Volume"));
        Assert.True(temp.HasColumn("MediaPreference", "PlaybackRate"));
        Assert.True(temp.HasColumn("MediaPreference", "AudioDelayMs"));
        Assert.Equal(0L, temp.Integer("SELECT COUNT(*) FROM MediaBookmark;"));
        Assert.Equal(0L, temp.Integer("SELECT COUNT(*) FROM Tag;"));
        Assert.Equal(0L, temp.Integer("SELECT COUNT(*) FROM MediaCollection;"));
        Assert.True(temp.HasColumn("MediaItem", "AudioStreamCount"));
        Assert.True(temp.HasColumn("MediaItem", "SubtitleStreamCount"));
        Assert.True(temp.HasColumn("MediaItem", "CollectionName"));
        Assert.Equal(0L, temp.Integer("SELECT COUNT(*) FROM Favorite;"));
    }

    [Fact]
    public void Folder_table_is_created_by_the_migration()
    {
        using var temp = new TempDatabase();
        using var repository = new LibraryRepository(temp.DatabasePath);

        Assert.Equal(0L, temp.Integer("SELECT COUNT(*) FROM Folder;"));
    }

    [Fact]
    public void Version1_database_backfills_relative_columns()
    {
        using var temp = new TempDatabase();
        temp.CreateVersion1Database();
        using var repository = new LibraryRepository(temp.DatabasePath);

        Assert.Equal(LibraryRepository.SchemaVersion, temp.UserVersion());
        Assert.Equal("a/b", temp.Text("SELECT RelativeDir FROM MediaItem WHERE Id = 'nested';"));
        Assert.Equal("c.mkv", temp.Text("SELECT FileName FROM MediaItem WHERE Id = 'nested';"));
        Assert.Equal("", temp.Text("SELECT RelativeDir FROM MediaItem WHERE Id = 'root';"));
        Assert.Equal("root.mkv", temp.Text("SELECT FileName FROM MediaItem WHERE Id = 'root';"));
        Assert.Equal(1111, temp.Integer("SELECT AddedUtcTicks FROM MediaItem WHERE Id = 'nested';"));
    }

    [Fact]
    public void Migration_is_idempotent()
    {
        using var temp = new TempDatabase();
        temp.CreateVersion1Database();

        using (var first = new LibraryRepository(temp.DatabasePath))
        {
        }

        using (var second = new LibraryRepository(temp.DatabasePath))
        {
        }

        Assert.Equal(LibraryRepository.SchemaVersion, temp.UserVersion());
        Assert.Equal("a/b", temp.Text("SELECT RelativeDir FROM MediaItem WHERE Id = 'nested';"));
    }

    [Fact]
    public void InsertItem_persists_relative_columns_and_added_time()
    {
        using var temp = new TempDatabase();
        using var repository = new LibraryRepository(temp.DatabasePath);
        repository.InsertLibrary(new MediaLibrary
        {
            Id = "lib",
            Name = "测试",
            RootPath = temp.Directory,
            Kind = LibraryKind.Raw
        });

        repository.InsertItem(new MediaItem
        {
            Id = "item",
            LibraryId = "lib",
            RelativePath = @"剧集\第 1 季\E01.mkv",
            Size = 10,
            MtimeUtcTicks = 555,
            Status = ItemStatus.Ready
        });

        Assert.Equal("剧集/第 1 季", temp.Text("SELECT RelativeDir FROM MediaItem WHERE Id = 'item';"));
        Assert.Equal("E01.mkv", temp.Text("SELECT FileName FROM MediaItem WHERE Id = 'item';"));
        Assert.True(temp.Integer("SELECT AddedUtcTicks FROM MediaItem WHERE Id = 'item';") > 0);

        var loaded = Assert.Single(repository.ListItems("lib"));
        Assert.Equal("剧集/第 1 季", loaded.RelativeDir);
        Assert.Equal("E01.mkv", loaded.FileName);
        Assert.True(loaded.AddedUtcTicks > 0);
    }

    private sealed class TempDatabase : IDisposable
    {
        public TempDatabase()
        {
            Directory = Path.Combine(Path.GetTempPath(), "HomePlayer.Schema." + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Directory);
            DatabasePath = System.IO.Path.Combine(Directory, "library.db");
        }

        public string Directory { get; }
        public string DatabasePath { get; }

        /// <summary>
        /// 复刻 v1 的表结构和一行数据，用于验证升级路径。
        /// </summary>
        public void CreateVersion1Database()
        {
            using var connection = new SqliteConnection($"Data Source={DatabasePath}");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE Library (
                    Id TEXT PRIMARY KEY,
                    Name TEXT NOT NULL,
                    RootPath TEXT NOT NULL,
                    Kind INTEGER NOT NULL,
                    LastFullScanUtcTicks INTEGER NULL
                );
                CREATE TABLE MediaItem (
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
                CREATE TABLE WorkItem (
                    MediaItemId TEXT PRIMARY KEY,
                    Kind INTEGER NOT NULL,
                    State INTEGER NOT NULL,
                    UpdatedUtc TEXT NOT NULL
                );
                CREATE TABLE TmdbCache (
                    Key TEXT PRIMARY KEY,
                    Payload TEXT NOT NULL,
                    StoredUtc TEXT NOT NULL
                );
                INSERT INTO Library (Id, Name, RootPath, Kind, LastFullScanUtcTicks)
                VALUES ('lib', '旧库', 'D:\旧库', 0, NULL);
                INSERT INTO MediaItem (
                    Id, LibraryId, RelativePath, Size, MtimeUtcTicks, Status, FramePathsJson, MatchStatus)
                VALUES ('nested', 'lib', 'a\b\c.mkv', 1, 1111, 1, '[]', 0);
                INSERT INTO MediaItem (
                    Id, LibraryId, RelativePath, Size, MtimeUtcTicks, Status, FramePathsJson, MatchStatus)
                VALUES ('root', 'lib', 'root.mkv', 1, 2222, 1, '[]', 0);
                """;
            command.ExecuteNonQuery();
        }

        public int UserVersion()
        {
            using var connection = new SqliteConnection($"Data Source={DatabasePath}");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version;";
            return Convert.ToInt32(command.ExecuteScalar());
        }

        public bool HasColumn(string table, string column)
        {
            using var connection = new SqliteConnection($"Data Source={DatabasePath}");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM pragma_table_info($table) WHERE name = $column;";
            command.Parameters.AddWithValue("$table", table);
            command.Parameters.AddWithValue("$column", column);
            return Convert.ToInt32(command.ExecuteScalar()) > 0;
        }

        public string? Text(string sql)
        {
            using var connection = new SqliteConnection($"Data Source={DatabasePath}");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return command.ExecuteScalar() as string;
        }

        public long Integer(string sql)
        {
            using var connection = new SqliteConnection($"Data Source={DatabasePath}");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(command.ExecuteScalar());
        }

        public void Dispose()
        {
            try
            {
                System.IO.Directory.Delete(Directory, true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
