using System.Text.Json;
using HomePlayer.Data;
using Microsoft.Data.Sqlite;

namespace HomePlayer.Library.Tests;

public class CachePathMigrationTests
{
    [Fact]
    public void Rewrites_item_and_folder_cache_paths()
    {
        var root = TempDirectory();
        var database = Path.Combine(root, "library.db");
        try
        {
            using (var connection = new SqliteConnection($"Data Source={database};Pooling=False"))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = """
                        CREATE TABLE MediaItem (Id TEXT PRIMARY KEY, SheetPath TEXT, FramePathsJson TEXT, PosterPath TEXT);
                        CREATE TABLE Folder (LibraryId TEXT, RelativePath TEXT, CoverPath TEXT, PRIMARY KEY (LibraryId, RelativePath));
                        """;
                    command.ExecuteNonQuery();
                }

                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "INSERT INTO MediaItem VALUES ('a', $sheet, $frames, $poster);";
                    command.Parameters.AddWithValue("$sheet", @"C:\data\cache\thumbs\a\sheet.jpg");
                    command.Parameters.AddWithValue("$frames", """["C:\\data\\cache\\thumbs\\a\\f0.jpg","relative.jpg"]""");
                    command.Parameters.AddWithValue("$poster", @"C:\data\cache\posters\1.jpg");
                    command.ExecuteNonQuery();
                }

                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "INSERT INTO Folder VALUES ('lib', 'dir', $cover);";
                    command.Parameters.AddWithValue("$cover", @"C:\data\cache\folder-thumbs\x\cover.jpg");
                    command.ExecuteNonQuery();
                }
            }

            var updated = CachePathMigration.Rewrite(database, @"C:\data", @"F:\moved");

            Assert.Equal(2, updated);
            using var check = new SqliteConnection($"Data Source={database};Pooling=False");
            check.Open();
            using var read = check.CreateCommand();
            read.CommandText = """
                SELECT i.SheetPath, i.FramePathsJson, i.PosterPath, f.CoverPath
                FROM MediaItem i, Folder f;
                """;
            using var reader = read.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal(@"F:\moved\cache\thumbs\a\sheet.jpg", reader.GetString(0));
            Assert.Equal(
                [@"F:\moved\cache\thumbs\a\f0.jpg", "relative.jpg"],
                JsonSerializer.Deserialize<string[]>(reader.GetString(1))!);
            Assert.Equal(@"F:\moved\cache\posters\1.jpg", reader.GetString(2));
            Assert.Equal(@"F:\moved\cache\folder-thumbs\x\cover.jpg", reader.GetString(3));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Leaves_unrelated_paths_untouched()
    {
        var root = TempDirectory();
        var database = Path.Combine(root, "library.db");
        try
        {
            using (var connection = new SqliteConnection($"Data Source={database};Pooling=False"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE MediaItem (Id TEXT PRIMARY KEY, SheetPath TEXT, FramePathsJson TEXT, PosterPath TEXT);
                    CREATE TABLE Folder (LibraryId TEXT, RelativePath TEXT, CoverPath TEXT, PRIMARY KEY (LibraryId, RelativePath));
                    INSERT INTO MediaItem VALUES ('a', 'D:\other\sheet.jpg', '[]', NULL);
                    """;
                command.ExecuteNonQuery();
            }

            Assert.Equal(0, CachePathMigration.Rewrite(database, @"C:\data", @"F:\moved"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static string TempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "HomePlayer.CachePath." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
