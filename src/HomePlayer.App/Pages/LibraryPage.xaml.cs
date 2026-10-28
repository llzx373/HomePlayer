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
    private bool _loading;
    private bool _sorting;
    private int _reloadToken;
    private int _loadToken;
    private int _page = 1;
    private int _pageSize = PagedView.DefaultPageSize;
    private int _total;

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
        _reloadTimer.Tick += (_, _) => LoadPage();
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
        Loaded += (_, _) => QueueLibraryReload();
        Unloaded += (_, _) =>
        {
            _reloadTimer.Stop();
            _libraryTimer.Stop();
            _filterTimer.Stop();
        };
    }

    private void QueuePageReload()
    {
        if (!IsLoaded || App.Host.Exiting)
            return;
        App.Host.Libraries.TakeDirtyItems();
        if (!_reloadTimer.IsRunning)
            _reloadTimer.Start();
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
        LibraryList.SelectedItem = _libraries.FirstOrDefault(item => item.Id == selected) ?? _libraries.FirstOrDefault();
        _loading = false;
        _page = 1;
        LoadPage();
    }

    private void LoadPage()
    {
        if (_loading || App.Host.Exiting)
            return;
        var library = LibraryList.SelectedItem as MediaLibrary;
        ConfigureSort(library?.Kind);
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
        _ = Task.Run(() =>
        {
            IReadOnlyList<MediaItem> items;
            int total;
            var page = requestedPage;
            try
            {
                total = App.Host.Repository.CountItems(libraryId, null, true, query, LibraryFilter.All);
                page = PagedView.ClampPage(page, total, pageSize);
                items = App.Host.Repository.ListItemsPage(
                    libraryId, null, true, query, LibraryFilter.All, sort, (page - 1) * pageSize, pageSize);
                if (items.Count == 0 && total > 0 && page > 1)
                {
                    page--;
                    items = App.Host.Repository.ListItemsPage(
                        libraryId, null, true, query, LibraryFilter.All, sort, (page - 1) * pageSize, pageSize);
                }
            }
            catch (Exception)
            {
                items = [];
                total = 0;
            }

            var stamps = ComputeImageStamps(items, kind);
            DispatcherQueue.TryEnqueue(() => ApplyPage(library, page, pageSize, total, items, stamps, token));
        });
    }

    private void ApplyPage(
        MediaLibrary library,
        int page,
        int pageSize,
        int total,
        IReadOnlyList<MediaItem> items,
        IReadOnlyDictionary<string, DateTime> stamps,
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
            var card = CreateCard(item, library, stamps);
            if (card != null)
                cards.Add(card);
        }

        _cards.ReplaceAll(cards);
        Pager.Update(total, page, pageSize);
        UpdateEmptyText();
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
        IReadOnlyDictionary<string, DateTime> stamps)
    {
        var title = library.Kind == LibraryKind.Catalog && !string.IsNullOrWhiteSpace(item.Title) && item.MatchStatus == MatchStatus.Matched
            ? item.Title!
            : item.FileName;
        var subtitle = library.Kind == LibraryKind.Raw
            ? $"{ClockText.FormatSeconds(item.DurationSeconds)} · {Resolution(item)} · {item.VideoCodec ?? item.Status.ToString()}"
            : CatalogSubtitle(item);
        var imagePath = library.Kind == LibraryKind.Raw ? item.SheetPath : item.PosterPath;
        var stamp = !string.IsNullOrWhiteSpace(imagePath) && stamps.TryGetValue(imagePath, out var value) ? value : default;
        return new MediaCard
        {
            Item = item,
            Library = library,
            Title = title,
            Subtitle = item.Error ?? subtitle,
            Image = LoadImage(imagePath, stamp),
            Badge = library.Kind == LibraryKind.Catalog && item.MatchStatus != MatchStatus.Matched ? "待核对" : ""
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

        EmptyText.Text = LibraryList.SelectedItem is not MediaLibrary
            ? "选择一个媒体库。"
            : App.Host.Libraries.PendingCount > 0 ? "正在扫描和生成预览…" : "这个库里还没有视频。";
    }

    private SortMode CurrentSort(LibraryKind kind) => kind == LibraryKind.Catalog
        ? SortBox.SelectedIndex == 1 ? SortMode.YearDesc : SortMode.Title
        : SortBox.SelectedIndex == 1 ? SortMode.ModifiedDesc : SortMode.FileName;

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
        if (_loading)
            return;
        _page = 1;
        LoadPage();
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_sorting || _loading)
            return;
        _page = 1;
        _filterTimer.Stop();
        _filterTimer.Start();
    }

    private void Sort_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_sorting || _loading)
            return;
        _page = 1;
        LoadPage();
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
        var paths = _cards.Select(item => item.Item.FullPath(item.Library.RootPath)).ToList();
        var index = _cards.IndexOf(card);
        await App.Host.Playback.PlayPathsAsync(paths, Math.Max(0, index));
        App.Window.SelectPlayer();
    }

    private void Cards_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (CardFrom(e.OriginalSource) is not MediaCard card)
            return;
        var menu = new MenuFlyout();
        menu.Items.Add(Action("播放", async () =>
        {
            var paths = _cards.Select(item => item.Item.FullPath(item.Library.RootPath)).ToList();
            await App.Host.Playback.PlayPathsAsync(paths, Math.Max(0, _cards.IndexOf(card)));
            App.Window.SelectPlayer();
        }));
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

public sealed class MediaCard
{
    public required MediaItem Item { get; init; }
    public required MediaLibrary Library { get; init; }
    public required string Title { get; init; }
    public required string Subtitle { get; init; }
    public BitmapImage? Image { get; init; }
    public required string Badge { get; init; }
}
