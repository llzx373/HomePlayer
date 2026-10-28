namespace HomePlayer.Core;

/// <summary>
/// 从文件相对路径推导目录树。纯函数，扫描和测试共用一套计数规则。
/// </summary>
public static class FolderTree
{
    public static IReadOnlyList<MediaFolder> Build(MediaLibrary library, IEnumerable<string> filePaths)
    {
        var counts = new Dictionary<string, (int Direct, int Recursive)>(StringComparer.OrdinalIgnoreCase);
        foreach (var filePath in filePaths)
        {
            var dir = RelativePaths.Split(filePath).Directory;
            foreach (var ancestor in Ancestors(dir))
            {
                var current = counts.TryGetValue(ancestor, out var value) ? value : (Direct: 0, Recursive: 0);
                counts[ancestor] = string.Equals(ancestor, dir, StringComparison.OrdinalIgnoreCase)
                    ? (current.Direct + 1, current.Recursive + 1)
                    : (current.Direct, current.Recursive + 1);
            }
        }

        var folders = new List<MediaFolder>(counts.Count);
        foreach (var (path, count) in counts)
        {
            folders.Add(new MediaFolder
            {
                LibraryId = library.Id,
                RelativePath = path,
                ParentPath = Parent(path),
                Name = path.Length == 0 ? library.Name : path[(path.LastIndexOf('/') + 1)..],
                DirectCount = count.Direct,
                RecursiveCount = count.Recursive
            });
        }

        folders.Sort((left, right) => string.Compare(left.RelativePath, right.RelativePath, StringComparison.OrdinalIgnoreCase));
        return folders;
    }

    /// <summary>
    /// 目录自身及全部祖先，根目录固定是空串。
    /// </summary>
    public static IEnumerable<string> Ancestors(string relativeDir)
    {
        var current = relativeDir;
        while (true)
        {
            yield return current;
            if (current.Length == 0)
                yield break;
            var slash = current.LastIndexOf('/');
            current = slash < 0 ? "" : current[..slash];
        }
    }

    public static string? Parent(string relativePath)
    {
        if (relativePath.Length == 0)
            return null;
        var slash = relativePath.LastIndexOf('/');
        return slash < 0 ? "" : relativePath[..slash];
    }
}
