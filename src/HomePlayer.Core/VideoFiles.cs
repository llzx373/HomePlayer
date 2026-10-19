namespace HomePlayer.Core;

public static class VideoFiles
{
    public static readonly string[] Extensions =
    [
        ".mkv", ".mp4", ".m4v", ".mov", ".avi", ".wmv", ".flv",
        ".ts", ".m2ts", ".mpg", ".mpeg", ".webm", ".rmvb"
    ];

    public static bool IsVideo(string path)
    {
        var ext = Path.GetExtension(path);
        return Extensions.Contains(ext, StringComparer.OrdinalIgnoreCase);
    }

    public static IEnumerable<string> Enumerate(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            IEnumerable<string> children;
            IEnumerable<string> files;
            try
            {
                children = Directory.EnumerateDirectories(dir);
                files = Directory.EnumerateFiles(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                if (IsVideo(file))
                    yield return file;
            }

            foreach (var child in children)
                stack.Push(child);
        }
    }

    public static List<string> Expand(IEnumerable<string> paths)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                foreach (var file in Enumerate(path))
                    Add(file);
            }
            else if (File.Exists(path) && IsVideo(path))
            {
                Add(path);
            }
        }

        return result;

        void Add(string file)
        {
            var full = Path.GetFullPath(file);
            if (seen.Add(full))
                result.Add(full);
        }
    }
}
