using HomePlayer.Core;

namespace HomePlayer.Core.Tests;

public sealed class DiagnosticTextTests
{
    [Fact]
    public void Network_credentials_and_signature_parameters_are_redacted()
    {
        var text = DiagnosticText.Sanitize("https://user:pass@example.com/v.mkv?token=secret&x=1");

        Assert.Equal("https://***@example.com/v.mkv?token=***&x=1", text);
        var unc = DiagnosticText.Sanitize(@"\\user:pass@server\share\video.mkv");
        Assert.Equal(@"\\***@server\share\video.mkv", unc);
    }

    [Fact]
    public void Local_paths_and_ordinary_diagnostics_are_unchanged()
    {
        const string text = @"F:\媒体\sample.mkv；错误码 -1094995529";
        Assert.Equal(text, DiagnosticText.Sanitize(text));
    }

    [Fact]
    public void Crash_and_pipeline_exports_do_not_retain_secrets()
    {
        var crash = CrashLog.Format("test", "https://user:pass@example.com/v?api_key=secret", DateTimeOffset.UnixEpoch, null);
        var pipeline = new PipelineInfo
        {
            FilePath = "https://user:pass@example.com/v?signature=secret",
            Usage = ["decoder https://user:pass@example.com/decoder"]
        }.Format();
        foreach (var text in new[] { crash, pipeline })
        {
            Assert.DoesNotContain("user:pass", text);
            Assert.DoesNotContain("secret", text);
            Assert.Contains("***", text);
        }
    }
}
