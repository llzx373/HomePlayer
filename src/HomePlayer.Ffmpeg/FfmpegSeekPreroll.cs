using HomePlayer.Core;

namespace HomePlayer.Ffmpeg;

public sealed unsafe partial class FfmpegPlayer
{
    private long _prerollVideoReadyEpoch = -1;
    private long _prerollAudioReadyEpoch = -1;
    private long _prerollResumingEpoch = -1;
    private long _prerollReleasedEpoch = -1;

    public event Action<long>? SeekPrerollReady;

    private void ResetSeekPrerollLocked()
    {
        _prerollVideoReadyEpoch = -1;
        _prerollAudioReadyEpoch = -1;
        _prerollResumingEpoch = -1;
        _prerollReleasedEpoch = -1;
    }

    private bool WaitForSeekPreroll(FrameLease frame, bool targetFrame)
    {
        if (!targetFrame)
            return true;
        var epoch = frame.Epoch;
        while (_cancel is { IsCancellationRequested: false })
        {
            var release = false;
            lock (_gate)
            {
                if (frame.Epoch != Epoch)
                    return false;
                var seek = _activeSeek?.Epoch == epoch ? _activeSeek : null;
                if (seek == null || _prerollReleasedEpoch == epoch)
                    return true;
                if (!_playIntent)
                {
                    _prerollReleasedEpoch = epoch;
                    return true;
                }
                _prerollVideoReadyEpoch = epoch;
                release = CanReleaseSeekPrerollLocked(epoch);
            }
            if (release)
                ReleaseSeekPreroll(epoch);
            Thread.Sleep(2);
        }
        return false;
    }

    private void MarkSeekPrerollAudioReady(long epoch)
    {
        var release = false;
        lock (_gate)
        {
            if (_activeSeek?.Epoch == epoch && _playIntent)
            {
                _prerollAudioReadyEpoch = epoch;
                release = CanReleaseSeekPrerollLocked(epoch);
            }
        }
        if (release)
            ReleaseSeekPreroll(epoch);
    }

    private void CompleteSeekPrerollAtEndOfStream(long epoch)
    {
        var release = false;
        lock (_gate)
        {
            if (_activeSeek?.Epoch == epoch && _playIntent)
            {
                _prerollVideoReadyEpoch = epoch;
                _prerollAudioReadyEpoch = epoch;
                release = CanReleaseSeekPrerollLocked(epoch);
            }
        }
        if (release)
            ReleaseSeekPreroll(epoch);
    }

    private bool CanReleaseSeekPrerollLocked(long epoch) =>
        _activeSeek?.Epoch == epoch
        && _playIntent
        && _prerollVideoReadyEpoch == epoch
        && (_prerollAudioReadyEpoch == epoch || !HasAudio || Volatile.Read(ref _audioDone) == 1)
        && _prerollResumingEpoch != epoch
        && _prerollReleasedEpoch != epoch;

    private void ReleaseSeekPreroll(long epoch)
    {
        SeekRequest? seek;
        lock (_gate)
        {
            seek = _activeSeek?.Epoch == epoch ? _activeSeek : null;
            if (seek == null || !_playIntent || _prerollResumingEpoch == epoch || _prerollReleasedEpoch == epoch)
                return;
            _prerollResumingEpoch = epoch;
        }

        SeekPrerollReady?.Invoke(epoch);
        lock (_gate)
        {
            if (_activeSeek != seek || !_playIntent)
                return;
            _clockOriginTicks = seek.Position.Ticks;
            _clock.Restart();
            _prerollReleasedEpoch = epoch;
        }
    }
}
