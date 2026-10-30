namespace HomePlayer.Core;

public sealed record DirectoryScanFailure(string Path, string Error);

public sealed record VideoScanResult(
    IReadOnlyList<string> Files,
    IReadOnlyList<DirectoryScanFailure> Failures)
{
    public bool IsComplete => Failures.Count == 0;
}

public static class VideoFiles
{
    public static readonly string[] Extensions =
    [
        ".mkv", ".mp4", ".m4v", ".mov", ".avi", ".wmv", ".flv",
        ".ts", ".m2ts", ".mpg", ".mpeg", ".webm", ".rmvb", ".rm",
        ".3gp", ".3g2", ".ogv", ".mts", ".vob", ".mxf", ".dv",
        ".wtv", ".y4m"
    ];

    public static bool IsVideo(string path)
    {
        var ext = Path.GetExtension(path);
        return Extensions.Contains(ext, StringComparer.OrdinalIgnoreCase);
    }

    public static VideoScanResult Scan(string root)
    {
        var files = new List<string>();
        var failures = new List<DirectoryScanFailure>();
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir))
                {
                    if (IsVideo(file))
                        files.Add(file);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures.Add(new DirectoryScanFailure(dir, ex.Message));
            }

            try
            {
                foreach (var child in Directory.EnumerateDirectories(dir))
                    stack.Push(child);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (failures.All(failure => !string.Equals(failure.Path, dir, StringComparison.OrdinalIgnoreCase)))
                    failures.Add(new DirectoryScanFailure(dir, ex.Message));
            }
        }

        return new VideoScanResult(files, failures);
    }

    public static IEnumerable<string> Enumerate(string root) => Scan(root).Files;

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
