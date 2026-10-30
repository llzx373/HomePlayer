namespace HomePlayer.Ffmpeg;

public sealed record FfmpegInputProfile(
    bool IsUnc,
    int ProbeSize,
    int AnalyzeDurationMicroseconds,
    int ReadBufferSize,
    int ReadWriteTimeoutMicroseconds)
{
    public static FfmpegInputProfile ForPath(string path)
    {
        if (!IsUncPath(path))
            return new FfmpegInputProfile(false, 0, 0, 0, 0);
        return new FfmpegInputProfile(
            true,
            4 * 1024 * 1024,
            2_000_000,
            1024 * 1024,
            5_000_000);
    }

    public static bool IsUncPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            return true;
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal))
            return false;
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
            return true;
        return Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.IsUnc;
    }
}
