using System.Collections.ObjectModel;
using System.Globalization;
using HomePlayer.Core;
using HomePlayer.Library;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace HomePlayer;

public sealed partial class LibraryPage : Page
{
    private readonly ObservableCollection<MediaLibrary> _libraries = [];
    private readonly BulkObservableCollection<MediaCard> _cards = [];
    private readonly Dictionary<string, (DateTime Stamp, BitmapImage? Image)> _imageCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _imageOrder = [];
    private const int ImageCacheLimit = 200;
    private readonly DispatcherQueueTimer _reloadTimer;
    private readonly DispatcherQueueTimer _libraryTimer;
    private readonly DispatcherQueueTimer _filterTimer;
    private readonly Dictionary<string, FolderNode> _folderNodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TreeViewNode> _folderTreeNodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (DateTime Stamp, BitmapImage Image)> _coverCache = new(StringComparer.OrdinalIgnoreCase);
    private bool _ready;
    private bool _loading;
    private bool _sorting;
    private bool _syncingLibrary;
    private bool _viewRestored;
    private bool _restoringView;
    private bool _pendingAll;
    private int _pendingUpdates;
    private int _reloadToken;
    private int _loadToken;
    private int _page = 1;
    private int _pageSize = PagedView.DefaultPageSize;
    private int _total;
    private string _folderPath = "";

    public LibraryPage()
    {
        InitializeComponent();
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
        LibraryList.DisplayMemberPath = nameof(MediaLibrary.Name);
        LibraryList.ItemsSource = _libraries;
        Cards.ItemsSource = _cards;
        _reloadTimer = DispatcherQueue.CreateTimer();
        _reloadTimer.Interval = TimeSpan.FromMilliseconds(400);
        _reloadTimer.IsRepeating = false;
        _reloadTimer.Tick += (_, _) =>
        {
            ShowPendingUpdates();
            RefreshFolderTree();
        };
        _libraryTimer = DispatcherQueue.CreateTimer();
        _libraryTimer.Interval = TimeSpan.FromMilliseconds(400);
        _libraryTimer.IsRepeating = false;
        _libraryTimer.Tick += (_, _) => ReloadLibraries();
        _filterTimer = DispatcherQueue.CreateTimer();
        _filterTimer.Interval = TimeSpan.FromMilliseconds(200);
        _filterTimer.IsRepeating = false;
        _filterTimer.Tick += (_, _) => LoadPage();
        Pager.PageRequested += (_, page) =>
        {
            _page = page;
            LoadPage();
        };
        Pager.PageSizeChanged += (_, size) =>
        {
            _pageSize = size;
            _page = 1;
            LoadPage();
        };
        App.Host.Libraries.Changed += (_, _) => DispatcherQueue.TryEnqueue(QueuePageReload);
        Loaded += (_, _) =>
        {
            _ready = true;
            QueueLibraryReload();
        };
        Unloaded += (_, _) =>
        {
            _ready = false;
            SaveViewState();
            _reloadTimer.Stop();
            _libraryTimer.Stop();
            _filterTimer.Stop();
        };
        _ready = true;
    }

    private void QueuePageReload()
    {
        if (!IsLoaded || App.Host.Exiting)
            return;
        UpdateScanWarning();
        if (!_reloadTimer.IsRunning)
            _reloadTimer.Start();
    }

    private void UpdateScanWarning()
    {
        var libraryId = (LibraryList.SelectedItem as MediaLibrary)?.Id;
        var warning = libraryId == null ? null : App.Host.Libraries.ScanWarning(libraryId);
        ScanWarningBar.Message = warning ?? "";
        ScanWarningBar.IsOpen = warning != null;
    }

    private void ShowPendingUpdates()
    {
        var (all, ids) = App.Host.Libraries.TakeDirtyItems();
        if (!all && ids.Length == 0)
            return;
        if (all)
            _pendingAll = true;
        _pendingUpdates += ids.Length;
        UpdateBar.Message = _pendingAll
            ? "媒体库有变动，点击刷新查看。"
            : $"有 {_pendingUpdates} 项更新，点击刷新查看。";
        UpdateBar.IsOpen = true;
    }

    private void RefreshPending_Click(object sender, RoutedEventArgs e)
    {
        _pendingUpdates = 0;
        _pendingAll = false;
        UpdateBar.IsOpen = false;
        LoadPage();
        RefreshFolderTree();
    }

    private void QueueLibraryReload()
    {
        if (!IsLoaded || App.Host.Exiting)
            return;
        if (!_libraryTimer.IsRunning)
            _libraryTimer.Start();
    }

    private void ReloadLibraries()
    {
        if (App.Host.Exiting)
            return;
        var selected = (LibraryList.SelectedItem as MediaLibrary)?.Id;
        var token = ++_reloadToken;
        _loading = true;
        _ = Task.Run(() =>
        {
            IReadOnlyList<MediaLibrary> libraries;
            try
            {
                libraries = App.Host.Repository.ListLibraries();
            }
            catch (Exception)
            {
                libraries = [];
            }

            DispatcherQueue.TryEnqueue(() => ApplyLibraries(selected, token, libraries));
        });
    }

