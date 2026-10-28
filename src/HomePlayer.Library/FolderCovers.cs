using System.Security.Cryptography;
using System.Text;
using HomePlayer.Core;

namespace HomePlayer.Library;

/// <summary>
/// 文件夹封面：缓存路径、输入指纹与合并规则。
/// </summary>
public static class FolderCovers
{
    public const int CoverWidth = 480;
    public const int CoverHeight = 270;
    public const int SourceLimit = 4;

    public static string CacheDirectory(string libraryId, string relativePath) =>
        Path.Combine(AppPaths.FolderThumbs, Hash(libraryId + "|" + Normalize(relativePath)));

    public static string CoverPath(string libraryId, string relativePath) =>
        Path.Combine(CacheDirectory(libraryId, relativePath), "cover.jpg");

    /// <summary>
    /// 指纹只由参与拼接的条目和它们的修改时间决定，任何一项变了就要重建。
    /// </summary>
    public static string Stamp(IReadOnlyList<MediaItem> sources)
    {
        if (sources.Count == 0)
            return "";
        var text = string.Join("|", sources.Select(item => item.Id + ":" + item.MtimeUtcTicks));
        return Hash(text);
    }

    /// <summary>
    /// Raw 库取条目中间的帧，没有则用第一帧；Catalog 库用海报。
    /// </summary>
    public static string? ImagePath(MediaItem item, LibraryKind kind)
    {
        if (kind == LibraryKind.Catalog)
            return string.IsNullOrWhiteSpace(item.PosterPath) ? null : item.PosterPath;
        if (item.FramePaths.Length == 0)
            return null;
        var middle = item.FramePaths.Length > 4 ? item.FramePaths[4] : item.FramePaths[item.FramePaths.Length / 2];
        return string.IsNullOrWhiteSpace(middle) ? item.FramePaths[0] : middle;
    }

    private static string Normalize(string relativePath) =>
        (relativePath ?? "").Replace('\\', '/').Trim('/');

    private static string Hash(string value) =>
        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(value)))[..16].ToLowerInvariant();
}
