using HomePlayer.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HomePlayer;

public sealed partial class LibraryPage
{
    private sealed record CollectionChoice(MediaCollection? Collection)
    {
        public override string ToString() => Collection == null
            ? "全部"
            : $"{Collection.Name}（{(Collection.Kind == MediaCollectionKind.Manual ? "手动" : "智能" )}）";
    }

    private void InitializeCollectionFilters()
    {
        _changingFilters = true;
        try
        {
            var selectedTag = TagFilterBox.SelectedItem as string;
            var selectedCollection = (CollectionFilterBox.SelectedItem as CollectionChoice)?.Collection?.Id;
            TagFilterBox.ItemsSource = new[] { "全部" }.Concat(App.Host.Repository.ListTags()).ToArray();
            CollectionFilterBox.ItemsSource = new[] { new CollectionChoice(null) }
                .Concat(App.Host.Repository.ListCollections().Select(collection => new CollectionChoice(collection)))
                .ToArray();
            TagFilterBox.SelectedItem = selectedTag ?? "全部";
            CollectionFilterBox.SelectedItem = ((IEnumerable<CollectionChoice>)CollectionFilterBox.ItemsSource)
                .FirstOrDefault(choice => choice.Collection?.Id == selectedCollection)
                ?? ((IEnumerable<CollectionChoice>)CollectionFilterBox.ItemsSource).First();
        }
        finally
        {
            _changingFilters = false;
        }
    }

    private MediaLibraryQuery ApplyCollectionQuery(MediaLibraryQuery query)
    {
        var tag = TagFilterBox.SelectedItem as string;
        var collection = (CollectionFilterBox.SelectedItem as CollectionChoice)?.Collection;
        query = query with { Tag = tag == "全部" ? null : tag };
        if (collection == null)
            return query;
        if (collection.Kind == MediaCollectionKind.Manual)
            return query with { CollectionId = collection.Id };
        var rule = collection.Rule;
        return query with
        {
            AdditionalSearch = string.IsNullOrWhiteSpace(rule.Search) ? null : rule.Search,
            AdditionalTag = string.IsNullOrWhiteSpace(rule.Tag) ? null : rule.Tag,
            Watch = rule.Watch == WatchFilter.All ? query.Watch : rule.Watch,
            FavoritesOnly = query.FavoritesOnly || rule.FavoritesOnly
        };
    }

    private async void BulkTags_Click(object sender, RoutedEventArgs e)
    {
        var ids = SelectedCards().Where(card => card.HasLibraryEntry).Select(card => card.Item.Id).ToList();
        if (ids.Count == 0)
            return;
        var input = new TextBox { PlaceholderText = "标签用逗号分隔，例如：科幻, 4K, 待复习" };
        var dialog = new ContentDialog
        {
            Title = $"设置 {ids.Count} 项媒体的标签",
            Content = input,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;
        var tags = input.Text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        await Task.Run(() => App.Host.Repository.SetTags(ids, tags));
        InitializeCollectionFilters();
        LoadPage();
    }

    private async void BulkCollection_Click(object sender, RoutedEventArgs e)
    {
        var ids = SelectedCards().Where(card => card.HasLibraryEntry).Select(card => card.Item.Id).ToList();
        if (ids.Count == 0)
            return;
        var collections = App.Host.Repository.ListCollections().Where(collection => collection.Kind == MediaCollectionKind.Manual).ToArray();
        if (collections.Length == 0)
        {
            await Message("请先创建一个手动合集。");
            return;
        }
        var box = new ComboBox { ItemsSource = collections, DisplayMemberPath = nameof(MediaCollection.Name), SelectedIndex = 0 };
        var dialog = new ContentDialog
        {
            Title = $"将 {ids.Count} 项加入手动合集",
            Content = box,
            PrimaryButtonText = "加入",
            CloseButtonText = "取消",
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && box.SelectedItem is MediaCollection collection)
            await Task.Run(() => App.Host.Repository.AddToCollection(collection.Id, ids));
    }

    private async void ManageCollections_Click(object sender, RoutedEventArgs e)
    {
        var name = new TextBox { PlaceholderText = "合集名称" };
        var kind = new ComboBox { SelectedIndex = 0 };
        kind.Items.Add("手动合集（固定成员）");
        kind.Items.Add("智能合集（按规则实时更新）");
        var search = new TextBox { PlaceholderText = "标题或文件名包含（可选）" };
        var tag = new TextBox { PlaceholderText = "标签（可选）" };
        var watch = new ComboBox { SelectedIndex = 0 };
        watch.Items.Add("不限观看状态");
        watch.Items.Add("未看");
        watch.Items.Add("在看");
        watch.Items.Add("已看");
        var favorites = new CheckBox { Content = "仅收藏" };
        var list = new ListView { Height = 150, DisplayMemberPath = nameof(MediaCollection.Name) };
        var panel = new StackPanel { Spacing = 8, Width = 420 };
        panel.Children.Add(name);
        panel.Children.Add(kind);
        panel.Children.Add(search);
        panel.Children.Add(tag);
        panel.Children.Add(watch);
        panel.Children.Add(favorites);
        var delete = new Button { Content = "删除下方选中的合集" };
        panel.Children.Add(list);
        panel.Children.Add(delete);
        var dialog = new ContentDialog
        {
            Title = "自定义合集",
            Content = new ScrollViewer { Content = panel, MaxHeight = 560 },
            PrimaryButtonText = "创建",
            CloseButtonText = "关闭",
            XamlRoot = XamlRoot
        };
        void Refresh()
        {
            list.ItemsSource = App.Host.Repository.ListCollections();
            InitializeCollectionFilters();
        }
        Refresh();
        delete.Click += (_, _) =>
        {
            if (list.SelectedItem is MediaCollection collection)
            {
                App.Host.Repository.DeleteCollection(collection.Id);
                Refresh();
            }
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;
        try
        {
            var rule = new SmartCollectionRule(search.Text.Trim(), tag.Text.Trim(), (WatchFilter)watch.SelectedIndex, favorites.IsChecked == true);
            App.Host.Repository.CreateCollection(name.Text, kind.SelectedIndex == 0 ? MediaCollectionKind.Manual : MediaCollectionKind.Smart, rule);
            InitializeCollectionFilters();
        }
        catch (Exception ex)
        {
            await Message(ex.Message);
        }
    }
}
