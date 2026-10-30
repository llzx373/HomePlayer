using HomePlayer.Core;
using HomePlayer.Ffmpeg;

namespace HomePlayer.Playback;

public sealed partial class PlaybackController
{
    private readonly Func<string, CancellationToken, Task<MediaProbe>> _probeNext;
    private CancellationTokenSource? _nextProbeStop;
    private int _nextProbeGeneration;

    public MediaProbe? NextProbe { get; private set; }
    public string? NextProbePath { get; private set; }
    public event Action<string, MediaProbe>? NextProbeAvailable;

    private static Task<MediaProbe> ProbeNextDefault(string path, CancellationToken cancellationToken) =>
        Task.Run(() => FfmpegProbe.Read(path, cancellationToken), cancellationToken);

    private void ScheduleNextProbe(int token)
    {
        CancelNextProbe();
        var index = Playlist.PeekNextIndex();
        if (index is not int nextIndex || nextIndex == Playlist.CurrentIndex)
            return;
        var path = Playlist.Items[nextIndex].Path;
        var generation = ++_nextProbeGeneration;
        var stop = CancellationTokenSource.CreateLinkedTokenSource(_flushStop.Token);
        var cancellationToken = stop.Token;
        stop.CancelAfter(TimeSpan.FromSeconds(5));
        _nextProbeStop = stop;
        _ = Task.Run(async () =>
        {
            try
            {
                var probe = await _probeNext(path, cancellationToken).ConfigureAwait(false);
                _post(() =>
                {
                    if (token != _token || generation != _nextProbeGeneration || cancellationToken.IsCancellationRequested)
                        return;
                    if (Playlist.PeekNextIndex() != nextIndex
                        || !string.Equals(Playlist.Items[nextIndex].Path, path, StringComparison.OrdinalIgnoreCase))
                        return;
                    NextProbePath = path;
                    NextProbe = probe;
                    NextProbeAvailable?.Invoke(path, probe);
                });
            }
            catch
            {
            }
            finally
            {
                stop.Dispose();
            }
        });
    }

    private void CancelNextProbe()
    {
        _nextProbeGeneration++;
        NextProbe = null;
        NextProbePath = null;
        var stop = _nextProbeStop;
        _nextProbeStop = null;
        if (stop == null)
            return;
        try
        {
            stop.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
