namespace HomePlayer.Core;

public enum LibraryScanStage
{
    Idle,
    Scanning,
    Processing,
    Complete,
    Incomplete,
    Unavailable
}

public sealed record LibraryScanStatus(
    LibraryScanStage Stage,
    int DiscoveredCount,
    int PendingCount,
    IReadOnlyList<DirectoryScanFailure> Failures,
    string? Message)
{
    public static LibraryScanStatus Idle { get; } = new(LibraryScanStage.Idle, 0, 0, [], null);
}
