using System.Collections.Concurrent;
using System.Threading.Channels;
using HomePlayer.Core;
using HomePlayer.Data;
using HomePlayer.Ffmpeg;

namespace HomePlayer.Library;

public sealed class LibraryCoordinator : IAsyncDisposable
{
    private readonly LibraryRepository _repository;
    private readonly TmdbClient _tmdb;
    private readonly Func<string?> _apiKey;
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>();
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CancellationTokenSource> _debounce = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pending = new(StringComparer.Ordinal);
    private readonly HashSet<string> _inFlight = new(StringComparer.Ordinal);
    private readonly HashSet<string> _resubmit = new(StringComparer.Ordinal);
    private readonly object _queuedGate = new();
    private readonly ConcurrentDictionary<string, object> _libraryLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly List<Task> _workers = [];
    private readonly HashSet<Task> _tasks = [];
    private readonly object _tasksGate = new();
    private readonly SemaphoreSlim _slotSignal = new(0);
    private readonly object _slot = new();
    private int _active;
    private int _maxParallel = 2;

    public LibraryCoordinator(LibraryRepository repository, Func<string?> apiKey)
    {
        _repository = repository;
        _tmdb = new TmdbClient(repository);
        _apiKey = apiKey;
    }

    public event EventHandler? Changed;

    public int PendingCount => _repository.PendingCount();

    public void Start()
    {
        foreach (var library in _repository.ListLibraries())
            Watch(library);
        foreach (var id in _repository.ListQueued())
            Submit(id);
        for (var i = 0; i < 2; i++)
            _workers.Add(Task.Run(() => WorkerAsync(_shutdown.Token)));
        foreach (var library in _repository.ListLibraries())
            ReconcileInBackground(library.Id);
    }

