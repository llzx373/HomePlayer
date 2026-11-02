using System.Text.Json;
using HomePlayer.Core;

namespace HomePlayer.Playback;

public sealed class PlaybackFileTrace : IDisposable
{
    private readonly object _gate = new();
    private readonly StreamWriter _writer;
    private bool _disposed;

    public PlaybackFileTrace(string directory)
    {
        Directory.CreateDirectory(directory);
        FilePath = Path.Combine(directory, $"playback-{DateTimeOffset.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.jsonl");
        _writer = new StreamWriter(new FileStream(FilePath, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            AutoFlush = true
        };
        PlaybackTelemetry.Traced += OnTraced;
    }

    public string FilePath { get; }

    public void Dispose()
    {
        PlaybackTelemetry.Traced -= OnTraced;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _writer.Dispose();
        }
    }

    private void OnTraced(PlaybackTraceEvent item)
    {
        try
        {
            lock (_gate)
            {
                if (_disposed)
                    return;
                _writer.WriteLine(JsonSerializer.Serialize(new
                {
                    utc = DateTimeOffset.UtcNow,
                    item.Name,
                    item.SessionId,
                    item.Epoch,
                    item.CommandId,
                    item.Timestamp,
                    Observation = item.Observation.ToString(),
                    item.Data
                }));
            }
        }
        catch
        {
        }
    }
}
