using System.Diagnostics;

namespace HomePlayer.Core;

public readonly record struct MediaTimeBase(int Numerator, int Denominator)
{
    public static MediaTimeBase TimeSpanTicks { get; } = new(1, (int)TimeSpan.TicksPerSecond);

    public long ToTimeSpanTicks(long value)
    {
        if (Denominator <= 0 || Numerator <= 0)
            return 0;
        var scaled = (Int128)value * Numerator * TimeSpan.TicksPerSecond;
        var divisor = (Int128)Denominator;
        var rounded = scaled >= 0 ? (scaled + divisor / 2) / divisor : (scaled - divisor / 2) / divisor;
        return (long)rounded;
    }

    public long FromTimeSpanTicks(long ticks)
    {
        if (Denominator <= 0 || Numerator <= 0)
            return 0;
        var scaled = (Int128)ticks * Denominator;
        var divisor = (Int128)Numerator * TimeSpan.TicksPerSecond;
        var rounded = scaled >= 0 ? (scaled + divisor / 2) / divisor : (scaled - divisor / 2) / divisor;
        return (long)rounded;
    }
}

public enum PresentationObservation
{
    Unknown,
    Decoded,
    Submitted,
    PresentReturned,
    GpuCompleted,
    Composed,
    Displayed
}

public enum ZeroCopyLevel
{
    Unknown,
    Z0,
    Z1,
    Z2,
    Z3,
    Z4
}

public enum PlaybackLifecycle
{
    Closed,
    Opening,
    Ready,
    Closing,
    Failed
}

public enum PlaybackTransportState
{
    Stopped,
    Playing,
    Paused,
    Ended
}

public enum PlaybackOperation
{
    None,
    Seeking,
    Recovering,
    TrackChanging
}

public enum PlaybackCommandType
{
    Open,
    Play,
    Pause,
    Stop,
    Seek,
    Close,
    SetAudioTrack,
    SetSubtitleTrack
}

public enum PlaybackCommandStatus
{
    Accepted,
    Completed,
    Cancelled,
    Failed
}

public sealed record PlaybackCommandResult(
    long CommandId,
    Guid SessionId,
    long Epoch,
    PlaybackCommandType CommandType,
    PlaybackCommandStatus Status,
    string? FailureReason = null);

public sealed record PlaybackSessionSnapshot(
    Guid SessionId,
    string MediaId,
    long Epoch,
    string TimelineId,
    PlaybackLifecycle Lifecycle,
    PlaybackTransportState TransportState,
    PlaybackOperation CurrentOperation,
    bool IsBuffering,
    long SurfaceGeneration,
    long DeviceGeneration);

public enum CapabilityStatus
{
    Supported,
    Unavailable,
    Unsupported,
    Unknown
}

public enum CapabilityOwner
{
    Engine,
    Application,
    Renderer,
    ExternalOutput,
    None
}

public sealed record PlaybackCapability(
    string Id,
    CapabilityStatus Status,
    CapabilityOwner Owner,
    string? ReasonCode = null,
    string? Reason = null);

public sealed record PlaybackTraceEvent(
    string Name,
    Guid SessionId,
    long Epoch,
    long? CommandId,
    long Timestamp,
    PresentationObservation Observation = PresentationObservation.Unknown,
    IReadOnlyDictionary<string, object?>? Data = null)
{
    public static long Now => Stopwatch.GetTimestamp();
}
