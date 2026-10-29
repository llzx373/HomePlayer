using System.Text.Json;
using HomePlayer.Core;
using Microsoft.Data.Sqlite;

namespace HomePlayer.Data;

public sealed partial class LibraryRepository
{
    public IReadOnlyList<string> ListTags()
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT Name FROM Tag ORDER BY Name COLLATE NOCASE;";
            using var reader = command.ExecuteReader();
            var tags = new List<string>();
            while (reader.Read())
                tags.Add(reader.GetString(0));
            return tags;
        }
    }

    public void SetTags(IReadOnlyCollection<string> itemIds, IReadOnlyCollection<string> tags)
    {
        if (itemIds.Count == 0)
            return;
        var names = tags.Select(tag => tag.Trim()).Where(tag => tag.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        lock (_gate)
        {
            using var tx = _connection.BeginTransaction();
            using var delete = _connection.CreateCommand();
            delete.Transaction = tx;
            delete.CommandText = "DELETE FROM MediaTag WHERE MediaItemId = $id;";
            var deleteId = delete.Parameters.Add("$id", SqliteType.Text);
            using var tag = _connection.CreateCommand();
            tag.Transaction = tx;
            tag.CommandText = "INSERT INTO Tag (Name) VALUES ($name) ON CONFLICT(Name) DO NOTHING;";
            var tagName = tag.Parameters.Add("$name", SqliteType.Text);
            using var link = _connection.CreateCommand();
            link.Transaction = tx;
            link.CommandText = "INSERT INTO MediaTag (MediaItemId, TagId) VALUES ($id, (SELECT Id FROM Tag WHERE Name = $name));";
            var linkId = link.Parameters.Add("$id", SqliteType.Text);
            var linkName = link.Parameters.Add("$name", SqliteType.Text);
            foreach (var id in itemIds.Distinct(StringComparer.Ordinal))
            {
                deleteId.Value = id;
                delete.ExecuteNonQuery();
                foreach (var name in names)
                {
                    tagName.Value = name;
                    tag.ExecuteNonQuery();
                    linkId.Value = id;
                    linkName.Value = name;
                    link.ExecuteNonQuery();
                }
            }
            tx.Commit();
        }
    }

    public IReadOnlyList<MediaCollection> ListCollections()
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT Id, Name, Kind, RuleJson FROM MediaCollection ORDER BY Name COLLATE NOCASE;";
            using var reader = command.ExecuteReader();
            var result = new List<MediaCollection>();
            while (reader.Read())
                result.Add(new MediaCollection(
                    reader.GetString(0),
                    reader.GetString(1),
                    (MediaCollectionKind)reader.GetInt32(2),
                    JsonSerializer.Deserialize<SmartCollectionRule>(reader.GetString(3)) ?? new SmartCollectionRule()));
            return result;
        }
    }

    public MediaCollection CreateCollection(string name, MediaCollectionKind kind, SmartCollectionRule? rule = null)
    {
        name = name.Trim();
        if (name.Length == 0)
            throw new InvalidOperationException("合集名称不能为空。");
        var collection = new MediaCollection(Guid.NewGuid().ToString("N"), name, kind, rule ?? new SmartCollectionRule());
        lock (_gate)
            Execute(
                "INSERT INTO MediaCollection (Id, Name, Kind, RuleJson) VALUES ($id, $name, $kind, $rule);",
                ("$id", collection.Id),
                ("$name", collection.Name),
                ("$kind", (int)collection.Kind),
                ("$rule", JsonSerializer.Serialize(collection.Rule)));
        return collection;
    }

    public void AddToCollection(string collectionId, IReadOnlyCollection<string> itemIds)
    {
        lock (_gate)
        {
            using var tx = _connection.BeginTransaction();
            using var command = _connection.CreateCommand();
            command.Transaction = tx;
            command.CommandText = "INSERT INTO MediaCollectionItem (CollectionId, MediaItemId) VALUES ($collection, $id) ON CONFLICT DO NOTHING;";
            var collection = command.Parameters.Add("$collection", SqliteType.Text);
            var id = command.Parameters.Add("$id", SqliteType.Text);
            collection.Value = collectionId;
            foreach (var itemId in itemIds.Distinct(StringComparer.Ordinal))
            {
                id.Value = itemId;
                command.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    public void DeleteCollection(string collectionId)
    {
        lock (_gate)
        {
            using var tx = _connection.BeginTransaction();
            Execute("DELETE FROM MediaCollectionItem WHERE CollectionId = $id;", ("$id", collectionId));
            Execute("DELETE FROM MediaCollection WHERE Id = $id;", ("$id", collectionId));
            tx.Commit();
        }
    }

    private void MigrateToVersion8()
    {
        using var tx = _connection.BeginTransaction();
        using var command = _connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS Tag (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL UNIQUE COLLATE NOCASE
            );
            CREATE TABLE IF NOT EXISTS MediaTag (
                MediaItemId TEXT NOT NULL,
                TagId INTEGER NOT NULL,
                PRIMARY KEY (MediaItemId, TagId)
            );
            CREATE INDEX IF NOT EXISTS IX_MediaTag_Tag ON MediaTag(TagId, MediaItemId);
            CREATE TABLE IF NOT EXISTS MediaCollection (
                Id TEXT PRIMARY KEY,
                Name TEXT NOT NULL,
                Kind INTEGER NOT NULL,
                RuleJson TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS MediaCollectionItem (
                CollectionId TEXT NOT NULL,
                MediaItemId TEXT NOT NULL,
                PRIMARY KEY (CollectionId, MediaItemId)
            );
            CREATE INDEX IF NOT EXISTS IX_MediaCollectionItem_Item ON MediaCollectionItem(MediaItemId, CollectionId);
            """;
        command.ExecuteNonQuery();
        WriteUserVersion(8, tx);
        tx.Commit();
    }
}
