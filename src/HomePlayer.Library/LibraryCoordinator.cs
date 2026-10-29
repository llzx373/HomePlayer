using System.Collections.Concurrent;
using System.Threading.Channels;
using HomePlayer.Core;
using HomePlayer.Data;
using HomePlayer.Ffmpeg;

namespace HomePlayer.Library;

public sealed partial class LibraryCoordinator : IAsyncDisposable
{
    private readonly LibraryRepository _repository;
    private readonly TmdbClient _tmdb;
    private readonly Func<string?> _apiKey;
    private readonly Func<string, VideoScanResult> _scan;
    private readonly ConcurrentDictionary<string, string> _scanWarnings = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, LibraryScanStatus> _scanStatuses = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PendingLibraryChanges> _changes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>();
    private readonly Channel<(string LibraryId, string RelativePath)> _coverQueue = Channel.CreateUnbounded<(string, string)>();
    private readonly HashSet<string> _coverQueued = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _coverQueuedGate = new();
    private readonly SemaphoreSlim _coverGate = new(1, 1);
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
    private readonly object _dirtyGate = new();
    private readonly HashSet<string> _dirtyItems = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _slotSignal = new(0);
    private bool _dirtyAll;
    private readonly object _slot = new();
    private readonly object _watchersGate = new();
    private int _active;
    private int _maxParallel = 2;
    private int _started;

    public LibraryCoordinator(
        LibraryRepository repository,
        Func<string?> apiKey,
        Func<string, VideoScanResult>? scan = null)
    {
        _repository = repository;
        _tmdb = new TmdbClient(repository);
        _apiKey = apiKey;
        _scan = scan ?? VideoFiles.Scan;
    }

    public event EventHandler? Changed;
    public event EventHandler? ScanStatusChanged;

    /// <summary>
    /// 条目的文件内容变了或被删掉，播放侧要丢掉对应的旧进度。
    /// </summary>
    public event Action<string>? ItemReset;

    public int PendingCount => _repository.PendingCount();

    public string? ScanWarning(string libraryId) => _scanWarnings.GetValueOrDefault(libraryId);

    public LibraryScanStatus ScanStatus(string libraryId) =>
        _scanStatuses.GetValueOrDefault(libraryId) ?? LibraryScanStatus.Idle;

    public (bool All, string[] Ids) TakeDirtyItems()
    {
        lock (_dirtyGate)
        {
            var all = _dirtyAll;
            _dirtyAll = false;
            var ids = _dirtyItems.ToArray();
            _dirtyItems.Clear();
            return (all, ids);
        }
    }

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            return;
        for (var i = 0; i < 2; i++)
            _workers.Add(Task.Run(() => WorkerAsync(_shutdown.Token)));
        _workers.Add(Task.Run(() => CoverWorkerAsync(_shutdown.Token)));
        _workers.Add(Task.Run(() => AvailabilityWorkerAsync(_shutdown.Token)));
        IReadOnlyList<MediaLibrary> libraries;
        try
        {
            libraries = _repository.ListLibraries();
        }
        catch (Exception)
        {
            return;
        }

        foreach (var library in libraries)
        {
            try
            {
                Watch(library);
            }
            catch (Exception)
            {
            }
        }

        try
        {
            foreach (var id in _repository.ListQueued())
                Submit(id);
        }
        catch (Exception)
        {
        }

