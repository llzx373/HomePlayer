using System.Globalization;
using HomePlayer.Core;
using HomePlayer.Library;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace HomePlayer;

public sealed partial class LibraryPage
{
    private bool _changingFilters;
    private IReadOnlyDictionary<string, MediaLibrary> _pageLibraries = new Dictionary<string, MediaLibrary>();

    private void InitializeManagement()
    {
        MediaList.ItemsSource = _cards;
        WatchBox.SelectedIndex = 0;
        MediaTypeBox.SelectedIndex = 0;
        ResolutionBox.SelectedIndex = 0;
        CodecBox.SelectedIndex = 0;
        DurationBox.SelectedIndex = 0;
        SubtitleBox.SelectedIndex = 0;
        SetSelectionMode(false);
        UpdateSelectionActions();
        InitializeCollectionFilters();
    }

    private bool UsesLibraryQuery => _browseMode is LibraryBrowseMode.Library or LibraryBrowseMode.Favorites or LibraryBrowseMode.Series;
    private bool AllLibraries => ScopeBox.SelectedIndex == 1 || _browseMode != LibraryBrowseMode.Library;
    private ListViewBase ActiveItems => LayoutBox.SelectedIndex == 2 ? MediaList : Cards;

    private MediaLibraryQuery CurrentQuery()
    {
        var library = LibraryList.SelectedItem as MediaLibrary;
        return ApplyCollectionQuery(new MediaLibraryQuery
        {
            LibraryId = AllLibraries ? null : library?.Id,
            RelativeDir = AllLibraries ? null : _folderPath,
            Recursive = RecursiveSwitch.IsOn,
            Search = SearchBox.Text,
            Filter = library == null ? LibraryFilter.All : CurrentFilter(library.Kind),
            Watch = (WatchFilter)Math.Max(0, WatchBox.SelectedIndex),
            MediaType = (MediaTypeFilter)Math.Max(0, MediaTypeBox.SelectedIndex),
            Resolution = (ResolutionFilter)Math.Max(0, ResolutionBox.SelectedIndex),
            Codec = (CodecFilter)Math.Max(0, CodecBox.SelectedIndex),
            Duration = (DurationFilter)Math.Max(0, DurationBox.SelectedIndex),
            Year = int.TryParse(YearBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var year) && year is >= 1800 and <= 3000 ? year : null,
            Subtitles = (PresenceFilter)Math.Max(0, SubtitleBox.SelectedIndex),
            MultipleAudioTracks = MultiAudioSwitch.IsOn,
            FavoritesOnly = _browseMode == LibraryBrowseMode.Favorites
        });
    }

    private void UpdateBrowseChrome()
    {
        var queryMode = UsesLibraryQuery;
        LibraryToolbar.Visibility = queryMode ? Visibility.Visible : Visibility.Collapsed;
        ViewToolbar.Visibility = queryMode ? Visibility.Visible : Visibility.Collapsed;
        Pager.Visibility = _browseMode == LibraryBrowseMode.Series ? Visibility.Collapsed : queryMode ? Visibility.Visible : Visibility.Collapsed;
        RecursiveSwitch.IsEnabled = _browseMode == LibraryBrowseMode.Library && !AllLibraries;
        FolderTree.IsEnabled = _browseMode == LibraryBrowseMode.Library && !AllLibraries;
        CardSizeBox.IsEnabled = LayoutBox.SelectedIndex != 2;
        var showList = queryMode && LayoutBox.SelectedIndex == 2;
        Cards.Visibility = showList ? Visibility.Collapsed : Visibility.Visible;
        MediaList.Visibility = showList ? Visibility.Visible : Visibility.Collapsed;
        if (!queryMode)
            BulkBar.Visibility = Visibility.Collapsed;
    }

