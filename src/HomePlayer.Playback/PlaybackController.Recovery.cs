using HomePlayer.Core;

namespace HomePlayer.Playback;

public sealed partial class PlaybackController
{
    private const int MaxRecoveryAttempts = 2;
    private string? _recoveryPath;
    private int _recoveryAttempts;

    private bool TryRecoverFromFault(Exception error)
    {
        if (_engine == null || Playlist.Current == null || _recoveryAttempts >= MaxRecoveryAttempts)
            return false;
        _recoveryAttempts++;
        var route = error is LavVideoOutputException
            ? PlaybackRoute.NativeFfmpeg
            : _recoveryAttempts == 1
                ? InnerEngine(_engine) is FfmpegPlaybackEngine ? PlaybackRoute.NativeFfmpeg : PlaybackRoute.DirectShow
                : PlaybackRoute.Auto;
        var session = _engine as PlaybackSession;
        PlaybackTelemetry.Trace("PlaybackRecoveryStarted", session?.SessionId ?? Guid.Empty, session?.Epoch ?? 0,
            data: new Dictionary<string, object?>
            {
                ["route"] = route.ToString(),
                ["attempt"] = _recoveryAttempts,
                ["error"] = error.Message
            });
        Status = error is LavVideoOutputException
            ? "视频输出失败，正在改用 FFmpeg"
            : _recoveryAttempts == 1
                ? "播放中断，正在按原路线恢复"
                : "原路线恢复失败，正在自动选择路线";
        Notify();
        _ = PlayCurrentAsync(route, recovery: true);
        return true;
    }

    private void ResetRecovery()
    {
        _recoveryPath = null;
        _recoveryAttempts = 0;
    }
}