        foreach (var library in libraries)
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
        MarkAllDirty();
        ReconcileInBackground(library.Id);
        Changed?.Invoke(this, EventArgs.Empty);
        return library;
    }

    public async Task RemoveAsync(string libraryId)
    {
        _scanWarnings.TryRemove(libraryId, out _);
        _scanStatuses.TryRemove(libraryId, out _);
        FileSystemWatcher? watcher;
        lock (_watchersGate)
            _watchers.Remove(libraryId, out watcher);
        watcher?.Dispose();
        CancellationTokenSource? debounce = null;
        lock (_debounce)
        {
            _debounce.Remove(libraryId, out debounce);
            _changes.Remove(libraryId);
        }
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
                    foreach (var folder in _repository.ListFolders(libraryId))
                        TryDeleteDirectory(FolderCovers.CacheDirectory(libraryId, folder.RelativePath));
                }

                _repository.DeleteLibrary(libraryId);
                _libraryLocks.TryRemove(libraryId, out _);
            }
        });

        MarkAllDirty();
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

        _repository.ReprocessItem(item, size, ticks);
        _repository.Enqueue(item.Id, library.Kind);
        MarkDirty(item.Id);
        Submit(item.Id);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public long CacheBytes()
    {
        using var measure = PerformanceMetrics.Measure("library.cache-bytes");
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
                    _repository.ReprocessItem(item, item.Size, item.MtimeUtcTicks);
                    _repository.Enqueue(item.Id, library.Kind);
                    Submit(item.Id);
                }

                _repository.ClearFolderCovers(library.Id);
                foreach (var folder in _repository.ListFolders(library.Id))
                    EnqueueCover(library.Id, folder.RelativePath);
            }
        });

        MarkAllDirty();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        _queue.Writer.TryComplete();
        _coverQueue.Writer.TryComplete();
        lock (_watchersGate)
        {
            foreach (var watcher in _watchers.Values)
                watcher.Dispose();
            _watchers.Clear();
        }
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
        _coverGate.Dispose();
        _shutdown.Dispose();
    }

    private void Watch(MediaLibrary library)
    {
        if (_shutdown.IsCancellationRequested || !Directory.Exists(library.RootPath))
            return;
        lock (_watchersGate)
        {
            if (_shutdown.IsCancellationRequested || _watchers.ContainsKey(library.Id))
                return;
        }

        var watcher = new FileSystemWatcher(library.RootPath)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            InternalBufferSize = 64 * 1024
        };
        watcher.Created += (_, e) => QueueChange(library.Id, e.FullPath, null, e.ChangeType);
        watcher.Changed += (_, e) => QueueChange(library.Id, e.FullPath, null, e.ChangeType);
        watcher.Deleted += (_, e) => QueueChange(library.Id, e.FullPath, null, e.ChangeType);
        watcher.Renamed += (_, e) => QueueChange(library.Id, e.FullPath, e.OldFullPath, e.ChangeType);
        watcher.Error += (_, _) => QueueChange(library.Id, null, null, null, fullScan: true);
        lock (_watchersGate)
        {
            if (_shutdown.IsCancellationRequested || _watchers.ContainsKey(library.Id))
            {
                watcher.Dispose();
                return;
            }

            try
            {
                watcher.EnableRaisingEvents = true;
                _watchers[library.Id] = watcher;
            }
            catch (Exception)
            {
                watcher.Dispose();
            }
        }
    }

    private void QueueChange(string libraryId, string? path, string? oldPath, WatcherChangeTypes? type, bool fullScan = false)
    {
        lock (_debounce)
        {
            if (!_changes.TryGetValue(libraryId, out var pending))
            {
                pending = new PendingLibraryChanges();
                _changes[libraryId] = pending;
            }

            pending.FullScan |= fullScan;
            if (path != null && type != null)
                pending.Changes.Add(new LibraryChange(path, oldPath, type.Value));
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
                    ReconcileQueued(libraryId);
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

    private void ReconcileQueued(string libraryId)
    {
        PendingLibraryChanges? pending;
        lock (_debounce)
        {
            _changes.Remove(libraryId, out pending);
        }
        if (pending == null)
            return;
        if (pending.FullScan)
        {
            Reconcile(libraryId);
            return;
        }

        lock (LibraryLock(libraryId))
        {
            if (_shutdown.IsCancellationRequested)
                return;
            try
            {
                ReconcileChanges(libraryId, pending.Changes);
            }
            catch (Exception)
            {
                ReconcileCore(libraryId);
            }
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
        if (library == null)
            return;
        if (!Directory.Exists(library.RootPath))
        {
            SetUnavailable(library);
            return;
        }

        SetScanStatus(library.Id, LibraryScanStage.Scanning, 0, [], "正在枚举文件…");
        var existing = _repository.ListItems(libraryId);
        var byPath = existing.ToDictionary(item => item.RelativePath, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var scan = _scan(library.RootPath);
        foreach (var file in scan.Files)
        {
            if (_shutdown.IsCancellationRequested)
                return;
            var relative = Path.GetRelativePath(library.RootPath, file);
            seen.Add(relative);
            UpsertFile(library, file, byPath.GetValueOrDefault(relative));
        }

        if (!scan.IsComplete)
        {
            RecordScanWarning(library, scan.Failures, scan.Files.Count);
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }

        _scanWarnings.TryRemove(library.Id, out _);
        foreach (var item in existing)
        {
            if (!seen.Contains(item.RelativePath))
                RemoveItem(library, item);
        }

        RebuildFolders(library);
        _repository.TouchScan(library.Id, DateTime.UtcNow);
        SetCompleteStatus(library.Id, scan.Files.Count);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void ReconcileChanges(string libraryId, IReadOnlyCollection<LibraryChange> changes)
    {
        var library = _repository.GetLibrary(libraryId);
        if (library == null)
            return;
        if (!Directory.Exists(library.RootPath))
        {
            SetUnavailable(library);
            return;
        }

        SetScanStatus(library.Id, LibraryScanStage.Scanning, changes.Count, [], "正在处理文件变化…");
        var failures = new List<DirectoryScanFailure>();
        var discovered = 0;
        foreach (var change in changes)
        {
            if (change.OldPath != null)
                RemovePath(library, change.OldPath);
            if (change.Type == WatcherChangeTypes.Deleted)
            {
                RemovePath(library, change.Path);
                continue;
            }

            if (Directory.Exists(change.Path))
            {
                if (change.Type != WatcherChangeTypes.Changed)
                {
                    var scan = _scan(change.Path);
                    discovered += scan.Files.Count;
                    failures.AddRange(scan.Failures);
                    foreach (var file in scan.Files)
                        UpsertFile(library, file);
                }
            }
            else if (File.Exists(change.Path) && VideoFiles.IsVideo(change.Path))
            {
                discovered++;
                UpsertFile(library, change.Path);
            }
            else if (!File.Exists(change.Path))
            {
                RemovePath(library, change.Path);
            }
        }

        RebuildFolders(library);
        if (failures.Count > 0)
            RecordScanWarning(library, failures, discovered);
        else
        {
            _scanWarnings.TryRemove(library.Id, out _);
            SetCompleteStatus(library.Id, discovered);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void UpsertFile(MediaLibrary library, string file, MediaItem? knownItem = null)
    {
        var relative = Path.GetRelativePath(library.RootPath, file);
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
            return;
        }

        var item = knownItem ?? _repository.FindItemByPath(file);
        if (item == null)
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
            EnqueueAncestorCovers(library.Id, relative);
            MarkDirty(item.Id);
            Submit(item.Id);
        }
        else if (item.Size != size || Math.Abs(item.MtimeUtcTicks - ticks) > TimeSpan.TicksPerSecond)
        {
            _repository.MarkChanged(item, size, ticks);
            _repository.Enqueue(item.Id, library.Kind);
            EnqueueAncestorCovers(library.Id, item.RelativePath);
            ResetProgress(library, item.RelativePath);
            MarkDirty(item.Id);
            Submit(item.Id);
        }
    }

    private void RemovePath(MediaLibrary library, string path)
    {
        var relative = Path.GetRelativePath(library.RootPath, path);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return;
        foreach (var item in _repository.ListItemsInDirectory(library.Id, relative))
            RemoveItem(library, item);
    }

    private void RemoveItem(MediaLibrary library, MediaItem item)
    {
        TryDeleteDirectory(Path.Combine(AppPaths.Thumbs, item.Id));
        _repository.DeleteItem(item.Id);
        ResetProgress(library, item.RelativePath);
        EnqueueAncestorCovers(library.Id, item.RelativePath);
        MarkDirty(item.Id);
    }

    private void RebuildFolders(MediaLibrary library)
    {
        var folders = FolderTree.Build(library, _repository.ListItems(library.Id).Select(item => item.RelativePath));
        var removedFolders = _repository.SyncFolders(library.Id, folders);
        foreach (var path in removedFolders)
            TryDeleteDirectory(FolderCovers.CacheDirectory(library.Id, path));
        foreach (var folder in _repository.ListFolders(library.Id))
        {
            if (string.IsNullOrEmpty(folder.CoverPath))
                EnqueueCover(library.Id, folder.RelativePath);
        }
    }

    private void RecordScanWarning(MediaLibrary library, IReadOnlyList<DirectoryScanFailure> failures, int discovered)
    {
        var message = $"本次扫描不完整：{failures.Count} 个文件夹无法读取，已保留未确认的条目。";
        _scanWarnings[library.Id] = message;
        SetScanStatus(library.Id, LibraryScanStage.Incomplete, discovered, failures, message);
        try
        {
            CrashLog.Write(
                "扫描不完整",
                library.Name + Environment.NewLine + string.Join(Environment.NewLine, failures.Select(failure => $"{failure.Path}: {failure.Error}")));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void SetUnavailable(MediaLibrary library)
    {
        var message = "媒体库文件夹当前不可用，已保留现有媒体和观看进度；恢复连接后会自动重试。";
        _scanWarnings[library.Id] = message;
        SetScanStatus(library.Id, LibraryScanStage.Unavailable, 0, [], message);
    }

    private void SetCompleteStatus(string libraryId, int discovered)
    {
        var pending = _repository.PendingCount();
        SetScanStatus(
            libraryId,
            pending > 0 ? LibraryScanStage.Processing : LibraryScanStage.Complete,
            discovered,
            [],
            pending > 0 ? $"扫描完成，正在处理 {pending:N0} 项元数据和封面。" : "扫描完成。");
    }

    private void SetScanStatus(
        string libraryId,
        LibraryScanStage stage,
        int discovered,
        IReadOnlyList<DirectoryScanFailure> failures,
        string message)
    {
        _scanStatuses[libraryId] = new LibraryScanStatus(stage, discovered, _repository.PendingCount(), failures, message);
        ScanStatusChanged?.Invoke(this, EventArgs.Empty);
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

            var libraryId = _repository.GetItem(itemId)?.LibraryId;
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
                if (libraryId != null && ScanStatus(libraryId).Stage == LibraryScanStage.Processing)
                    SetCompleteStatus(libraryId, ScanStatus(libraryId).DiscoveredCount);
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
            ResetProgress(library, item.RelativePath);
            MarkDirty(item.Id);
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
            var parsed = FilenameParser.Parse(full);
            item.Season ??= parsed.Season;
            item.Episode ??= parsed.Episode;
            if (parsed.IsTv)
                item.MediaType = "tv";
            var hasNfo = NfoMetadata.Apply(item, full);
            _repository.SaveMetadata(item);
            if (library.Kind == LibraryKind.Raw)
                BuildThumbs(item, full, cancellationToken);
            else if (!hasNfo)
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

        EnqueueAncestorCovers(library.Id, item.RelativePath);
        MarkDirty(item.Id);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void EnqueueAncestorCovers(string libraryId, string relativePath)
    {
        foreach (var dir in FolderTree.Ancestors(RelativePaths.Split(relativePath).Directory))
            EnqueueCover(libraryId, dir);
    }

    private void EnqueueCover(string libraryId, string relativePath)
    {
        var path = (relativePath ?? "").Replace('\\', '/').Trim('/');
        var key = libraryId + "|" + path;
        lock (_coverQueuedGate)
        {
            if (!_coverQueued.Add(key))
                return;
        }

        if (!_coverQueue.Writer.TryWrite((libraryId, path)))
        {
            lock (_coverQueuedGate)
                _coverQueued.Remove(key);
        }
    }

    private async Task CoverWorkerAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var (libraryId, relativePath) in _coverQueue.Reader.ReadAllAsync(cancellationToken))
            {
                var key = libraryId + "|" + relativePath;
                try
                {
                    while (!cancellationToken.IsCancellationRequested && Busy())
                        await Task.Delay(500, cancellationToken);
                    if (cancellationToken.IsCancellationRequested)
                        return;

                    await _coverGate.WaitAsync(cancellationToken);
                    bool changed;
                    try
                    {
                        changed = await Task.Run(() => RefreshFolderCover(libraryId, relativePath), cancellationToken);
                    }
                    finally
                    {
                        _coverGate.Release();
                    }

                    if (changed)
                        Changed?.Invoke(this, EventArgs.Empty);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception)
                {
                }
                finally
                {
                    lock (_coverQueuedGate)
                        _coverQueued.Remove(key);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private bool Busy() =>
        Volatile.Read(ref _active) > 0 || Volatile.Read(ref _maxParallel) == 1;

    private bool RefreshFolderCover(string libraryId, string relativePath)
    {
        var library = _repository.GetLibrary(libraryId);
        if (library == null)
            return false;
        var folder = _repository.GetFolder(libraryId, relativePath);
        if (folder == null)
            return false;

        var sources = CoverSources(library, relativePath);
        var stamp = FolderCovers.Stamp(sources);
        var path = FolderCovers.CoverPath(libraryId, relativePath);
        var upToDate = string.Equals(folder.CoverStamp, stamp, StringComparison.Ordinal)
            && (stamp.Length == 0 ? !File.Exists(path) : File.Exists(path));
        if (upToDate)
            return false;

        if (sources.Count == 0)
        {
            if (folder.CoverPath == null && folder.CoverStamp == null)
                return false;
            TryDeleteDirectory(FolderCovers.CacheDirectory(libraryId, relativePath));
            _repository.SaveFolderCover(libraryId, relativePath, null, null);
            return true;
        }

        var images = sources
            .Select(item => FolderCovers.ImagePath(item, library.Kind))
            .ToList();
        var cover = ImageFiles.ComposeGrid(images, path, FolderCovers.CoverWidth, FolderCovers.CoverHeight);
        if (cover == null)
            return false;
        _repository.SaveFolderCover(libraryId, relativePath, cover, stamp);
        return true;
    }

    private IReadOnlyList<MediaItem> CoverSources(MediaLibrary library, string relativePath)
    {
        var selected = new List<MediaItem>(FolderCovers.SourceLimit);
        var direct = _repository.ListItemsPage(
            library.Id, relativePath, false, null, LibraryFilter.All, SortMode.FileName, 0, FolderCovers.SourceLimit * 2);
        foreach (var item in direct)
        {
            if (selected.Count >= FolderCovers.SourceLimit)
                break;
            if (FolderCovers.ImagePath(item, library.Kind) == null)
                continue;
            selected.Add(item);
        }

        if (library.Kind == LibraryKind.Catalog)
        {
            if (selected.Count == 0)
            {
                var fallback = _repository.ListItemsPage(
                        library.Id, relativePath, true, null, LibraryFilter.All, SortMode.FileName, 0, FolderCovers.SourceLimit * 4)
                    .FirstOrDefault(item => FolderCovers.ImagePath(item, library.Kind) != null);
                if (fallback != null)
                    selected.Add(fallback);
            }

            return selected.Count == 0 ? [] : [selected[0]];
        }

        if (selected.Count >= FolderCovers.SourceLimit)
            return selected;

        var recursive = _repository.ListItemsPage(
            library.Id, relativePath, true, null, LibraryFilter.All, SortMode.FileName, 0, FolderCovers.SourceLimit * 4);
        foreach (var item in recursive)
        {
            if (selected.Count >= FolderCovers.SourceLimit)
                break;
            if (selected.Any(existing => string.Equals(existing.Id, item.Id, StringComparison.Ordinal)))
                continue;
            if (FolderCovers.ImagePath(item, library.Kind) == null)
                continue;
            selected.Add(item);
        }

        return selected;
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
        if (hit.MediaType == "movie")
        {
            try
            {
                item.CollectionName = await _tmdb.GetMovieCollectionAsync(hit.Id, _apiKey() ?? "", cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
            }
        }
        item.MatchStatus = MatchStatus.Matched;
        item.Status = ItemStatus.Ready;
        item.Error = null;
        _repository.SaveMetadata(item);
        MarkDirty(item.Id);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task AvailabilityWorkerAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                foreach (var (libraryId, status) in _scanStatuses)
                {
                    if (status.Stage != LibraryScanStage.Unavailable)
                        continue;
                    var library = _repository.GetLibrary(libraryId);
                    if (library == null || !Directory.Exists(library.RootPath))
                        continue;
                    Watch(library);
                    ReconcileInBackground(libraryId);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
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

    private void MarkDirty(string itemId)
    {
        lock (_dirtyGate)
            _dirtyItems.Add(itemId);
    }

    private void ResetProgress(MediaLibrary library, string relativePath)
    {
        var full = Path.Combine(library.RootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
        ItemReset?.Invoke(full);
    }

    private void MarkAllDirty()
    {
        lock (_dirtyGate)
            _dirtyAll = true;
    }

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

    private sealed record LibraryChange(string Path, string? OldPath, WatcherChangeTypes Type);

    private sealed class PendingLibraryChanges
    {
        public bool FullScan { get; set; }
        public HashSet<LibraryChange> Changes { get; } = [];
    }
}
