using System.Diagnostics;

namespace HomePlayer.Ffmpeg;

public sealed partial class FfmpegPlayer
{
    private Thread? _watchdogThread;
    private long _lastProgressTimestamp;
    private int _watchdogTripped;

    public TimeSpan WatchdogTimeout { get; set; } = TimeSpan.FromSeconds(10);

    private void StartWatchdog()
    {
        MarkPlaybackProgress();
        Interlocked.Exchange(ref _watchdogTripped, 0);
        _watchdogThread = StartThread(WatchdogLoop, "FFmpeg playback watchdog");
    }

    private void MarkPlaybackProgress() =>
        Interlocked.Exchange(ref _lastProgressTimestamp, Stopwatch.GetTimestamp());

    private void WatchdogLoop()
    {
        try
        {
            while (_cancel is { IsCancellationRequested: false })
            {
                var timeout = WatchdogTimeout;
                if (_isPlaying && !_endedRaised && timeout > TimeSpan.Zero
                    && Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastProgressTimestamp)) > timeout
                    && Interlocked.CompareExchange(ref _watchdogTripped, 1, 0) == 0)
                {
                    Interlocked.Exchange(ref _ioInterrupt, 1);
                    _cancel.Cancel();
                    RaiseFaulted(new InvalidOperationException($"播放看门狗超时：连续 {timeout.TotalSeconds:0.###} 秒没有完成音频或视频交付，已中断本次会话。"));
                    return;
                }
                Thread.Sleep(100);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
