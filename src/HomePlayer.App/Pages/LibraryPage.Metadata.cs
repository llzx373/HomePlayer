using System.Globalization;
using HomePlayer.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace HomePlayer;

public sealed partial class LibraryPage
{
    private async Task EditMetadataAsync(MediaCard card)
    {
        var item = card.Item;
        var title = new TextBox { Header = "标题", Text = item.Title ?? "" };
        var originalTitle = new TextBox { Header = "原始标题", Text = item.OriginalTitle ?? "" };
        var year = new TextBox { Header = "年份", Text = item.Year?.ToString(CultureInfo.InvariantCulture) ?? "" };
        var season = new TextBox { Header = "季编号（特别篇为 0）", Text = item.Season?.ToString(CultureInfo.InvariantCulture) ?? "" };
        var episode = new TextBox { Header = "集编号", Text = item.Episode?.ToString(CultureInfo.InvariantCulture) ?? "" };
        var collection = new TextBox { Header = "电影合集名称", Text = item.CollectionName ?? "" };
        var poster = new TextBox { Header = "本地海报路径", Text = item.PosterPath ?? "" };
        var overview = new TextBox { Header = "简介", Text = item.Overview ?? "", AcceptsReturn = true, MinHeight = 100, TextWrapping = TextWrapping.Wrap };
        var type = new ComboBox { Header = "类型", SelectedIndex = item.MediaType == "tv" ? 2 : item.MediaType == "movie" ? 1 : 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        type.Items.Add("未知");
        type.Items.Add("电影");
        type.Items.Add("剧集");
        var dialog = new ContentDialog
        {
            Title = "编辑元数据",
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            XamlRoot = XamlRoot
        };
        var body = new StackPanel { Width = 500, Spacing = 8 };
        body.Children.Add(title);
        body.Children.Add(originalTitle);
        body.Children.Add(type);
        body.Children.Add(year);
        body.Children.Add(season);
        body.Children.Add(episode);
        body.Children.Add(collection);
        body.Children.Add(poster);
        body.Children.Add(overview);
        dialog.Content = new ScrollViewer { Content = body, MaxHeight = 620 };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;
        item.Title = NullIfEmpty(title.Text);
        item.OriginalTitle = NullIfEmpty(originalTitle.Text);
        item.Year = NullableNumber(year.Text);
        item.Season = NullableNumber(season.Text);
        item.Episode = NullableNumber(episode.Text);
        item.CollectionName = NullIfEmpty(collection.Text);
        item.PosterPath = NullIfEmpty(poster.Text);
        item.Overview = NullIfEmpty(overview.Text);
        item.MediaType = type.SelectedIndex == 2 ? "tv" : type.SelectedIndex == 1 ? "movie" : null;
        item.MetadataEdited = true;
        item.MatchStatus = MatchStatus.Matched;
        item.Status = ItemStatus.Ready;
        item.Error = null;
        await Task.Run(() => App.Host.Repository.SaveMetadata(item));
        LoadPage();
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static int? NullableNumber(string value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? Math.Max(0, number) : null;
}
