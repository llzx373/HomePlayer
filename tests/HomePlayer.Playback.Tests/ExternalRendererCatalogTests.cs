using HomePlayer.Playback;

namespace HomePlayer.Playback.Tests;

public sealed class ExternalRendererCatalogTests
{
    [Fact]
    public void Missing_files_and_registration_report_not_installed()
    {
        var result = ExternalRendererCatalog.Detect(ExternalRendererKind.MpcVR, new FakePlatform());

        Assert.Equal(ExternalRendererStatus.NotInstalled, result.Status);
        Assert.False(result.Available);
        Assert.Equal("not-installed", result.ReasonCode);
    }

    [Fact]
    public void Installation_files_without_registration_are_not_available()
    {
        var platform = new FakePlatform { Candidate = @"F:\Program Files\madVR09217\madVR64.ax" };
        platform.Files.Add(platform.Candidate);

        var result = ExternalRendererCatalog.Detect(ExternalRendererKind.MadVR, platform);

        Assert.Equal(ExternalRendererStatus.FilesOnly, result.Status);
        Assert.False(result.Available);
        Assert.Equal(platform.Candidate, result.InstallPath);
        Assert.Equal("not-registered", result.ReasonCode);
        Assert.Equal(0, platform.Activations);
    }

    [Fact]
    public void Broken_registration_does_not_fall_back_to_unregistered_activation()
    {
        var platform = new FakePlatform
        {
            Candidate = @"F:\Program Files\madVR09217\madVR64.ax",
            Registered = @"F:\missing\madVR64.ax"
        };
        platform.Files.Add(platform.Candidate);

        var result = ExternalRendererCatalog.Detect(ExternalRendererKind.MadVR, platform);

        Assert.Equal(ExternalRendererStatus.Unavailable, result.Status);
        Assert.Equal("registration-missing-file", result.ReasonCode);
        Assert.Equal(0, platform.Activations);
    }

    [Fact]
    public void Activation_failure_is_observable()
    {
        var platform = new FakePlatform { Candidate = @"F:\madVR\madVR64.ax", Registered = @"F:\madVR\madVR64.ax" };
        platform.Files.Add(platform.Candidate);
        platform.ActivationError = new UnauthorizedAccessException("denied");

        var result = ExternalRendererCatalog.Detect(ExternalRendererKind.MadVR, platform);

        Assert.Equal(ExternalRendererStatus.Unavailable, result.Status);
        Assert.Equal("activation-failed", result.ReasonCode);
        Assert.Contains("denied", result.Reason);
        Assert.Equal(1, platform.Activations);
    }

    [Fact]
    public void Successful_x64_activation_is_available()
    {
        var platform = new FakePlatform { Candidate = @"F:\madVR\madVR64.ax", Registered = @"F:\madVR\madVR64.ax" };
        platform.Files.Add(platform.Candidate);

        var result = ExternalRendererCatalog.Detect(ExternalRendererKind.MadVR, platform);

        Assert.True(result.Available);
        Assert.Equal("available", result.ReasonCode);
        Assert.Equal("0.92.17", result.Version);
        Assert.Equal(ExternalRendererCatalog.MadVRClsid, result.Clsid);
        Assert.Equal(1, platform.Activations);
    }

    private sealed class FakePlatform : IExternalRendererPlatform
    {
        public string Candidate { get; init; } = "";
        public string? Registered { get; init; }
        public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Exception? ActivationError { get; set; }
        public int Activations { get; private set; }

        public string? RegisteredPath(Guid clsid) => Registered;
        public IEnumerable<string> CandidatePaths(ExternalRendererKind kind)
        {
            if (Candidate.Length > 0)
                yield return Candidate;
        }
        public bool FileExists(string path) => Files.Contains(path);
        public string? Version(string path) => "0.92.17";
        public object CreateInstance(Guid clsid)
        {
            Activations++;
            if (ActivationError != null)
                throw ActivationError;
            return new object();
        }
    }
}
