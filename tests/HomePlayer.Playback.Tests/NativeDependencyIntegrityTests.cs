using HomePlayer.Core;

namespace HomePlayer.Playback.Tests;

public sealed class NativeDependencyIntegrityTests
{
    [Fact]
    public void Private_dependencies_match_the_distributed_manifest()
    {
        NativeDependencyIntegrity.Verify(Path.Combine(AppContext.BaseDirectory, "ffmpeg"), "ffmpeg", ["avcodec-63.dll"]);
        NativeDependencyIntegrity.Verify(Path.Combine(AppContext.BaseDirectory, "libplacebo"), "libplacebo", ["libplacebo-360.dll"]);
    }

    [Fact]
    public void Unregistered_file_is_rejected()
    {
        Assert.Throws<InvalidOperationException>(() =>
            NativeDependencyIntegrity.Verify(AppContext.BaseDirectory, "ffmpeg", ["unregistered.dll"]));
    }
}
