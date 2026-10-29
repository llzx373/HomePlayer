using HomePlayer.Core;

namespace HomePlayer.Library;

public sealed partial class LibraryCoordinator
{
    public Task MigrateRootAsync(string libraryId, string newRoot) => Task.Run(() =>
    {
        var full = Path.GetFullPath(newRoot);
        if (!string.Equals(full, Path.GetPathRoot(full), StringComparison.OrdinalIgnoreCase))
            full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Directory.Exists(full))
            throw new InvalidOperationException("新文件夹不存在。请先移动媒体文件，再选择新位置。");
        lock (LibraryLock(libraryId))
        {
            var library = _repository.GetLibrary(libraryId) ?? throw new InvalidOperationException("媒体库不存在。");
            if (string.Equals(library.RootPath, full, StringComparison.OrdinalIgnoreCase))
                return;
            if (_repository.ListLibraries().Any(item => item.Id != libraryId && string.Equals(item.RootPath, full, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("新位置已经属于另一个媒体库。");

            FileSystemWatcher? watcher;
            lock (_watchersGate)
                _watchers.Remove(libraryId, out watcher);
            watcher?.Dispose();
            lock (_debounce)
            {
                if (_debounce.Remove(libraryId, out var debounce))
                {
                    debounce.Cancel();
                    debounce.Dispose();
                }
                _changes.Remove(libraryId);
            }

            _repository.UpdateLibraryRoot(libraryId, full);
            var migrated = _repository.GetLibrary(libraryId)!;
            MarkAllDirty();
            Watch(migrated);
            ReconcileCore(libraryId);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    });
}