    private void LoadManagementPage()
    {
        if (_loading || App.Host.Exiting)
            return;
        var library = LibraryList.SelectedItem as MediaLibrary;
        ConfigureSort(library?.Kind);
        UpdateScanWarning();
        UpdateBrowseChrome();
        var token = ++_loadToken;
        if (!UsesLibraryQuery)
        {
            LoadSpecialPage(token);
            return;
        }
        if (library == null && !AllLibraries)
        {
            _pageLoading = false;
            _cards.Clear();
            _total = 0;
            Pager.Update(0, 1, _pageSize);
            UpdateEmptyText();
            return;
        }

        var query = CurrentQuery();
        var sort = CurrentSort(library?.Kind ?? LibraryKind.Raw);
        var requestedPage = _page;
        var pageSize = _pageSize;
        _pageLoading = true;
        UpdateEmptyText();
        _ = Task.Run(() =>
        {
            IReadOnlyList<MediaItem> items;
            var total = 0;
            string? error = null;
            var page = requestedPage;
            IReadOnlyList<MediaSeriesGroup> groups = [];
            try
            {
                total = App.Host.Repository.CountItems(query);
                if (_browseMode == LibraryBrowseMode.Series)
                {
                    items = App.Host.Repository.ListItemsPage(query, SortMode.Episode, 0, Math.Max(1, total));
                }
                else
                {
                    page = PagedView.ClampPage(page, total, pageSize);
                    items = App.Host.Repository.ListItemsPage(query, sort, (page - 1) * pageSize, pageSize);
                }
            }
            catch (Exception ex)
            {
                items = [];
                error = LoadFailure("读取媒体条目", ex);
            }

            var libraries = App.Host.Repository.ListLibraries().ToDictionary(item => item.Id, StringComparer.Ordinal);
            var states = LoadStates(items, libraries);
            var favorites = App.Host.Repository.FavoriteStates(items.Select(item => item.Id).ToList());
            if (_browseMode == LibraryBrowseMode.Series)
            {
                groups = MediaGrouping.Build(items
                    .Where(item => libraries.ContainsKey(item.LibraryId))
                    .Select(item => new MediaSeriesEntry(item, libraries[item.LibraryId], states.GetValueOrDefault(item.FullPath(libraries[item.LibraryId].RootPath)))));
                total = groups.Count;
            }
            var stamps = ComputeImageStamps(items, libraries);
            DispatcherQueue.TryEnqueue(() => ApplyManagementPage(page, pageSize, total, items, groups, libraries, stamps, states, favorites, token, error));
        });
    }

    private void ApplyManagementPage(
        int page,
        int pageSize,
        int total,
        IReadOnlyList<MediaItem> items,
        IReadOnlyList<MediaSeriesGroup> groups,
        IReadOnlyDictionary<string, MediaLibrary> libraries,
        IReadOnlyDictionary<string, DateTime> stamps,
        IReadOnlyDictionary<string, PlaybackState> states,
        IReadOnlyDictionary<string, bool> favorites,
        int token,
        string? error)
    {
        if (token != _loadToken || App.Host.Exiting)
            return;
        _pageLoading = false;
        SetLoadError(error);
        if (error != null)
        {
            UpdateEmptyText();
            return;
        }
        _page = page;
        _pageSize = pageSize;
        _total = total;
        _pageLibraries = libraries;
        var cards = new List<MediaCard>(_browseMode == LibraryBrowseMode.Series ? groups.Count : items.Count);
        if (_browseMode == LibraryBrowseMode.Series)
        {
            foreach (var group in groups)
            {
                var entry = group.NextUnwatched ?? group.OrderedItems[0];
                var baseCard = CreateCard(entry.Item, entry.Library, stamps, states);
                if (baseCard != null)
                    cards.Add(ManagementCard(baseCard, favorites, group));
            }
        }
        else
        {
            foreach (var item in items)
            {
                if (!libraries.TryGetValue(item.LibraryId, out var itemLibrary))
                    continue;
                var card = CreateCard(item, itemLibrary, stamps, states);
                if (card != null)
                    cards.Add(ManagementCard(card, favorites));
            }
        }
        _cards.ReplaceAll(cards);
        Pager.Update(_browseMode == LibraryBrowseMode.Series ? 0 : total, page, pageSize);
        _pendingUpdates = 0;
        _pendingAll = false;
        UpdateBar.IsOpen = false;
        UpdateEmptyText();
        UpdateSelectionActions();
        SaveViewState();
    }

