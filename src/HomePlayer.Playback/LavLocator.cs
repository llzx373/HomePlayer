using System.Diagnostics;

namespace HomePlayer.Playback;

public sealed record LavInstall(string VideoDecoderPath, string? Version);

public static class LavLocator
{
    public static LavInstall? Detect()
    {
        var installed = CodecCatalog.Scan();
        var video = installed.FirstOrDefault(item => item.Usable && item.Clsid == DsGuid.LavVideo)?.Path;
        var audio = installed.FirstOrDefault(item => item.Usable && item.Clsid == DsGuid.LavAudio)?.Path;
        var splitter = installed.FirstOrDefault(item => item.Usable && item.Clsid == DsGuid.LavSplitterSource)?.Path;
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
}
