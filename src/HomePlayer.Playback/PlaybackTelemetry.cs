using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using HomePlayer.Core;

namespace HomePlayer.Playback;

public static class PlaybackTelemetry
{
    private const int MaxEvents = 4096;
    private static readonly ConcurrentQueue<PlaybackTraceEvent> Events = new();
    private static readonly ConcurrentDictionary<Guid, int> FirstObservations = new();
    private static readonly ConcurrentDictionary<(long CommandId, int Observation), byte> SeekObservations = new();
    private static int _frameSampling = 30;

    public static event Action<PlaybackTraceEvent>? Traced;

    public static int FrameSampling
    {
        get => Math.Max(1, Volatile.Read(ref _frameSampling));
        set => Volatile.Write(ref _frameSampling, Math.Max(1, value));
    }

    public static void Trace(
        string name,
        Guid sessionId,
        long epoch,
        long? commandId = null,
        PresentationObservation observation = PresentationObservation.Unknown,
        IReadOnlyDictionary<string, object?>? data = null)
    {
        var item = new PlaybackTraceEvent(name, sessionId, epoch, commandId, Stopwatch.GetTimestamp(), observation, data);
        Events.Enqueue(item);
        while (Events.Count > MaxEvents && Events.TryDequeue(out _))
        {
        }
        Traced?.Invoke(item);
    }

    public static void Observe(VideoFrame frame, PresentationObservation observation)
    {
        var now = Stopwatch.GetTimestamp();
        var sampled = frame.FrameSequence % FrameSampling == 0;
        if (sampled && frame.DecodedTimestamp > 0 && observation >= PresentationObservation.Submitted)
        {
            PerformanceMetrics.Record(
                observation == PresentationObservation.Submitted ? "playback.submit-latency" : "playback.present-latency",
                Stopwatch.GetElapsedTime(frame.DecodedTimestamp, now).TotalMilliseconds);
        }

        var key = observation == PresentationObservation.Submitted ? 1 : 2;
        if (frame.OpenedTimestamp > 0 && FirstObservations.GetOrAdd(frame.SessionId, 0) is var previous)
        {
            var expected = observation == PresentationObservation.Submitted ? 0 : 1;
            if (previous == expected && FirstObservations.TryUpdate(frame.SessionId, key, expected))
            {
                PerformanceMetrics.Record(
                    observation == PresentationObservation.Submitted
                        ? "playback.first-submitted-frame"
                        : "playback.first-present-returned",
                    Stopwatch.GetElapsedTime(frame.OpenedTimestamp, now).TotalMilliseconds);
            }
        }

        var targetFrame = frame.SeekStartedTimestamp > 0
            && frame.SeekCommandId is long commandId
            && SeekObservations.TryAdd((commandId, (int)observation), 0);
        if (targetFrame)
        {
            PerformanceMetrics.Record(
                observation == PresentationObservation.Submitted
                    ? "playback.seek-submitted"
                    : "playback.seek-present-returned",
                Stopwatch.GetElapsedTime(frame.SeekStartedTimestamp, now).TotalMilliseconds);
        }

        if (sampled || targetFrame)
        {
            Trace(
                observation == PresentationObservation.Submitted ? "FrameSubmitted" : "FramePresentReturned",
                frame.SessionId,
                frame.Epoch,
                frame.SeekCommandId,
                observation,
                new Dictionary<string, object?>
                {
                    ["pts"] = frame.PresentationTimestamp,
                    ["timeBase"] = $"{frame.TimeBase.Numerator}/{frame.TimeBase.Denominator}",
                    ["frameSequence"] = frame.FrameSequence
                });
        }
    }

    public static IReadOnlyList<PlaybackTraceEvent> Snapshot() => Events.ToArray();

    public static string ExportJson() => JsonSerializer.Serialize(Snapshot(), new JsonSerializerOptions { WriteIndented = true });

    public static void Reset()
    {
        Events.Clear();
        FirstObservations.Clear();
        SeekObservations.Clear();
    }
}
