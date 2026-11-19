using System.Diagnostics;
using HomePlayer.Core;

namespace HomePlayer.Ffmpeg;

public sealed unsafe partial class FfmpegPlayer
{
    private long _prerollVideoReadyEpoch = -1;
    private long _prerollAudioReadyEpoch = -1;
    private long _prerollResumingEpoch = -1;
    private long _prerollReleasedEpoch = -1;
    private long _audioReadyEpoch = -1;
    private int _clockArmed;

    public event Action<long>? SeekPrerollReady;

    /// <summary>
    /// 时钟还没对齐过当前时间线时，WASAPI 先播会把设备时钟跑在画面前面。
    /// 暂停后恢复时时钟已经对齐，不能再推迟。
    /// </summary>
    public bool DeferAudioOutput
    {
        get
        {
            lock (_gate)
                return _clockArmed == 0;
        }
    }

    private void ResetSeekPrerollLocked()
    {
        _prerollVideoReadyEpoch = -1;
        _prerollAudioReadyEpoch = -1;
        _prerollResumingEpoch = -1;
        _prerollReleasedEpoch = -1;
        _audioReadyEpoch = -1;
        _clockArmed = 0;
    }

    private bool WaitForSeekPreroll(FrameLease frame, bool targetFrame)
    {
        if (!targetFrame)
            return true;
        var epoch = frame.Epoch;
        var started = Stopwatch.GetTimestamp();
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
                    _clockArmed = 1;
                    return true;
                }
                _prerollVideoReadyEpoch = epoch;
                release = CanReleaseSeekPrerollLocked(epoch);
            }
            if (release || Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(2))
            {
                ReleaseSeekPreroll(epoch);
                return true;
            }
            Thread.Sleep(2);
        }
        return false;
    }

    private void MarkSeekPrerollAudioReady(long epoch)
    {
        var release = false;
        lock (_gate)
        {
            if (epoch == Epoch)
                _audioReadyEpoch = epoch;
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
            _clockArmed = 1;
            _prerollReleasedEpoch = epoch;
        }
    }

    /// <summary>
    /// 从头播放没有 seek 目标帧。按住第一帧，直到音频写出或超过 2 秒，再启动时钟。
    /// 2 秒是 seek 预滚动同一上限：调度线程拿着帧等待时，视频队列会堵住 demux，不能无限等。
    /// </summary>
    private bool WaitForStartupClock(FrameLease frame, bool targetFrame, out bool armedThisFrame)
    {
        armedThisFrame = false;
        if (targetFrame || Volatile.Read(ref _clockArmed) == 1)
            return true;
        var started = Stopwatch.GetTimestamp();
        var timing = false;
        while (_cancel is { IsCancellationRequested: false })
        {
            if (frame.Epoch != Epoch)
                return false;
            lock (_gate)
            {
                if (frame.Epoch != Epoch)
                    return false;
                if (_clockArmed == 1 || _step)
                    return true;
                if (_isPlaying)
                {
                    if (!timing)
                    {
                        started = Stopwatch.GetTimestamp();
                        timing = true;
                    }
                    var audioReady = !HasAudio || _audioReadyEpoch == frame.Epoch || Volatile.Read(ref _audioDone) == 1;
                    if (audioReady || Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(2))
                    {
                        var ticks = Math.Max(0, frame.TimeBase.ToTimeSpanTicks(frame.PresentationTimestamp));
                        _clockOriginTicks = ticks;
                        Volatile.Write(ref _positionTicks, ticks);
                        _clock.Restart();
                        _clockArmed = 1;
                        armedThisFrame = true;
                    }
                }
                else
                {
                    timing = false;
                }
            }
            if (armedThisFrame)
            {
                SeekPrerollReady?.Invoke(frame.Epoch);
                return true;
            }
            Thread.Sleep(2);
        }
        return false;
    }
}
