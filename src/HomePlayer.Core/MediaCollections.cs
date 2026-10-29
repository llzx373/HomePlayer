namespace HomePlayer.Core;

public enum MediaCollectionKind
{
    Manual,
    Smart
}

public sealed record SmartCollectionRule(
    string? Search = null,
    string? Tag = null,
    WatchFilter Watch = WatchFilter.All,
    bool FavoritesOnly = false);

public sealed record MediaCollection(
    string Id,
    string Name,
    MediaCollectionKind Kind,
    SmartCollectionRule Rule);

public sealed record MediaTag(string Name);
