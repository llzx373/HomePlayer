namespace HomePlayer.Core;

/// <summary>
/// 相对路径的目录 / 文件名拆分。统一用正斜杠，避免和 SQL LIKE 的转义字符冲突。
/// </summary>
public static class RelativePaths
{
    public static (string Directory, string FileName) Split(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');
        var slash = normalized.LastIndexOf('/');
        return slash < 0
            ? ("", normalized)
            : (normalized[..slash], normalized[(slash + 1)..]);
    }
}
