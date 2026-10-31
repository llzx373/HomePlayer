using HomePlayer.Playback;

namespace HomePlayer.Playback.Tests;

public sealed class PlaybackTelemetryPrivacyTests
{
    [Fact]
    public void Trace_is_sanitized_before_subscription_and_export()
    {
        PlaybackTelemetry.Reset();
        string? observed = null;
        void Handler(HomePlayer.Core.PlaybackTraceEvent item)
        {
            if (item.Data?.TryGetValue("url", out var value) == true)
                observed = value as string;
        }
        PlaybackTelemetry.Traced += Handler;
        try
        {
            PlaybackTelemetry.Trace("privacy", Guid.NewGuid(), 0, data: new Dictionary<string, object?>
            {
                ["url"] = "https://user:pass@example.com/v?token=secret"
            });
            var exported = PlaybackTelemetry.ExportJson();
            Assert.Equal("https://***@example.com/v?token=***", observed);
            Assert.DoesNotContain("user:pass", exported);
            Assert.DoesNotContain("secret", exported);
        }
        finally
        {
            PlaybackTelemetry.Traced -= Handler;
            PlaybackTelemetry.Reset();
        }
    }
}
