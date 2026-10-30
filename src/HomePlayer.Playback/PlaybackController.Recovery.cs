using HomePlayer.Core;

namespace HomePlayer.Playback;

public sealed partial class PlaybackController
{
    private const int MaxRecoveryAttempts = 2;
    private string? _recoveryPath;
    private int _recoveryAttempts;

    private bool TryRecoverFromFault()
    {
        if (_engine == null || Playlist.Current == null || _recoveryAttempts >= MaxRecoveryAttempts)
            return false;
        _recoveryAttempts++;
        var route = _recoveryAttempts == 1
            ? InnerEngine(_engine) is FfmpegPlaybackEngine ? PlaybackRoute.NativeFfmpeg : PlaybackRoute.DirectShow
            : PlaybackRoute.Auto;
        Status = _recoveryAttempts == 1
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
