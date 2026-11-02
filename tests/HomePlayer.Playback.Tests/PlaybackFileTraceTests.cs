using HomePlayer.Core;

namespace HomePlayer.Playback.Tests;

public sealed class PlaybackFileTraceTests
{
    [Fact]
    public void Trace_events_are_flushed_as_json_lines()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"HomePlayer.TraceTests.{Guid.NewGuid():N}");
        var marker = Guid.NewGuid().ToString("N");
        string file;
        using (var trace = new PlaybackFileTrace(directory))
        {
            file = trace.FilePath;
            PlaybackTelemetry.Trace(marker, Guid.Empty, 7, 11, PresentationObservation.PresentReturned,
                new Dictionary<string, object?> { ["width"] = 7680 });
            using var reader = new StreamReader(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
            var text = reader.ReadToEnd();
            Assert.Contains(marker, text);
            Assert.Contains("\"Epoch\":7", text);
            Assert.Contains("\"CommandId\":11", text);
            Assert.Contains("PresentReturned", text);
            Assert.Contains("7680", text);
        }
        Directory.Delete(directory, true);
    }
}
