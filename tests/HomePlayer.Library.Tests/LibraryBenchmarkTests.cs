using System.Diagnostics;
using HomePlayer.Core;
using HomePlayer.Data;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;

namespace HomePlayer.Library.Tests;

public class LibraryBenchmarkTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(10_000)]
    [InlineData(50_000)]
    public void Large_library_search_paging_tree_and_progress_are_measured(int count)
    {
        var directory = Path.Combine(Path.GetTempPath(), "HomePlayer.Benchmark." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var database = Path.Combine(directory, "library.db");
        try
        {
            using var repository = new LibraryRepository(database);
            var library = new MediaLibrary
            {
                Id = "benchmark",
                Name = "Benchmark",
                RootPath = directory,
                Kind = LibraryKind.Raw
            };
            repository.InsertLibrary(library);
            Seed(database, library.Id, count);

            var paths = Enumerable.Range(0, count)
                .Select(i => Path.Combine(directory, $"folder{i % 1000}", $"file{i:D5}.mkv"))
                .ToArray();
            var measurements = new Dictionary<string, double>();
            var sw = Stopwatch.StartNew();
            var query = new MediaLibraryQuery { LibraryId = library.Id, Search = "file010" };
            var matches = repository.CountItems(query);
            measurements["search-count-ms"] = sw.Elapsed.TotalMilliseconds;
            sw.Restart();
            var page = repository.ListItemsPage(query, SortMode.FileName, 0, 60);
            measurements["search-page-ms"] = sw.Elapsed.TotalMilliseconds;
            sw.Restart();
            var tree = FolderTree.Build(library, Enumerable.Range(0, count).Select(i => $"folder{i % 1000}/file{i:D5}.mkv"));
            repository.SyncFolders(library.Id, tree);
            measurements["folder-tree-ms"] = sw.Elapsed.TotalMilliseconds;
            sw.Restart();
            var states = repository.LoadPlaybackStates(paths);
            measurements["progress-load-ms"] = sw.Elapsed.TotalMilliseconds;

            Assert.True(matches > 0);
            Assert.True(page.Count > 0);
            Assert.Equal(1001, tree.Count);
            Assert.Equal(count, states.Count);
            output.WriteLine($"items={count}; {string.Join("; ", measurements.Select(pair => $"{pair.Key}={pair.Value:F2}"))}");
        }
        finally
        {
            try
            {
                Directory.Delete(directory, true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static void Seed(string database, string libraryId, int count)
    {
        using var connection = new SqliteConnection($"Data Source={database};Pooling=false");
        connection.Open();
        using var tx = connection.BeginTransaction();
        using var item = connection.CreateCommand();
        item.Transaction = tx;
        item.CommandText = """
            INSERT INTO MediaItem (
                Id, LibraryId, RelativePath, RelativeDir, FileName, AddedUtcTicks,
                Size, MtimeUtcTicks, Status, FramePathsJson, MatchStatus)
            VALUES ($id, $library, $path, $dir, $name, $added, 1, 1, 1, '[]', 0);
            """;
        var id = item.Parameters.Add("$id", SqliteType.Text);
        var library = item.Parameters.Add("$library", SqliteType.Text);
        var path = item.Parameters.Add("$path", SqliteType.Text);
        var dir = item.Parameters.Add("$dir", SqliteType.Text);
        var name = item.Parameters.Add("$name", SqliteType.Text);
        var added = item.Parameters.Add("$added", SqliteType.Integer);
        using var state = connection.CreateCommand();
        state.Transaction = tx;
        state.CommandText = """
            INSERT INTO PlaybackState (Path, PositionSeconds, DurationSeconds, Finished, UpdatedUtcTicks)
            VALUES ($path, 10, 100, 0, $added);
            """;
        var statePath = state.Parameters.Add("$path", SqliteType.Text);
        var stateAdded = state.Parameters.Add("$added", SqliteType.Integer);
        for (var i = 0; i < count; i++)
        {
            var relative = $"folder{i % 1000}/file{i:D5}.mkv";
            id.Value = $"item-{i}";
            library.Value = libraryId;
            path.Value = relative;
            dir.Value = $"folder{i % 1000}";
            name.Value = $"file{i:D5}.mkv";
            added.Value = i;
            item.ExecuteNonQuery();
            statePath.Value = Path.Combine(Path.GetDirectoryName(database)!, relative.Replace('/', Path.DirectorySeparatorChar));
            stateAdded.Value = i;
            state.ExecuteNonQuery();
        }
        tx.Commit();
    }
}
