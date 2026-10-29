using HomePlayer.Core;
using HomePlayer.Ffmpeg;

namespace HomePlayer;

internal sealed class AssSubtitlePresenter : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<long, SubtitleBitmap> _cache = [];
    private readonly Queue<long> _order = [];
    private FfmpegAssRenderer? _renderer;
    private string? _key;
    private string? _temporaryDirectory;
    private bool _pending;

    public SubtitleBitmap? Current { get; private set; }
    public event Action? Changed;

    public void Update(SubtitleSession subtitles, TimeSpan position, int width, int height)
    {
        if (!subtitles.CurrentIsAss || width <= 0 || height <= 0 || !FfmpegAssRenderer.IsAvailable())
        {
            Reset();
            return;
        }
        var key = $"{subtitles.VideoPath}|{subtitles.AssSidecarPath}|{subtitles.AssEmbeddedIndex}|{width}x{height}";
        var timestamp = (long)Math.Max(0, (position - TimeSpan.FromMilliseconds(subtitles.DelayMilliseconds)).TotalMilliseconds) / 100;
        lock (_gate)
        {
            if (_cache.TryGetValue(timestamp, out var cached))
            {
                Current = cached;
                return;
            }
            if (_pending)
                return;
            _pending = true;
        }
        _ = Task.Run(() => Render(key, subtitles, timestamp, width, height));
    }

    public void Reset()
    {
        lock (_gate)
        {
            _key = null;
            Current = null;
            _cache.Clear();
            _order.Clear();
            _renderer?.Dispose();
            _renderer = null;
            DeleteTemporaryDirectory();
        }
    }

    public void Dispose() => Reset();

    private void Render(string key, SubtitleSession subtitles, long timestamp, int width, int height)
    {
        try
        {
            lock (_gate)
            {
                EnsureRenderer(key, subtitles, width, height);
                var frame = _renderer!.Render(TimeSpan.FromMilliseconds(timestamp * 100));
                _cache[timestamp] = frame;
                _order.Enqueue(timestamp);
                if (_order.Count > 24)
                    _cache.Remove(_order.Dequeue());
                Current = frame;
            }
        }
        catch (Exception ex)
        {
            CrashLog.Write("ASS 渲染失败", ex.ToString());
        }
        finally
        {
            lock (_gate)
                _pending = false;
            Changed?.Invoke();
        }
    }

    private void EnsureRenderer(string key, SubtitleSession subtitles, int width, int height)
    {
        if (_key == key && _renderer != null)
            return;
        _renderer?.Dispose();
        _cache.Clear();
        _order.Clear();
        DeleteTemporaryDirectory();
        var path = subtitles.AssSidecarPath;
        string? fonts = null;
        if (path == null)
        {
            _temporaryDirectory = Path.Combine(Path.GetTempPath(), "HomePlayer.Ass." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_temporaryDirectory);
            path = Path.Combine(_temporaryDirectory, "embedded.ass");
            fonts = Path.Combine(_temporaryDirectory, "fonts");
            FfmpegAssExtraction.Extract(subtitles.VideoPath!, subtitles.AssEmbeddedIndex, path, fonts);
        }
        _renderer = new FfmpegAssRenderer(path, width, height, fonts);
        _key = key;
    }

    private void DeleteTemporaryDirectory()
    {
        if (_temporaryDirectory == null)
            return;
        try
        {
            Directory.Delete(_temporaryDirectory, true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        _temporaryDirectory = null;
    }
}
