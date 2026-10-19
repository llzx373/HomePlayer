using System.Diagnostics;
using Microsoft.Win32;

namespace HomePlayer.Playback;

public sealed record LavInstall(string VideoDecoderPath, string? Version);

public static class LavLocator
{
    public static LavInstall? Detect()
    {
        var video = Inproc(new Guid("EE30215D-164F-4A92-A4EB-9D4C13390F9F"));
        var audio = Inproc(new Guid("E8E73B6B-4CB3-44A4-BE99-4F7BCB96E491"));
        var splitter = Inproc(new Guid("B98D13E7-55DB-4385-A33D-09FD1BA26338"));
        if (video == null || audio == null || splitter == null)
            return null;
        string? version = null;
        try
        {
            version = FileVersionInfo.GetVersionInfo(video).FileVersion;
        }
        catch (IOException)
        {
        }

        return new LavInstall(video, version);
    }

    private static string? Inproc(Guid clsid)
    {
        using var key = Registry.ClassesRoot.OpenSubKey($@"CLSID\{{{clsid}}}\InprocServer32");
        var path = key?.GetValue(null) as string;
        return !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? path : null;
    }
}