    private void ApplyLibraries(string? selected, int token, IReadOnlyList<MediaLibrary> libraries)
    {
        if (token != _reloadToken || App.Host.Exiting)
            return;
        _libraries.Clear();
        foreach (var library in libraries)
            _libraries.Add(library);
        var view = _viewRestored ? null : App.Host.Settings.LibraryView;
        var targetId = selected ?? view?.LibraryId;
        var selection = _libraries.FirstOrDefault(item => item.Id == targetId) ?? _libraries.FirstOrDefault();
        _syncingLibrary = true;
        try
        {
            LibraryList.SelectedItem = selection;
        }
        finally
        {
            _syncingLibrary = false;
        }

        _loading = false;
        if (view != null && selection != null)
        {
            _viewRestored = true;
            if (string.Equals(view.LibraryId, selection.Id, StringComparison.Ordinal))
            {
                _ = RestoreViewAsync(selection, view);
                return;
            }
        }

        _folderPath = "";
        RebuildFolderTree(selection);
        _page = 1;
        LoadPage();
    }

    private void LoadPage()
    {
        if (_loading || App.Host.Exiting)
            return;
        var library = LibraryList.SelectedItem as MediaLibrary;
        ConfigureSort(library?.Kind);
        UpdateScanWarning();
        var token = ++_loadToken;
        if (library == null)
        {
            _cards.Clear();
            _total = 0;
            Pager.Update(0, 1, _pageSize);
            UpdateEmptyText();
            return;
        }

        var query = SearchBox.Text?.Trim() ?? "";
        var sort = CurrentSort(library.Kind);
        var requestedPage = _page;
        var pageSize = _pageSize;
        var libraryId = library.Id;
        var kind = library.Kind;
        var folderPath = _folderPath;
        var recursive = RecursiveSwitch.IsOn;
        var filter = CurrentFilter(library.Kind);
        _ = Task.Run(() =>
        {
            IReadOnlyList<MediaItem> items;
            int total;
            var page = requestedPage;
            try
            {
                total = App.Host.Repository.CountItems(libraryId, folderPath, recursive, query, filter);
                page = PagedView.ClampPage(page, total, pageSize);
                items = App.Host.Repository.ListItemsPage(
                    libraryId, folderPath, recursive, query, filter, sort, (page - 1) * pageSize, pageSize);
                if (items.Count == 0 && total > 0 && page > 1)
                {
                    page--;
                    items = App.Host.Repository.ListItemsPage(
                        libraryId, folderPath, recursive, query, filter, sort, (page - 1) * pageSize, pageSize);
                }
            }
            catch (Exception)
            {
                items = [];
                total = 0;
            }

            var stamps = ComputeImageStamps(items, kind);
            IReadOnlyDictionary<string, PlaybackState> states;
            try
            {
                states = App.Host.Repository.LoadPlaybackStates(
                    items.Select(item => item.FullPath(library.RootPath)).ToList());
            }
            catch (Exception)
            {
                states = new Dictionary<string, PlaybackState>(StringComparer.OrdinalIgnoreCase);
            }

            DispatcherQueue.TryEnqueue(() => ApplyPage(library, page, pageSize, total, items, stamps, states, token));
        });
    }

    private void ApplyPage(
        MediaLibrary library,
        int page,
        int pageSize,
        int total,
        IReadOnlyList<MediaItem> items,
        IReadOnlyDictionary<string, DateTime> stamps,
        IReadOnlyDictionary<string, PlaybackState> states,
        int token)
    {
        if (token != _loadToken || App.Host.Exiting)
            return;
        if ((LibraryList.SelectedItem as MediaLibrary)?.Id != library.Id)
            return;
        _page = page;
        _pageSize = pageSize;
        _total = total;
        var cards = new List<MediaCard>(items.Count);
        foreach (var item in items)
        {
            var card = CreateCard(item, library, stamps, states);
            if (card != null)
                cards.Add(card);
        }

        _cards.ReplaceAll(cards);
        Pager.Update(total, page, pageSize);
        _pendingUpdates = 0;
        _pendingAll = false;
        UpdateBar.IsOpen = false;
        UpdateEmptyText();
        SaveViewState();
    }

    private static IReadOnlyDictionary<string, DateTime> ComputeImageStamps(
        IReadOnlyList<MediaItem> items,
        LibraryKind kind)
    {
        var stamps = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            var path = kind == LibraryKind.Raw ? item.SheetPath : item.PosterPath;
            if (string.IsNullOrWhiteSpace(path) || stamps.ContainsKey(path))
                continue;
            try
            {
                var info = new FileInfo(path);
                stamps[path] = info.Exists ? info.LastWriteTimeUtc : default;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                stamps[path] = default;
            }
        }

