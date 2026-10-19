using System.Collections.ObjectModel;
using System.Globalization;
using HomePlayer.Core;
using HomePlayer.Library;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;

namespace HomePlayer;

public sealed partial class LibraryPage : Page
{
    private readonly ObservableCollection<MediaLibrary> _libraries = [];
    private readonly ObservableCollection<MediaCard> _cards = [];
    private bool _loading;
    private bool _sorting;
    private bool _reloadQueued;

    public LibraryPage()
    {
        InitializeComponent();
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
        LibraryList.DisplayMemberPath = nameof(MediaLibrary.Name);
        LibraryList.ItemsSource = _libraries;
        Cards.ItemsSource = _cards;
        App.Host.Libraries.Changed += (_, _) => QueueReload();
        Loaded += (_, _) => QueueReload();
    }

    private void QueueReload()
    {
        if (_reloadQueued)
            return;
        _reloadQueued = true;
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            _reloadQueued = false;
            Reload();
        });
    }

    private void Reload()
    {
        var selected = (LibraryList.SelectedItem as MediaLibrary)?.Id;
        var libraries = App.Host.Repository.ListLibraries();
        _loading = true;
        _libraries.Clear();
        foreach (var library in libraries)
            _libraries.Add(library);
        LibraryList.SelectedItem = _libraries.FirstOrDefault(item => item.Id == selected) ?? _libraries.FirstOrDefault();
        _loading = false;
        BindCards();
        EmptyText.Visibility = libraries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var pending = App.Host.Libraries.PendingCount;
        EmptyText.Text = libraries.Count == 0
            ? "还没有媒体库。添加一个本地文件夹开始。"
            : pending > 0 ? $"正在处理后台队列，剩余约 {pending} 项。" : "";
    }

    private void BindCards()
    {
        if (_loading)
            return;
        var library = LibraryList.SelectedItem as MediaLibrary;
        _cards.Clear();
        if (library == null)
        {
            ConfigureSort(null);
            return;
        }

        ConfigureSort(library.Kind);
        var query = SearchBox.Text?.Trim() ?? "";
        var items = App.Host.Repository.ListItems(library.Id);
        IEnumerable<MediaItem> sorted = library.Kind == LibraryKind.Raw
            ? (SortBox.SelectedIndex == 1
                ? items.OrderByDescending(item => item.MtimeUtcTicks)
                : items.OrderBy(item => item.FileName, StringComparer.OrdinalIgnoreCase))
            : (SortBox.SelectedIndex == 1
                ? items.OrderByDescending(item => item.Year ?? 0).ThenBy(item => item.Title)
                : items.OrderBy(item => item.Title ?? item.FileName, StringComparer.OrdinalIgnoreCase));
        foreach (var item in sorted)
        {
            var title = library.Kind == LibraryKind.Catalog && !string.IsNullOrWhiteSpace(item.Title) && item.MatchStatus == MatchStatus.Matched
                ? item.Title!
                : item.FileName;
            if (query.Length > 0 && title.Contains(query, StringComparison.OrdinalIgnoreCase) == false && item.FileName.Contains(query, StringComparison.OrdinalIgnoreCase) == false)
                continue;
            var subtitle = library.Kind == LibraryKind.Raw
                ? $"{ClockText.FormatSeconds(item.DurationSeconds)} · {Resolution(item)} · {item.VideoCodec ?? item.Status.ToString()}"
                : CatalogSubtitle(item);
            _cards.Add(new MediaCard
            {
                Item = item,
                Library = library,
                Title = title,
                Subtitle = item.Error ?? subtitle,
                Image = LoadImage(library.Kind == LibraryKind.Raw ? item.SheetPath : item.PosterPath),
                Badge = library.Kind == LibraryKind.Catalog && item.MatchStatus != MatchStatus.Matched ? "待核对" : ""
            });
        }

        EmptyText.Visibility = _cards.Count == 0 && _libraries.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_cards.Count == 0 && library != null)
            EmptyText.Text = App.Host.Libraries.PendingCount > 0 ? "正在扫描和生成预览…" : "这个库里还没有视频。";
    }

    private void ConfigureSort(LibraryKind? kind)
    {
        var catalog = kind == LibraryKind.Catalog;
        if (SortBox.Items.Count > 0 && Equals(SortBox.Tag, catalog))
            return;
        _sorting = true;
        try
        {
            SortBox.Tag = catalog;
            var index = SortBox.SelectedIndex;
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

            SortBox.SelectedIndex = index >= 0 && index < SortBox.Items.Count ? index : 0;
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
                var folder = await FolderDialog.PickAsync();
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
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            App.Host.Libraries.Remove(library.Id);
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (LibraryList.SelectedItem is MediaLibrary library)
            App.Host.Libraries.Refresh(library.Id);
    }

    private void LibraryList_SelectionChanged(object sender, SelectionChangedEventArgs e) => BindCards();

    private void Filter_Changed(object sender, object e)
    {
        if (_sorting || _loading)
            return;
        BindCards();
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
        var client = new TmdbClient(App.Host.Repository);
        IReadOnlyList<TmdbHit> hits = [];
        search.Click += async (_, _) =>
        {
            try
            {
                var movies = await client.SearchAsync(query.Text, null, false, key, CancellationToken.None);
                var shows = await client.SearchAsync(query.Text, null, true, key, CancellationToken.None);
                hits = movies.Concat(shows).ToList();
                results.ItemsSource = hits.Select(hit => $"{(hit.MediaType == "tv" ? "剧集" : "电影")}  {hit.Title}  {hit.Year}").ToList();
            }
            catch (Exception ex)
            {
                await Message(ex.Message);
            }
        };
        var panel = new StackPanel { Spacing = 8, Width = 480 };
        panel.Children.Add(query);
        panel.Children.Add(search);
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
        await App.Host.Libraries.ApplyHitAsync(item, hits[results.SelectedIndex], key, CancellationToken.None);
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

    private static BitmapImage? LoadImage(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;
        var image = new BitmapImage();
        image.DecodePixelWidth = 480;
        image.UriSource = new Uri(path);
        return image;
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