    public void SetPlaybackActive(bool active)
    {
        Volatile.Write(ref _maxParallel, active ? 1 : 2);
        try
        {
            if (_slotSignal.CurrentCount == 0)
                _slotSignal.Release();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public MediaLibrary Add(string name, string root, LibraryKind kind)
    {
        var full = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Directory.Exists(full))
            throw new InvalidOperationException("文件夹不存在。");
        if (_repository.ListLibraries().Any(item => string.Equals(item.RootPath, full, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("这个文件夹已经是一个媒体库。");
        var library = new MediaLibrary
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = string.IsNullOrWhiteSpace(name) ? Path.GetFileName(full) : name.Trim(),
            RootPath = full,
            Kind = kind
        };
        _repository.InsertLibrary(library);
        Watch(library);
        ReconcileInBackground(library.Id);
        Changed?.Invoke(this, EventArgs.Empty);
        return library;
    }

    public async Task RemoveAsync(string libraryId)
    {
        if (_watchers.Remove(libraryId, out var watcher))
            watcher.Dispose();
        CancellationTokenSource? debounce = null;
        lock (_debounce)
            _debounce.Remove(libraryId, out debounce);
        if (debounce != null)
        {
            debounce.Cancel();
            debounce.Dispose();
        }

        await Task.Run(() =>
        {
            lock (LibraryLock(libraryId))
            {
                var library = _repository.GetLibrary(libraryId);
                if (library != null)
                {
                    foreach (var item in _repository.ListItems(libraryId))
                        TryDeleteDirectory(Path.Combine(AppPaths.Thumbs, item.Id));
                }

                _repository.DeleteLibrary(libraryId);
                _libraryLocks.TryRemove(libraryId, out _);
            }
        });

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Refresh(string libraryId) => ReconcileInBackground(libraryId);

    public void Requeue(string itemId)
    {
        var item = _repository.GetItem(itemId);
        var library = item == null ? null : _repository.GetLibrary(item.LibraryId);
        if (item == null || library == null)
            return;
        var full = item.FullPath(library.RootPath);
        long size;
        long ticks;
        try
        {
            var info = new FileInfo(full);
            size = info.Length;
            ticks = SecondTicks(info.LastWriteTimeUtc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        _repository.MarkChanged(item, size, ticks);
        _repository.Enqueue(item.Id, library.Kind);
        Submit(item.Id);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public long CacheBytes()
    {
        if (!Directory.Exists(AppPaths.Cache))
            return 0;
        try
        {
            return Directory.EnumerateFiles(AppPaths.Cache, "*", SearchOption.AllDirectories)
                .Sum(file =>
                {
                    try
                    {
                        return new FileInfo(file).Length;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        return 0;
                    }
                });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    public async Task ClearCacheAsync()
    {
        await Task.Run(() =>
        {
            TryDeleteDirectory(AppPaths.Cache);
            AppPaths.Ensure();
            foreach (var library in _repository.ListLibraries())
            {
                if (_shutdown.IsCancellationRequested)
                    return;
                foreach (var item in _repository.ListItems(library.Id))
                {
                    if (_shutdown.IsCancellationRequested)
                        return;
                    _repository.MarkChanged(item, item.Size, item.MtimeUtcTicks);
                    _repository.Enqueue(item.Id, library.Kind);
                    Submit(item.Id);
                }
            }
        });

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        _queue.Writer.TryComplete();
        foreach (var watcher in _watchers.Values)
            watcher.Dispose();
        _watchers.Clear();
        lock (_debounce)
        {
            foreach (var source in _debounce.Values)
            {
                source.Cancel();
                source.Dispose();
            }

            _debounce.Clear();
        }

        try
        {
            await Task.WhenAll(_workers);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
        }

        Task[] tasks;
        lock (_tasksGate)
            tasks = [.. _tasks];
        try
        {
            if (tasks.Length > 0)
                await Task.WhenAll(tasks);
        }
        catch
        {
        }

        _slotSignal.Dispose();
        _shutdown.Dispose();
    }

    private void Watch(MediaLibrary library)
    {
        if (!Directory.Exists(library.RootPath))
            return;
        var watcher = new FileSystemWatcher(library.RootPath)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            InternalBufferSize = 64 * 1024,
            EnableRaisingEvents = true
        };
        FileSystemEventHandler onChange = (_, _) => Debounce(library.Id);
        watcher.Created += onChange;
        watcher.Changed += onChange;
        watcher.Deleted += onChange;
        watcher.Renamed += (_, _) => Debounce(library.Id);
        watcher.Error += (_, _) => ReconcileInBackground(library.Id);
        _watchers[library.Id] = watcher;
    }

    private void Debounce(string libraryId)
    {
        lock (_debounce)
        {
            if (_debounce.Remove(libraryId, out var previous))
            {
                previous.Cancel();
                previous.Dispose();
            }

            var source = new CancellationTokenSource();
            _debounce[libraryId] = source;
            Track(Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(2000, source.Token);
                    Reconcile(libraryId);
                }
                catch (OperationCanceledException)
                {
                }
                finally
                {
                    lock (_debounce)
                    {
                        if (_debounce.TryGetValue(libraryId, out var current) && ReferenceEquals(current, source))
                            _debounce.Remove(libraryId);
                    }

                    source.Dispose();
                }
            }));
        }
    }

    private void ReconcileInBackground(string libraryId) => Track(Task.Run(() => Reconcile(libraryId)));

    private void Reconcile(string libraryId)
    {
        lock (LibraryLock(libraryId))
        {
            if (_shutdown.IsCancellationRequested)
                return;
            try
            {
                ReconcileCore(libraryId);
            }
            catch (Exception)
            {
            }
        }
    }

    private void ReconcileCore(string libraryId)
    {
        var library = _repository.GetLibrary(libraryId);
        if (library == null || !Directory.Exists(library.RootPath))
            return;
        var existing = _repository.ListItems(libraryId);
        var byPath = existing.ToDictionary(item => item.RelativePath, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in VideoFiles.Enumerate(library.RootPath))
        {
            if (_shutdown.IsCancellationRequested)
                return;
            var relative = Path.GetRelativePath(library.RootPath, file);
            seen.Add(relative);
            long size;
            long ticks;
            try
            {
                var info = new FileInfo(file);
                size = info.Length;
                ticks = SecondTicks(info.LastWriteTimeUtc);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                continue;
            }

            if (!byPath.TryGetValue(relative, out var item))
            {
                item = new MediaItem
                {
                    Id = Guid.NewGuid().ToString("N"),
                    LibraryId = library.Id,
                    RelativePath = relative,
                    Size = size,
                    MtimeUtcTicks = ticks,
                    Status = ItemStatus.Pending
                };
                _repository.InsertItem(item);
                _repository.Enqueue(item.Id, library.Kind);
                Submit(item.Id);
            }
            else if (item.Size != size || Math.Abs(item.MtimeUtcTicks - ticks) > TimeSpan.TicksPerSecond)
            {
                _repository.MarkChanged(item, size, ticks);
                _repository.Enqueue(item.Id, library.Kind);
                Submit(item.Id);
            }
        }

        foreach (var item in existing)
        {
            if (seen.Contains(item.RelativePath))
                continue;
            TryDeleteDirectory(Path.Combine(AppPaths.Thumbs, item.Id));
            _repository.DeleteItem(item.Id);
        }

        _repository.TouchScan(library.Id, DateTime.UtcNow);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Submit(string itemId)
    {
        lock (_queuedGate)
        {
            if (_inFlight.Contains(itemId))
            {
                _resubmit.Add(itemId);
                return;
            }

            if (!_pending.Add(itemId))
                return;
        }

        if (!_queue.Writer.TryWrite(itemId))
        {
            lock (_queuedGate)
                _pending.Remove(itemId);
        }
    }

    private async Task WorkerAsync(CancellationToken cancellationToken)
    {
        await foreach (var itemId in _queue.Reader.ReadAllAsync(cancellationToken))
        {
            lock (_queuedGate)
            {
                _pending.Remove(itemId);
                _inFlight.Add(itemId);
            }

            try
            {
                var acquired = false;
                try
                {
                    await AcquireAsync(cancellationToken);
                    acquired = true;
                    await ProcessAsync(itemId, cancellationToken);
                }
                finally
                {
                    if (acquired)
                        Release();

                    bool resubmit;
                    lock (_queuedGate)
                    {
                        _inFlight.Remove(itemId);
                        resubmit = _resubmit.Remove(itemId);
                    }

                    if (resubmit && !cancellationToken.IsCancellationRequested)
                        Submit(itemId);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
            }
        }
    }

    private async Task ProcessAsync(string itemId, CancellationToken cancellationToken)
    {
        var item = _repository.GetItem(itemId);
        var library = item == null ? null : _repository.GetLibrary(item.LibraryId);
        if (item == null || library == null)
        {
            _repository.MarkWork(itemId, 2);
            return;
        }

        var full = item.FullPath(library.RootPath);
        if (!File.Exists(full))
        {
            _repository.DeleteItem(item.Id);
            _repository.MarkWork(itemId, 2);
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }

        _repository.MarkWork(itemId, 1);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var probe = FfmpegProbe.Read(full);
            cancellationToken.ThrowIfCancellationRequested();
            _repository.SaveProbe(item.Id, probe);
            item = _repository.GetItem(item.Id) ?? item;
            if (library.Kind == LibraryKind.Raw)
                BuildThumbs(item, full, cancellationToken);
            else
                await MatchAsync(item, full, cancellationToken);
            _repository.MarkWork(item.Id, 2);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _repository.SetStatus(item.Id, ItemStatus.Failed, ex.Message);
            _repository.MarkWork(item.Id, 3);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void BuildThumbs(MediaItem item, string full, CancellationToken cancellationToken)
    {
        var frames = FfmpegImages.ExtractNine(full);
        var directory = Path.Combine(AppPaths.Thumbs, item.Id);
        Directory.CreateDirectory(directory);
        var paths = new string?[9];
        for (var i = 0; i < frames.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            paths[i] = ImageFiles.SaveJpeg(frames[i], Path.Combine(directory, $"f{i}.jpg"));
        }

        var sheet = ImageFiles.ComposeSheet(paths, Path.Combine(directory, "sheet.jpg"))
            ?? throw new InvalidOperationException("没有抽出可用的画面。");
        var saved = paths.Where(path => path != null).Cast<string>().ToArray();
        _repository.SaveThumbs(item.Id, saved, sheet);
    }

    private async Task MatchAsync(MediaItem item, string full, CancellationToken cancellationToken)
    {
        var parsed = FilenameParser.Parse(full);
        item.Season = parsed.Season;
        item.Episode = parsed.Episode;
        item.MediaType = parsed.IsTv ? "tv" : "movie";
        var apiKey = _apiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            item.Title = item.FileName;
            item.MatchStatus = MatchStatus.NeedsReview;
            item.Status = ItemStatus.Ready;
            item.Error = "未配置 TMDB API Key";
            _repository.SaveMetadata(item);
            return;
        }

        IReadOnlyList<TmdbHit> hits;
        try
        {
            hits = await _tmdb.SearchAsync(parsed.Title, parsed.Year, parsed.IsTv, apiKey, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            item.Title = item.FileName;
            item.MatchStatus = MatchStatus.NeedsReview;
            item.Status = ItemStatus.Ready;
            item.Error = "TMDB 暂时不可用：" + ex.Message;
            _repository.SaveMetadata(item);
            return;
        }

        var uniqueYear = parsed.Year is int year
            ? hits.Where(hit => hit.Year == year).Take(2).ToList()
            : [];
        if (parsed.Year != null && uniqueYear.Count == 1)
        {
            await ApplyHitAsync(item, uniqueYear[0], cancellationToken);
            return;
        }

        item.Title = item.FileName;
        item.MatchStatus = hits.Count == 0 ? MatchStatus.Failed : MatchStatus.NeedsReview;
        item.Status = ItemStatus.Ready;
        item.Error = hits.Count == 0 ? "没有匹配到 TMDB 条目" : "需要手动确认匹配";
        _repository.SaveMetadata(item);
    }

    public async Task ApplyHitAsync(MediaItem item, TmdbHit hit, CancellationToken cancellationToken)
    {
        item.TmdbId = hit.Id;
        item.MediaType = hit.MediaType;
        item.Title = hit.Title;
        item.OriginalTitle = hit.OriginalTitle;
        item.Year = hit.Year;
        item.Overview = hit.Overview;
        item.Rating = hit.Rating;
        item.PosterPath = await _tmdb.DownloadPosterAsync(hit.PosterPath, hit.Id, cancellationToken);
        item.MatchStatus = MatchStatus.Matched;
        item.Status = ItemStatus.Ready;
        item.Error = null;
        _repository.SaveMetadata(item);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task AcquireAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_slot)
            {
                if (_active < Volatile.Read(ref _maxParallel))
                {
                    _active++;
                    return;
                }
            }

            await _slotSignal.WaitAsync(cancellationToken);
        }
    }

    private void Release()
    {
        lock (_slot)
            _active = Math.Max(0, _active - 1);
        if (_slotSignal.CurrentCount == 0)
            _slotSignal.Release();
    }

    private Task Track(Task task)
    {
        lock (_tasksGate)
            _tasks.Add(task);
        _ = task.ContinueWith(
            completed =>
            {
                lock (_tasksGate)
                    _tasks.Remove(completed);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return task;
    }

    private object LibraryLock(string libraryId) => _libraryLocks.GetOrAdd(libraryId, _ => new object());

    private static long SecondTicks(DateTime utc) => utc.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond;

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
