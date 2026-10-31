using HomePlayer.Core;

namespace HomePlayer.Ffmpeg;

public sealed record SeekPreviewResult(
    string Path,
    TimeSpan Target,
    VideoFrame Frame,
    bool CacheHit);

public sealed class SeekPreviewService : IDisposable
{
    private readonly int _cacheCapacity;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _cacheGate = new();
    private readonly Dictionary<string, LinkedListNode<CachedPreview>> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<CachedPreview> _lru = new();
    private FfmpegSeekPreviewExtractor? _extractor;
    private string _extractorKey = "";
    private bool _disposed;

    public SeekPreviewService(int cacheCapacity = 32)
    {
        if (cacheCapacity < 1)
            throw new ArgumentOutOfRangeException(nameof(cacheCapacity));
        _cacheCapacity = cacheCapacity;
    }

    public int CacheCount
    {
        get
        {
            lock (_cacheGate)
                return _cache.Count;
        }
    }

    public async Task<SeekPreviewResult?> CaptureAsync(
        string path,
        TimeSpan target,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        target = TimeSpan.FromTicks(Math.Max(0, target.Ticks));
        var key = await Task.Run(() => MediaKey(path), cancellationToken).ConfigureAwait(false);
        var cacheKey = CacheKey(key, target);
        if (TryGetCached(cacheKey, out var cached))
            return cached;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (TryGetCached(cacheKey, out cached))
                return cached;

            if (!string.Equals(_extractorKey, key, StringComparison.OrdinalIgnoreCase))
            {
                _extractor?.Dispose();
                _extractor = new FfmpegSeekPreviewExtractor(path);
                _extractorKey = key;
            }

            var frame = _extractor!.Capture(target, linked.Token);
            if (frame == null)
                return null;
            var result = new SeekPreviewResult(path, target, frame, false);
            AddToCache(cacheKey, result);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _shutdown.Cancel();
        _gate.Wait();
        try
        {
            _extractor?.Dispose();
            _extractor = null;
            lock (_cacheGate)
            {
                _cache.Clear();
                _lru.Clear();
            }
            _shutdown.Dispose();
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool TryGetCached(string key, out SeekPreviewResult result)
    {
        lock (_cacheGate)
        {
            if (_cache.TryGetValue(key, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                result = node.Value.Result with { CacheHit = true };
                return true;
            }
        }

        result = null!;
        return false;
    }

    private void AddToCache(string key, SeekPreviewResult result)
    {
        lock (_cacheGate)
        {
            if (_cache.TryGetValue(key, out var existing))
            {
                _lru.Remove(existing);
                _cache.Remove(key);
            }

            var node = _lru.AddFirst(new CachedPreview(key, result));
            _cache.Add(key, node);
            while (_cache.Count > _cacheCapacity)
            {
                var last = _lru.Last!;
                _lru.RemoveLast();
                _cache.Remove(last.Value.Key);
            }
        }
    }

    private static string MediaKey(string path)
    {
        var full = Path.GetFullPath(path);
        var info = new FileInfo(full);
        if (!info.Exists)
            throw new FileNotFoundException("预览媒体不存在。", full);
        return $"{full}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
    }

    private static string CacheKey(string mediaKey, TimeSpan target) => $"{mediaKey}|{target.Ticks}";

    private sealed record CachedPreview(string Key, SeekPreviewResult Result);
}
