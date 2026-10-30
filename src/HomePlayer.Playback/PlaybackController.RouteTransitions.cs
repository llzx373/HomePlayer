using HomePlayer.Core;

namespace HomePlayer.Playback;

public sealed partial class PlaybackController
{
    private long _routeOperationId;
    private RouteSwitchOperation? _activeRouteSwitch;
    private PlaybackRouteTransition? _failedRouteTransition;

    public PlaybackRouteTransition? CurrentRouteTransition { get; private set; }
    public PlaybackTransferState? LastTransferState { get; private set; }

    public async Task<bool> SwitchRouteAsync(PlaybackRoute route)
    {
        if (route == PlaybackRoute.Auto || Playlist.Current == null)
            return false;
        var mediaId = Playlist.Current.Path;
        var transfer = new PlaybackTransferState(
            mediaId,
            Position,
            IsPlaying,
            Volume,
            Muted,
            Rate,
            AudioDelayMs,
            AudioTrackIndex,
            GetAudioTracks().FirstOrDefault(track => track.Index == AudioTrackIndex)?.StableId);
        LastTransferState = transfer;
        SavePlaybackPreference();
        if (AudioTrackIndex >= 0)
            SaveAudioPreference(AudioTrackIndex);
        var operationId = ++_routeOperationId;
        _activeRouteSwitch?.Complete(false, "被新的路线切换取代。");
        var operation = new RouteSwitchOperation(this, operationId, mediaId, route);
        _activeRouteSwitch = operation;
        CurrentRouteTransition = new PlaybackRouteTransition(operationId, mediaId, route, PlaybackRouteTransitionPhase.Preparing, false);
        Status = "正在准备路线切换";
        Notify();
        await PlayCurrentAsync(route, routeSwitch: operation);
        return await operation.Completion.Task;
    }

    public Task<bool> RetryRouteSwitchAsync()
    {
        var failed = _failedRouteTransition;
        if (failed == null || !string.Equals(Playlist.Current?.Path, failed.MediaId, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(false);
        _failedRouteTransition = null;
        return SwitchRouteAsync(failed.Route);
    }

    private void CompleteRouteSwitch(RouteSwitchOperation operation, bool succeeded, string? reason)
    {
        if (operation.Id != _routeOperationId)
            return;
        var phase = succeeded ? PlaybackRouteTransitionPhase.Committed : PlaybackRouteTransitionPhase.Aborted;
        var transition = new PlaybackRouteTransition(operation.Id, operation.MediaId, operation.Route, phase, false, reason);
        CurrentRouteTransition = transition;
        _failedRouteTransition = succeeded ? null : transition;
        if (ReferenceEquals(_activeRouteSwitch, operation))
            _activeRouteSwitch = null;
        Status = succeeded ? "路线切换完成" : "路线切换失败，可重试：" + reason;
        Notify();
    }

    private void InvalidateRouteSwitch()
    {
        _routeOperationId++;
        _activeRouteSwitch?.Complete(false, "被新的打开操作取代。");
        _activeRouteSwitch = null;
        _failedRouteTransition = null;
        CurrentRouteTransition = null;
    }

    private sealed class RouteSwitchOperation(
        PlaybackController owner,
        long id,
        string mediaId,
        PlaybackRoute route)
    {
        public long Id { get; } = id;
        public string MediaId { get; } = mediaId;
        public PlaybackRoute Route { get; } = route;
        public TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Complete(bool succeeded, string? reason = null)
        {
            if (!Completion.TrySetResult(succeeded))
                return;
            owner.CompleteRouteSwitch(this, succeeded, reason);
        }
    }
}