        return stamps;
    }

    private MediaCard? CreateCard(
        MediaItem item,
        MediaLibrary library,
        IReadOnlyDictionary<string, DateTime> stamps,
        IReadOnlyDictionary<string, PlaybackState> states)
    {
        var title = library.Kind == LibraryKind.Catalog && !string.IsNullOrWhiteSpace(item.Title) && item.MatchStatus == MatchStatus.Matched
            ? item.Title!
            : item.FileName;
        var subtitle = library.Kind == LibraryKind.Raw
            ? $"{ClockText.FormatSeconds(item.DurationSeconds)} · {Resolution(item)} · {item.VideoCodec ?? item.Status.ToString()}"
            : CatalogSubtitle(item);
        var imagePath = library.Kind == LibraryKind.Raw ? item.SheetPath : item.PosterPath;
        var stamp = !string.IsNullOrWhiteSpace(imagePath) && stamps.TryGetValue(imagePath, out var value) ? value : default;
        states.TryGetValue(item.FullPath(library.RootPath), out var progress);
        var ratio = progress is { Finished: false, DurationSeconds: > 0 } && progress.PositionSeconds > 0
            ? Math.Clamp(progress.PositionSeconds / progress.DurationSeconds, 0, 1)
            : 0;
        return new MediaCard
        {
            Item = item,
            Library = library,
            Title = title,
            Subtitle = item.Error ?? subtitle,
            Image = LoadImage(imagePath, stamp),
            Badge = item.Status == ItemStatus.Pending
                ? "处理中"
                : library.Kind == LibraryKind.Catalog && item.MatchStatus != MatchStatus.Matched ? "待核对" : "",
            Progress = ratio,
            ProgressVisibility = ratio > 0 ? Visibility.Visible : Visibility.Collapsed,
            WatchedVisibility = progress?.Finished == true ? Visibility.Visible : Visibility.Collapsed
        };
    }

    private void UpdateEmptyText()
    {
        if (_libraries.Count == 0)
        {
            EmptyText.Visibility = Visibility.Visible;
            EmptyText.Text = "还没有媒体库。添加一个本地文件夹开始。";
            return;
        }

        if (_total > 0)
        {
            EmptyText.Visibility = Visibility.Collapsed;
            return;
        }

        EmptyText.Visibility = Visibility.Visible;
        if (!string.IsNullOrWhiteSpace(SearchBox.Text))
        {
            EmptyText.Text = "没有匹配的条目。";
            return;
        }

        if (LibraryList.SelectedItem is not MediaLibrary library)
        {
            EmptyText.Text = "选择一个媒体库。";
            return;
        }

        if (CurrentFilter(library.Kind) != LibraryFilter.All)
        {
            EmptyText.Text = "没有符合筛选条件的条目。";
            return;
        }

        if (_folderPath.Length > 0)
        {
            EmptyText.Text = App.Host.Libraries.PendingCount > 0 ? "正在扫描和生成预览…" : "这个文件夹里没有视频。";
            return;
        }

        EmptyText.Text = App.Host.Libraries.PendingCount > 0 ? "正在扫描和生成预览…" : "这个库里还没有视频。";
    }

    private SortMode CurrentSort(LibraryKind kind) => kind == LibraryKind.Catalog
        ? SortBox.SelectedIndex == 1 ? SortMode.YearDesc : SortMode.Title
        : SortBox.SelectedIndex == 1 ? SortMode.ModifiedDesc : SortMode.FileName;

    private LibraryFilter CurrentFilter(LibraryKind kind)
    {
        var index = FilterBox.SelectedIndex;
        if (index <= 0)
            return LibraryFilter.All;
        if (kind == LibraryKind.Catalog)
            return index switch
            {
                1 => LibraryFilter.Failed,
                2 => LibraryFilter.NeedsReview,
                _ => LibraryFilter.Unwatched
            };
        return index == 1 ? LibraryFilter.Failed : LibraryFilter.Unwatched;
    }

    private void ConfigureSort(LibraryKind? kind)
    {
        if (kind == null)
            return;
        var catalog = kind == LibraryKind.Catalog;
        if (SortBox.Items.Count > 0 && Equals(SortBox.Tag, catalog))
            return;
        _sorting = true;
        try
        {
            SortBox.Tag = catalog;
            SortBox.Items.Clear();
            if (catalog)
            {
                SortBox.Items.Add("标题");
                SortBox.Items.Add("年份");
            }
            else
            {
                SortBox.Items.Add("文件名");
                SortBox.Items.Add("修改时间");
            }

            SortBox.SelectedIndex = 0;

            FilterBox.Items.Clear();
            FilterBox.Items.Add("全部");
            FilterBox.Items.Add("失败");
            if (catalog)
                FilterBox.Items.Add("待核对");
            FilterBox.Items.Add("未看");
            FilterBox.SelectedIndex = 0;
        }
        finally
        {
            _sorting = false;
        }
    }

    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        var name = new TextBox { PlaceholderText = "显示名称" };
        var path = new TextBox { PlaceholderText = "文件夹路径，也可以点下面的按钮选择" };
        var kind = new RadioButtons();
        kind.Items.Add("无媒体信息");
        kind.Items.Add("有媒体信息（TMDB）");
        kind.SelectedIndex = 0;
        var panel = new StackPanel { Spacing = 12, Width = 420 };
        panel.Children.Add(name);
        panel.Children.Add(path);
        panel.Children.Add(kind);
        ContentDialog? dialog = null;
        var browse = new Button { Content = "选择文件夹" };
        var choosingFolder = false;
        browse.Click += (_, _) =>
        {
            choosingFolder = true;
            dialog?.Hide();
        };
        panel.Children.Insert(2, browse);
        while (true)
        {
            dialog?.Content = null;
            choosingFolder = false;
            dialog = new ContentDialog
            {
                Title = "添加媒体库",
                Content = panel,
                PrimaryButtonText = "添加",
                CloseButtonText = "取消",
                XamlRoot = XamlRoot
            };
            var result = await dialog.ShowAsync();
            if (choosingFolder)
            {
                string? folder = null;
                try
                {
                    folder = await FolderDialog.PickAsync();
                }
                catch (Exception ex)
                {
                    await Message(ex.Message);
                }

                if (!string.IsNullOrWhiteSpace(folder))
                {
                    path.Text = folder;
                    if (string.IsNullOrWhiteSpace(name.Text))
                        name.Text = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                }

                continue;
            }

            if (result != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(path.Text))
                return;
            break;
        }
        if (kind.SelectedIndex == 1 && string.IsNullOrWhiteSpace(App.Host.Settings.TmdbApiKey))
        {
            await Message("有信息库需要先在设置里填写 TMDB API Key。");
            return;
        }

        try
        {
            App.Host.Libraries.Add(name.Text, path.Text, kind.SelectedIndex == 1 ? LibraryKind.Catalog : LibraryKind.Raw);
            QueueLibraryReload();
        }
        catch (Exception ex)
        {
            await Message(ex.Message);
        }
    }

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (LibraryList.SelectedItem is not MediaLibrary library)
            return;
        var dialog = new ContentDialog
        {
            Title = "移除媒体库",
            Content = $"从列表中移除“{library.Name}”。磁盘上的视频不会删除。",
            PrimaryButtonText = "移除",
            CloseButtonText = "取消",
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;
        try
        {
            await App.Host.Libraries.RemoveAsync(library.Id);
            QueueLibraryReload();
        }
        catch (Exception ex)
        {
            await Message(ex.Message);
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (LibraryList.SelectedItem is MediaLibrary library)
            App.Host.Libraries.Refresh(library.Id);
        LoadPage();
    }

    private void LibraryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _loading || _syncingLibrary)
            return;
        _folderPath = "";
        RebuildFolderTree(LibraryList.SelectedItem as MediaLibrary);
        _page = 1;
        LoadPage();
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_sorting || _loading || _restoringView)
            return;
        _page = 1;
        _filterTimer.Stop();
        _filterTimer.Start();
    }

    private void Sort_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_sorting || _loading || _restoringView)
            return;
        _page = 1;
        LoadPage();
    }

    private void Filter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_sorting || _loading || _restoringView)
            return;
        _page = 1;
        LoadPage();
    }

    private void SaveViewState()
    {
        if (!_viewRestored || _restoringView)
            return;
        var library = LibraryList.SelectedItem as MediaLibrary;
        var expanded = new List<string>();
        foreach (var (path, node) in _folderTreeNodes)
        {
            if (path.Length > 0 && node.IsExpanded)
                expanded.Add(path);
        }

        App.Host.Settings.LibraryView = new LibraryViewState
        {
            LibraryId = library?.Id,
            FolderPath = _folderPath.Length == 0 ? null : _folderPath,
            Recursive = RecursiveSwitch.IsOn,
            Page = _page,
            PageSize = _pageSize,
            SortMode = library == null ? "" : CurrentSort(library.Kind).ToString(),
            ExpandedFolders = expanded
        };
    }

    private async Task RestoreViewAsync(MediaLibrary library, LibraryViewState view)
    {
        _restoringView = true;
        try
        {
            if (view.PageSize is 30 or 60 or 100 or 200)
                _pageSize = view.PageSize;
            RecursiveSwitch.IsOn = view.Recursive;
            RebuildFolderTree(library);
            foreach (var path in view.ExpandedFolders
                         .Where(path => !string.IsNullOrWhiteSpace(path))
                         .OrderBy(path => path.Count(character => character == '/')))
            {
                var node = await EnsureFolderNodeAsync(path);
                if (node != null)
                    node.IsExpanded = true;
            }

            var folderPath = (view.FolderPath ?? "").Replace('\\', '/').Trim('/');
            var target = await EnsureFolderNodeAsync(folderPath);
            if (target != null)
                FolderTree.SelectedNode = target;
            _folderPath = folderPath;
            RestoreSort(library.Kind, view.SortMode);
            _page = Math.Max(1, view.Page);
        }
        finally
        {
            _restoringView = false;
        }

        LoadPage();
    }

    private async Task<TreeViewNode?> EnsureFolderNodeAsync(string path)
    {
        path = (path ?? "").Replace('\\', '/').Trim('/');
        if (!_folderTreeNodes.TryGetValue("", out var root))
            return null;
        if (path.Length == 0)
            return root;
        var parentPath = HomePlayer.Core.FolderTree.Parent(path) ?? "";
        var parentNode = await EnsureFolderNodeAsync(parentPath);
        if (parentNode?.Content is not FolderNode parentFolder)
            return null;
        if (_folderTreeNodes.TryGetValue(path, out var node))
            return node;
        if (!parentFolder.Loaded)
            await LoadFolderChildrenAsync(parentNode, parentFolder);
        return _folderTreeNodes.GetValueOrDefault(path);
    }

    private void RestoreSort(LibraryKind kind, string? sortMode)
    {
        var mode = Enum.TryParse<SortMode>(sortMode, out var parsed) ? parsed : SortMode.FileName;
        ConfigureSort(kind);
        var index = kind == LibraryKind.Catalog
            ? mode == SortMode.YearDesc ? 1 : 0
            : mode == SortMode.ModifiedDesc ? 1 : 0;
        _sorting = true;
        try
        {
            SortBox.SelectedIndex = index;
        }
        finally
        {
            _sorting = false;
        }
    }

    private void RebuildFolderTree(MediaLibrary? library)
    {
        _folderNodes.Clear();
        _folderTreeNodes.Clear();
        FolderTree.RootNodes.Clear();
        if (library == null)
            return;
        var root = new FolderNode { LibraryId = library.Id, Path = "", Name = library.Name };
        root.SetRecursive(RecursiveSwitch.IsOn);
        var node = new TreeViewNode { Content = root, HasUnrealizedChildren = true };
        _folderNodes[""] = root;
        _folderTreeNodes[""] = node;
        FolderTree.RootNodes.Add(node);
        _ = LoadFolderChildrenAsync(node, root);
    }

    private void RefreshFolderTree()
    {
        var library = LibraryList.SelectedItem as MediaLibrary;
        if (library == null || _folderNodes.Count == 0)
            return;
        var targets = _folderNodes.Values
            .Where(folder => folder.Loaded || folder.Path.Length == 0)
            .ToList();
        if (targets.Count == 0)
            return;
        _ = Task.Run(() =>
        {
            var results = new List<(FolderNode Folder, IReadOnlyList<MediaFolder> Children, MediaFolder? Self)>();
            foreach (var folder in targets)
            {
                try
                {
                    var children = App.Host.Repository.ListChildFolders(library.Id, folder.Path);
                    var self = folder.Path.Length == 0 ? App.Host.Repository.GetFolder(library.Id, "") : null;
                    results.Add((folder, children, self));
                }
                catch (Exception)
                {
                }
            }

            DispatcherQueue.TryEnqueue(() =>
            {
                if ((LibraryList.SelectedItem as MediaLibrary)?.Id != library.Id)
                    return;
                foreach (var (folder, children, self) in results)
                {
                    if (!_folderTreeNodes.TryGetValue(folder.Path, out var node)
                        || !_folderNodes.TryGetValue(folder.Path, out var current)
                        || !ReferenceEquals(current, folder))
                        continue;
                    ApplyFolderChildren(node, folder, children, self);
                }
            });
        });
    }

    private async Task LoadFolderChildrenAsync(TreeViewNode node, FolderNode folder)
    {
        IReadOnlyList<MediaFolder> children;
        MediaFolder? self = null;
        try
        {
            (children, self) = await Task.Run(() =>
            {
                var childFolders = App.Host.Repository.ListChildFolders(folder.LibraryId, folder.Path);
                var folderRow = folder.Path.Length == 0
                    ? App.Host.Repository.GetFolder(folder.LibraryId, "")
                    : null;
                return (childFolders, folderRow);
            });
        }
        catch (Exception)
        {
            children = [];
        }

        void Apply()
        {
            if ((LibraryList.SelectedItem as MediaLibrary)?.Id != folder.LibraryId)
                return;
            if (!_folderNodes.TryGetValue(folder.Path, out var current) || !ReferenceEquals(current, folder))
                return;
            if (!_folderTreeNodes.TryGetValue(folder.Path, out var live) || !ReferenceEquals(live, node))
                return;
            ApplyFolderChildren(node, folder, children, self);
        }

        if (DispatcherQueue.HasThreadAccess)
            Apply();
        else
            DispatcherQueue.TryEnqueue(Apply);
    }

    private void ApplyFolderChildren(
        TreeViewNode node,
        FolderNode folder,
        IReadOnlyList<MediaFolder> children,
        MediaFolder? self)
    {
        if (self != null)
        {
            folder.SetCounts(self.DirectCount, self.RecursiveCount);
            folder.SetCover(LoadCover(self.CoverPath));
            folder.HasChildren = self.HasChildren;
        }

        var models = new Dictionary<string, MediaFolder>(StringComparer.OrdinalIgnoreCase);
        foreach (var child in children)
            models[child.RelativePath] = child;

        for (var i = node.Children.Count - 1; i >= 0; i--)
        {
            if (node.Children[i].Content is not FolderNode existing || models.ContainsKey(existing.Path))
                continue;
            node.Children.RemoveAt(i);
            _folderNodes.Remove(existing.Path);
            _folderTreeNodes.Remove(existing.Path);
        }

        foreach (var model in children)
        {
            if (_folderTreeNodes.TryGetValue(model.RelativePath, out var childNode)
                && childNode.Content is FolderNode existing)
            {
                existing.SetCounts(model.DirectCount, model.RecursiveCount);
                existing.SetCover(LoadCover(model.CoverPath));
                existing.HasChildren = model.HasChildren;
                if (!existing.Loaded)
                    childNode.HasUnrealizedChildren = model.HasChildren;
                continue;
            }

            var folderNode = new FolderNode
            {
                LibraryId = model.LibraryId,
                Path = model.RelativePath,
                Name = model.Name,
                HasChildren = model.HasChildren
            };
            folderNode.SetRecursive(RecursiveSwitch.IsOn);
            folderNode.SetCounts(model.DirectCount, model.RecursiveCount);
            folderNode.SetCover(LoadCover(model.CoverPath));
            var treeNode = new TreeViewNode { Content = folderNode, HasUnrealizedChildren = model.HasChildren };
            node.Children.Add(treeNode);
            _folderNodes[model.RelativePath] = folderNode;
            _folderTreeNodes[model.RelativePath] = treeNode;
        }

        folder.Loaded = true;
        node.HasUnrealizedChildren = false;
    }

    private void FolderTree_Expanding(TreeView sender, TreeViewExpandingEventArgs args)
    {
        DispatcherQueue.TryEnqueue(SaveViewState);
        if (args.Node.Content is not FolderNode folder || folder.Loaded)
            return;
        _ = LoadFolderChildrenAsync(args.Node, folder);
    }

    private void FolderTree_SelectionChanged(TreeView sender, TreeViewSelectionChangedEventArgs args)
    {
        if (_restoringView)
            return;
        var path = (FolderTree.SelectedNode?.Content as FolderNode)?.Path ?? "";
        if (string.Equals(path, _folderPath, StringComparison.OrdinalIgnoreCase))
            return;
        _folderPath = path;
        _page = 1;
        LoadPage();
    }

    private void FolderTree_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (FolderFrom(e.OriginalSource) is not FolderNode folder
            || LibraryList.SelectedItem is not MediaLibrary library)
            return;
        var menu = new MenuFlyout();
        menu.Items.Add(Action("本文件夹随机播放", () => PlayFolderRandomAsync(library, folder)));
        menu.Items.Add(Action("在资源管理器中打开", () =>
        {
            OpenFolder(library, folder);
            return Task.CompletedTask;
        }));
        menu.ShowAt(FolderTree, e.GetPosition(FolderTree));
        e.Handled = true;
    }

    private void FolderTree_Collapsed(TreeView sender, TreeViewCollapsedEventArgs args) => SaveViewState();

    private void Recursive_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_ready || _loading || _restoringView)
            return;
        foreach (var folder in _folderNodes.Values)
            folder.SetRecursive(RecursiveSwitch.IsOn);
        _page = 1;
        SaveViewState();
        LoadPage();
    }

    private async Task PlayFolderRandomAsync(MediaLibrary library, FolderNode folder)
    {
        var recursive = RecursiveSwitch.IsOn;
        var sort = CurrentSort(library.Kind);
        var paths = await Task.Run(() => App.Host.Repository.ListAllPaths(
            library.Id, folder.Path, recursive, null, LibraryFilter.All, sort));
        if (paths.Count == 0)
            return;
        await App.Host.Playback.PlayPathsAsync(Shuffle.Order(paths), 0);
        App.Window.SelectPlayer();
    }

    private static void OpenFolder(MediaLibrary library, FolderNode folder)
    {
        var path = folder.Path.Length == 0
            ? library.RootPath
            : Path.Combine(library.RootPath, folder.Path.Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(path))
            return;
        System.Diagnostics.Process.Start("explorer.exe", $"\"{path}\"");
    }

    private static FolderNode? FolderFrom(object source)
    {
        var element = source as FrameworkElement;
        while (element != null)
        {
            if (element.DataContext is FolderNode folder)
                return folder;
            element = VisualTreeHelper.GetParent(element) as FrameworkElement;
        }

        return null;
    }

    private BitmapImage? LoadCover(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        DateTime stamp;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
                return null;
            stamp = info.LastWriteTimeUtc;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (_coverCache.TryGetValue(path, out var cached) && cached.Stamp == stamp)
            return cached.Image;
        var image = new BitmapImage { DecodePixelWidth = 96, UriSource = new Uri(path) };
        _coverCache[path] = (stamp, image);
        return image;
    }

    private async void Card_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not MediaCard card)
            return;
        e.Handled = true;
        await PlayCardAsync(card);
    }

    private async void Cards_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (CardFrom(e.OriginalSource) is not MediaCard card)
            return;
        await PlayCardAsync(card);
    }

    private async Task PlayCardAsync(MediaCard card)
    {
        var paths = await ListViewPathsAsync(card.Library);
        if (paths.Count == 0)
            return;
        var target = card.Item.FullPath(card.Library.RootPath);
        var index = 0;
        for (var i = 0; i < paths.Count; i++)
        {
            if (!string.Equals(paths[i], target, StringComparison.OrdinalIgnoreCase))
                continue;
            index = i;
            break;
        }

        await App.Host.Playback.PlayPathsAsync(paths, index);
        App.Window.SelectPlayer();
    }

    private async Task<IReadOnlyList<string>> ListViewPathsAsync(MediaLibrary library)
    {
        var folderPath = _folderPath;
        var recursive = RecursiveSwitch.IsOn;
        var query = SearchBox.Text?.Trim() ?? "";
        var sort = CurrentSort(library.Kind);
        var filter = CurrentFilter(library.Kind);
        return await Task.Run(() => App.Host.Repository.ListAllPaths(
            library.Id, folderPath, recursive, query, filter, sort));
    }

    private async void Random_Click(object sender, RoutedEventArgs e)
    {
        if (LibraryList.SelectedItem is not MediaLibrary library)
            return;
        var paths = await ListViewPathsAsync(library);
        if (paths.Count == 0)
            return;
        await App.Host.Playback.PlayPathsAsync(Shuffle.Order(paths), 0);
        App.Window.SelectPlayer();
    }

    private void Cards_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (CardFrom(e.OriginalSource) is not MediaCard card)
            return;
        var menu = new MenuFlyout();
        menu.Items.Add(Action("播放", () => PlayCardAsync(card)));
        menu.Items.Add(Action("加入播放列表", () =>
        {
            App.Host.Playback.Enqueue([card.Item.FullPath(card.Library.RootPath)]);
            return Task.CompletedTask;
        }));
        menu.Items.Add(Action("查看", () => ShowCardAsync(card)));
        menu.Items.Add(Action("在资源管理器中打开", () =>
        {
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{card.Item.FullPath(card.Library.RootPath)}\"");
            return Task.CompletedTask;
        }));
        menu.Items.Add(Action("重新处理", () =>
        {
            App.Host.Libraries.Requeue(card.Item.Id);
            return Task.CompletedTask;
        }));
        if (card.Library.Kind == LibraryKind.Catalog)
            menu.Items.Add(Action("重新匹配", () => MatchAsync(card)));
        menu.ShowAt(Cards, e.GetPosition(Cards));
        e.Handled = true;
    }

    private async Task ShowCardAsync(MediaCard card)
    {
        if (card.Library.Kind == LibraryKind.Raw)
        {
            var grid = new Grid { ColumnSpacing = 8, RowSpacing = 8 };
            for (var i = 0; i < 3; i++)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition());
                grid.RowDefinitions.Add(new RowDefinition());
            }

            for (var i = 0; i < 9; i++)
            {
                var image = new Image { Width = 240, Height = 135, Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill };
                if (i < card.Item.FramePaths.Length)
                    image.Source = LoadImage(card.Item.FramePaths[i]);
                Grid.SetColumn(image, i % 3);
                Grid.SetRow(image, i / 3);
                grid.Children.Add(image);
            }

            await new ContentDialog { Title = card.Title, Content = new ScrollViewer { Content = grid }, CloseButtonText = "关闭", XamlRoot = XamlRoot }.ShowAsync();
            return;
        }

        var body = new StackPanel { Spacing = 8, Width = 480 };
        body.Children.Add(new TextBlock { Text = card.Item.OriginalTitle ?? "", Opacity = 0.7 });
        body.Children.Add(new TextBlock { Text = card.Item.Overview ?? "没有简介。", TextWrapping = TextWrapping.Wrap });
        if (card.Item.MatchStatus != MatchStatus.Matched)
            body.Children.Add(new TextBlock { Text = "这条还没有确认的媒体信息。" });
        var dialog = new ContentDialog
        {
            Title = card.Title,
            Content = body,
            PrimaryButtonText = card.Library.Kind == LibraryKind.Catalog ? "手动匹配" : null,
            CloseButtonText = "关闭",
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            await MatchAsync(card);
    }

    private async Task MatchAsync(MediaCard card)
    {
        var key = App.Host.Settings.TmdbApiKey;
        if (string.IsNullOrWhiteSpace(key))
        {
            await Message("先在设置里填写 TMDB API Key。");
            return;
        }

        var query = new TextBox { Text = FilenameParser.Parse(card.Item.FileName).Title };
        var results = new ListView { Height = 240 };
        var search = new Button { Content = "搜索" };
        var error = new TextBlock { Foreground = new SolidColorBrush(Microsoft.UI.Colors.OrangeRed), TextWrapping = TextWrapping.Wrap };
        var client = new TmdbClient(App.Host.Repository);
        IReadOnlyList<TmdbHit> hits = [];
        var searchVersion = 0;
        search.Click += async (_, _) =>
        {
            var version = ++searchVersion;
            hits = [];
            results.ItemsSource = null;
            error.Text = "";
            search.IsEnabled = false;
            try
            {
                var movies = await client.SearchAsync(query.Text, null, false, key, CancellationToken.None);
                var shows = await client.SearchAsync(query.Text, null, true, key, CancellationToken.None);
                if (version != searchVersion)
                    return;
                hits = movies.Concat(shows).ToList();
                results.ItemsSource = hits.Select(hit => $"{(hit.MediaType == "tv" ? "剧集" : "电影")}  {hit.Title}  {hit.Year}").ToList();
            }
            catch (Exception ex)
            {
                if (version == searchVersion)
                    error.Text = ex.Message;
            }
            finally
            {
                if (version == searchVersion)
                    search.IsEnabled = true;
            }
        };
        var panel = new StackPanel { Spacing = 8, Width = 480 };
        panel.Children.Add(query);
        panel.Children.Add(search);
        panel.Children.Add(error);
        panel.Children.Add(results);
        var dialog = new ContentDialog
        {
            Title = "指定 TMDB 条目",
            Content = panel,
            PrimaryButtonText = "使用所选",
            CloseButtonText = "取消",
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || results.SelectedIndex < 0 || results.SelectedIndex >= hits.Count)
            return;
        var item = App.Host.Repository.GetItem(card.Item.Id);
        if (item == null)
            return;
        await App.Host.Libraries.ApplyHitAsync(item, hits[results.SelectedIndex], CancellationToken.None);
    }

    private async Task Message(string text) =>
        await new ContentDialog { Title = "HomePlayer", Content = text, CloseButtonText = "确定", XamlRoot = XamlRoot }.ShowAsync();

    private static MediaCard? CardFrom(object source) =>
        (source as FrameworkElement)?.DataContext as MediaCard;

    private static MenuFlyoutItem Action(string text, Func<Task> action)
    {
        var item = new MenuFlyoutItem { Text = text };
        item.Click += async (_, _) => await action();
        return item;
    }

    private static string Resolution(MediaItem item) =>
        item.Width is int width && item.Height is int height ? $"{width}×{height}" : "分辨率未知";

    private static string CatalogSubtitle(MediaItem item)
    {
        var year = item.Year?.ToString(CultureInfo.InvariantCulture) ?? "";
        var rating = item.Rating is double value ? $"评分 {value:0.0}" : "";
        var episode = item.Season is int season && item.Episode is int episodeNumber ? $"S{season:00}E{episodeNumber:00}" : "";
        return string.Join(" · ", new[] { year, episode, rating, item.OriginalTitle }.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private BitmapImage? LoadImage(string? path, DateTime stamp)
    {
        if (string.IsNullOrWhiteSpace(path) || stamp == default)
            return null;
        if (_imageCache.TryGetValue(path, out var cached) && cached.Stamp == stamp)
            return cached.Image;
        var image = new BitmapImage();
        image.DecodePixelWidth = 480;
        image.UriSource = new Uri(path);
        RememberImage(path, stamp, image);
        return image;
    }

    private BitmapImage? LoadImage(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
                return null;
            return LoadImage(path, info.LastWriteTimeUtc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void RememberImage(string path, DateTime stamp, BitmapImage image)
    {
        if (!_imageCache.ContainsKey(path))
            _imageOrder.Enqueue(path);
        _imageCache[path] = (stamp, image);
        while (_imageCache.Count > ImageCacheLimit && _imageOrder.Count > 0)
        {
            var oldest = _imageOrder.Dequeue();
            if (!_imageCache.TryGetValue(oldest, out var entry) || ReferenceEquals(entry.Image, image))
                continue;
            _imageCache.Remove(oldest);
        }
    }
}

public sealed class FolderNode : System.ComponentModel.INotifyPropertyChanged
{
    private bool _recursive = true;
    private BitmapImage? _cover;

    public string LibraryId { get; init; } = "";
    public string Path { get; init; } = "";
    public string Name { get; init; } = "";
    public int DirectCount { get; private set; }
    public int RecursiveCount { get; private set; }
    public bool HasChildren { get; set; }
    public bool Loaded { get; set; }

    public BitmapImage? Cover
    {
        get => _cover;
        private set
        {
            if (ReferenceEquals(_cover, value))
                return;
            _cover = value;
            OnPropertyChanged(nameof(Cover));
        }
    }

    public string CountText => (_recursive ? RecursiveCount : DirectCount).ToString("N0", CultureInfo.InvariantCulture);

    public void SetCounts(int direct, int recursive)
    {
        if (DirectCount == direct && RecursiveCount == recursive)
            return;
        DirectCount = direct;
        RecursiveCount = recursive;
        OnPropertyChanged(nameof(CountText));
    }

    public void SetRecursive(bool recursive)
    {
        if (_recursive == recursive)
            return;
        _recursive = recursive;
        OnPropertyChanged(nameof(CountText));
    }

    public void SetCover(BitmapImage? cover) => Cover = cover;

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
}

public sealed class MediaCard
{
    public required MediaItem Item { get; init; }
    public required MediaLibrary Library { get; init; }
    public required string Title { get; init; }
    public required string Subtitle { get; init; }
    public BitmapImage? Image { get; init; }
    public required string Badge { get; init; }
    public double Progress { get; init; }
    public Visibility ProgressVisibility { get; init; }
    public Visibility WatchedVisibility { get; init; }
}
