namespace HomePlayer.Core;

public sealed record MediaChapter(string Title, TimeSpan Start);

public sealed record MediaBookmark(string Path, double PositionSeconds, string Title, long CreatedUtcTicks);

public interface IPlaybackChapterSource
{
    IReadOnlyList<MediaChapter> GetChapters();
}

public interface IMediaBookmarkStore
{
    IReadOnlyList<MediaBookmark> ListBookmarks(string path);
    void SaveBookmark(MediaBookmark bookmark);
    void DeleteBookmark(string path, double positionSeconds);
}
