namespace HomePlayer.Playback;

public enum PlaybackRouteTransitionPhase
{
    Preparing,
    Committed,
    Aborted
}

public sealed record PlaybackTransferState(
    string MediaId,
    TimeSpan Position,
    bool IsPlaying,
    double Volume,
    bool IsMuted,
    double PlaybackRate,
    int AudioDelayMs,
    int? AudioTrackIndex,
    string? AudioTrackStableId);

public sealed record PlaybackRouteTransition(
    long OperationId,
    string MediaId,
    PlaybackRoute Route,
    PlaybackRouteTransitionPhase Phase,
    bool OldSessionPreserved,
    string? FailureReason = null);
