using HomePlayer.Core;
using HomePlayer.Library;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace HomePlayer;

public sealed partial class LibraryPage
{
    private async Task ShowSeriesAsync(MediaSeriesGroup group)
    {
        var seasons = group.Seasons.Select(season => new SeasonChoice(season, SeasonLabel(season))).ToList();
        var seasonBox = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = seasons, DisplayMemberPath = nameof(SeasonChoice.Label) };
        seasonBox.SelectedItem = seasons.FirstOrDefault(choice => choice.Season == group.NextUnwatched?.Item.Season) ?? seasons.FirstOrDefault();
        var episodes = new ListView { Height = 320, SelectionMode = ListViewSelectionMode.Single, DisplayMemberPath = nameof(HomePlayer.SeriesEpisodeRow.Text) };
        var summary = new TextBlock
        {
            Text = group.IsMovieCollection
                ? $"{group.EpisodeCount} 部 · 已看 {group.WatchedCount} 部"
                : $"共 {group.EpisodeCount} 集 · 已看 {group.WatchedCount} 集 · 下一集 {(group.NextUnwatched == null ? "无" : EpisodeLabel(group.NextUnwatched.Item))}",
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gray)
        };
        var autoPlay = new ToggleSwitch
        {
            Header = "播完自动连播",
            IsOn = App.Host.Settings.AutoPlayNextEpisode,
            OnContent = "开启",
            OffContent = "关闭"
        };
        var body = new StackPanel { Width = 580, Spacing = 10 };
        body.Children.Add(summary);
        body.Children.Add(seasonBox);
        body.Children.Add(episodes);
        body.Children.Add(autoPlay);
        var dialog = new ContentDialog
        {
            Title = group.Title,
            Content = body,
            PrimaryButtonText = group.IsMovieCollection ? "播放合集" : "播放下一集未看",
            SecondaryButtonText = "从头播放",
            CloseButtonText = "关闭",
            XamlRoot = XamlRoot
        };
        MediaSeriesEntry? selected = null;
        void FillEpisodes()
        {
            var season = (seasonBox.SelectedItem as SeasonChoice)?.Season;
            episodes.ItemsSource = group.InSeason(season).Select(SeriesEpisodeRow).ToList();
        }
        seasonBox.SelectionChanged += (_, _) => FillEpisodes();
        episodes.DoubleTapped += (_, _) =>
        {
            if (episodes.SelectedItem is not SeriesEpisodeRow row)
                return;
            selected = group.OrderedItems.FirstOrDefault(entry => entry.Item.Id == row.ItemId);
            dialog.Hide();
        };
        FillEpisodes();
        var result = await dialog.ShowAsync();
        App.Host.Settings.AutoPlayNextEpisode = autoPlay.IsOn;
        App.Host.Playback.AutoAdvance = autoPlay.IsOn;
        if (selected != null)
            await PlaySeriesAsync(group, selected);
        else if (result == ContentDialogResult.Primary)
            await PlaySeriesAsync(group, group.NextUnwatched ?? group.CanonicalItems.FirstOrDefault());
        else if (result == ContentDialogResult.Secondary)
            await PlaySeriesAsync(group, group.CanonicalItems.FirstOrDefault(), restart: true);
    }

    private static SeriesEpisodeRow SeriesEpisodeRow(MediaSeriesEntry entry)
    {
        var state = entry.State?.Finished == true ? "已看" : entry.State?.PositionSeconds >= 5 ? "在看" : "未看";
        return new SeriesEpisodeRow(
            entry.Item.Id,
            $"{EpisodeLabel(entry.Item)} · {entry.Item.FileName} · {state}",
            entry.Item.FullPath(entry.Library.RootPath));
    }

    private static string SeasonLabel(int? season) => season switch
    {
        null => "OVA / 合集文件",
        0 => "特别篇",
        _ => $"第 {season} 季"
    };

    private async Task PlaySeriesAsync(MediaSeriesGroup group, MediaSeriesEntry? target, bool restart = false)
    {
        var entries = group.CanonicalItems.ToList();
        if (entries.Count == 0)
            return;
        var index = 0;
        if (target != null)
        {
            index = Math.Max(0, entries.FindIndex(entry => entry.Item.Season == target.Item.Season && entry.Item.Episode == target.Item.Episode));
            entries[index] = target;
        }
        var paths = entries.Select(entry => entry.Item.FullPath(entry.Library.RootPath)).ToList();
        await App.Host.Playback.PlayPathsAsync(paths, index, restart: restart);
        App.Window.SelectPlayer();
    }
}

public sealed record SeasonChoice(int? Season, string Label);
public sealed record SeriesEpisodeRow(string ItemId, string Text, string Path);