    private IReadOnlyDictionary<string, PlaybackState> LoadStates(IReadOnlyList<MediaItem> items, IReadOnlyDictionary<string, MediaLibrary> libraries)
    {
        try
        {
            return App.Host.Repository.LoadPlaybackStates(items
                .Where(item => libraries.ContainsKey(item.LibraryId))
                .Select(item => item.FullPath(libraries[item.LibraryId].RootPath))
                .ToList());
        }
        catch (Exception)
        {
            return new Dictionary<string, PlaybackState>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static IReadOnlyDictionary<string, DateTime> ComputeImageStamps(
        IReadOnlyList<MediaItem> items,
        IReadOnlyDictionary<string, MediaLibrary> libraries)
    {
        var stamps = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            if (!libraries.TryGetValue(item.LibraryId, out var library))
                continue;
            var path = library.Kind == LibraryKind.Raw ? item.SheetPath : item.PosterPath;
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

    private MediaCard ManagementCard(MediaCard card, IReadOnlyDictionary<string, bool> favorites, MediaSeriesGroup? group = null)
    {
        var layout = (LibraryLayout)Math.Max(0, LayoutBox.SelectedIndex);
        var compact = layout == LibraryLayout.CompactCards;
        var size = (LibraryCardSize)Math.Max(0, CardSizeBox.SelectedIndex);
        var width = compact ? 160 : size switch { LibraryCardSize.Small => 180, LibraryCardSize.Large => 300, _ => 220 };
        var imageHeight = compact ? 100 : size switch { LibraryCardSize.Small => 115, LibraryCardSize.Large => 195, _ => 150 };
        var info = (LibraryInfoDisplay)Math.Max(0, InfoBox.SelectedIndex);
        var subtitle = info switch
        {
            LibraryInfoDisplay.Path => card.Item.RelativePath,
            LibraryInfoDisplay.Technical => TechnicalSubtitle(card.Item),
            _ => card.Subtitle
        };
        if (group != null)
        {
            var next = group.NextUnwatched;
            subtitle = group.IsMovieCollection
                ? $"{group.EpisodeCount} 部 · {group.WatchedCount} 部已看"
                : $"{group.Seasons.Count} 季 · {group.WatchedCount}/{group.EpisodeCount} 已看" +
                    (next == null ? " · 全部看完" : $" · 下一集 {EpisodeLabel(next.Item)}");
        }
        return CopyCard(card) with
        {
            Subtitle = subtitle,
            CardWidth = width,
            ImageHeight = imageHeight,
            FavoriteVisibility = favorites.GetValueOrDefault(card.Item.Id) ? Visibility.Visible : Visibility.Collapsed,
            SeriesGroup = group,
            FullPath = card.Item.FullPath(card.Library.RootPath),
            DurationText = ClockText.FormatSeconds(card.Item.DurationSeconds),
            ResolutionText = Resolution(card.Item),
            CodecText = card.Item.VideoCodec ?? "未知",
            SizeText = FileSize(card.Item.Size),
            AddedText = AddedDate(card.Item),
            WatchText = card.WatchedVisibility == Visibility.Visible ? "已看" : card.Progress > 0 ? "在看" : "未看",
            LibraryText = card.Library.Name
        };
    }

    private static MediaCard CopyCard(MediaCard card) => new()
    {
        Item = card.Item,
        Library = card.Library,
        Title = card.Title,
        Subtitle = card.Subtitle,
        Image = card.Image,
        Badge = card.Badge,
        BadgeVisibility = card.BadgeVisibility,
        BadgeBackground = card.BadgeBackground,
        Progress = card.Progress,
        ProgressVisibility = card.ProgressVisibility,
        WatchedVisibility = card.WatchedVisibility,
        HasLibraryEntry = card.HasLibraryEntry,
        IsHistoryEntry = card.IsHistoryEntry,
        HistoryPath = card.HistoryPath,
        CardWidth = card.CardWidth,
        ImageHeight = card.ImageHeight,
        FavoriteVisibility = card.FavoriteVisibility,
        SeriesGroup = card.SeriesGroup
    };

    private static string TechnicalSubtitle(MediaItem item) =>
        $"{ClockText.FormatSeconds(item.DurationSeconds)} · {Resolution(item)} · {item.VideoCodec ?? "编码未知"} · {FileSize(item.Size)}";

    private static string FileSize(long bytes) => bytes >= 1024L * 1024 * 1024
        ? (bytes / 1024d / 1024 / 1024).ToString("0.00", CultureInfo.CurrentCulture) + " GB"
        : (bytes / 1024d / 1024).ToString("0.0", CultureInfo.CurrentCulture) + " MB";

    private static string EpisodeLabel(MediaItem item) => item.Season == null && item.Episode == null
        ? item.FileName
        : $"S{item.Season ?? 0:00}E{item.Episode ?? 0:00}";

    private void Scope_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _sorting || _loading || _restoringView)
            return;
        _page = 1;
        UpdateBrowseChrome();
        LoadPage();
    }

    private void AdvancedFilter_SelectionChanged(object sender, SelectionChangedEventArgs e) => QueueAdvancedFilter();
    private void MultiAudio_Toggled(object sender, RoutedEventArgs e) => QueueAdvancedFilter();
    private void Year_TextChanged(object sender, TextChangedEventArgs e) => QueueAdvancedFilter();

    private void QueueAdvancedFilter()
    {
        if (!_ready || _changingFilters || _loading || _restoringView)
            return;
        _page = 1;
        _filterTimer.Stop();
        _filterTimer.Start();
    }

    private void ClearAdvancedFilters_Click(object sender, RoutedEventArgs e)
    {
        _changingFilters = true;
        try
        {
            WatchBox.SelectedIndex = 0;
            MediaTypeBox.SelectedIndex = 0;
            ResolutionBox.SelectedIndex = 0;
            CodecBox.SelectedIndex = 0;
            DurationBox.SelectedIndex = 0;
            SubtitleBox.SelectedIndex = 0;
            YearBox.Text = "";
            MultiAudioSwitch.IsOn = false;
            TagFilterBox.SelectedIndex = 0;
            CollectionFilterBox.SelectedIndex = 0;
        }
        finally
        {
            _changingFilters = false;
        }
        AdvancedFiltersButton.Flyout?.Hide();
        _page = 1;
        LoadPage();
    }

    private void Layout_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _restoringView)
            return;
        Cards.Visibility = LayoutBox.SelectedIndex == 2 ? Visibility.Collapsed : Visibility.Visible;
        MediaList.Visibility = LayoutBox.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        CardSizeBox.IsEnabled = LayoutBox.SelectedIndex != 2;
        SetSelectionMode(MultiSelectToggle.IsChecked == true);
        LoadPage();
    }

    private void CardSize_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ready && !_restoringView)
            LoadPage();
    }

    private void Info_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ready && !_restoringView)
            LoadPage();
    }

    private void MultiSelect_Click(object sender, RoutedEventArgs e) => SetSelectionMode(MultiSelectToggle.IsChecked == true);

    private void SetSelectionMode(bool multiple)
    {
        Cards.SelectionMode = multiple ? ListViewSelectionMode.Multiple : ListViewSelectionMode.Single;
        MediaList.SelectionMode = multiple ? ListViewSelectionMode.Multiple : ListViewSelectionMode.Single;
        if (!multiple)
            BulkBar.Visibility = Visibility.Collapsed;
        UpdateSelectionActions();
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        MultiSelectToggle.IsChecked = true;
        SetSelectionMode(true);
        ActiveItems.SelectAll();
        UpdateSelectionActions();
    }

    private void Cards_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSelectionActions();

    private IReadOnlyList<MediaCard> SelectedCards() => ActiveItems.SelectedItems.OfType<MediaCard>().ToList();

    private void UpdateSelectionActions()
    {
        if (MultiSelectToggle?.IsChecked != true || BulkBar == null)
            return;
        var count = ActiveItems.SelectedItems.Count;
        SelectionText.Text = $"已选 {count} 项";
        BulkBar.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BulkEnqueue_Click(object sender, RoutedEventArgs e)
    {
        var paths = SelectedCards().Select(card => card.Item.FullPath(card.Library.RootPath)).ToList();
        if (paths.Count > 0)
            App.Host.Playback.Enqueue(paths);
    }

    private void BulkWatched_Click(object sender, RoutedEventArgs e)
    {
        var watched = (sender as FrameworkElement)?.Tag as string == "True";
        foreach (var card in SelectedCards())
            App.Host.Playback.SetWatched(card.Item.FullPath(card.Library.RootPath), watched);
        LoadPage();
    }

    private async void BulkFavorite_Click(object sender, RoutedEventArgs e)
    {
        var favorite = (sender as FrameworkElement)?.Tag as string == "True";
        var ids = SelectedCards().Where(card => card.HasLibraryEntry).Select(card => card.Item.Id).ToList();
        await Task.Run(() => App.Host.Repository.SetFavorites(ids, favorite));
        LoadPage();
    }

    private void BulkReprocess_Click(object sender, RoutedEventArgs e)
    {
        foreach (var card in SelectedCards().Where(card => card.HasLibraryEntry))
            App.Host.Libraries.Requeue(card.Item.Id);
        LoadPage();
    }

    private async void BulkMatch_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(App.Host.Settings.TmdbApiKey))
        {
            await Message("先在设置里填写 TMDB API Key。");
            return;
        }
        foreach (var card in SelectedCards().Where(card => card.HasLibraryEntry && card.Library.Kind == LibraryKind.Catalog))
            App.Host.Libraries.Requeue(card.Item.Id);
        LoadPage();
    }

    private async void BulkRemove_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedCards().Where(card => card.HasLibraryEntry).ToList();
        if (selected.Count == 0)
            return;
        var dialog = new ContentDialog
        {
            Title = "从媒体库移除",
            Content = $"从媒体库移除所选的 {selected.Count} 项。磁盘上的视频不会删除；下次完整扫描可能再次入库。",
            PrimaryButtonText = "移除",
            CloseButtonText = "取消",
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;
        await Task.Run(() => App.Host.Repository.DeleteItems(selected.Select(card => card.Item.Id).ToList()));
        LoadPage();
    }
}
