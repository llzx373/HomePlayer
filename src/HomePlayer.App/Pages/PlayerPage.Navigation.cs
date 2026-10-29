using HomePlayer.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;

namespace HomePlayer;

public sealed partial class PlayerPage
{
    private string? _seekPreviewImagePath;

    private void SeekBackLarge_Click(object sender, RoutedEventArgs e) =>
        Playback.Seek(Playback.Position - TimeSpan.FromSeconds(App.Host.Settings.LargeSeekStepSeconds));

    private void SeekForwardLarge_Click(object sender, RoutedEventArgs e) =>
        Playback.Seek(Playback.Position + TimeSpan.FromSeconds(App.Host.Settings.LargeSeekStepSeconds));

    private void SetLoopA_Click(object sender, RoutedEventArgs e) => Playback.SetLoopA();

    private void SetLoopB_Click(object sender, RoutedEventArgs e) => Playback.SetLoopB();

    private void ClearLoop_Click(object sender, RoutedEventArgs e) => Playback.ClearLoop();

    private void MoreMenu_Opening(object sender, object e)
    {
        ChaptersMenu_Opening(sender, e);
        BookmarksMenu_Opening(sender, e);
    }

    private void ChaptersMenu_Opening(object sender, object e)
    {
        ChaptersMenu.Items.Clear();
        var chapters = Playback.GetChapters();
        if (chapters.Count == 0)
        {
            ChaptersMenu.Items.Add(new MenuFlyoutItem { Text = "当前媒体没有章节", IsEnabled = false });
            return;
        }
        foreach (var chapter in chapters)
        {
            var item = new MenuFlyoutItem { Text = $"{ClockText.Format(chapter.Start)}  {chapter.Title}" };
            item.Click += (_, _) => Playback.Seek(chapter.Start);
            ChaptersMenu.Items.Add(item);
        }
    }

    private void BookmarksMenu_Opening(object sender, object e)
    {
        BookmarksMenu.Items.Clear();
        var add = new MenuFlyoutItem { Text = "添加当前位置书签", IsEnabled = Playback.CurrentPath != null && Playback.IsOpen };
        add.Click += AddBookmark_Click;
        BookmarksMenu.Items.Add(add);
        var path = Playback.CurrentPath;
        if (path == null)
            return;
        var bookmarks = App.Host.Repository.ListBookmarks(path);
        if (bookmarks.Count > 0)
            BookmarksMenu.Items.Add(new MenuFlyoutSeparator());
        foreach (var bookmark in bookmarks)
        {
            var menu = new MenuFlyoutSubItem { Text = string.IsNullOrWhiteSpace(bookmark.Title) ? ClockText.FormatSeconds(bookmark.PositionSeconds) : bookmark.Title };
            var jump = new MenuFlyoutItem { Text = "跳转到此位置" };
            jump.Click += (_, _) => Playback.Seek(TimeSpan.FromSeconds(bookmark.PositionSeconds));
            var delete = new MenuFlyoutItem { Text = "删除书签" };
            delete.Click += (_, _) => App.Host.Repository.DeleteBookmark(bookmark.Path, bookmark.PositionSeconds);
            menu.Items.Add(jump);
            menu.Items.Add(delete);
            BookmarksMenu.Items.Add(menu);
        }
    }

    private void AddBookmark_Click(object sender, RoutedEventArgs e)
    {
        if (Playback.CurrentPath is not string path || !Playback.IsOpen)
            return;
        var position = Playback.Position;
        App.Host.Repository.SaveBookmark(new MediaBookmark(
            path,
            position.TotalSeconds,
            $"{ClockText.Format(position)}  {Path.GetFileNameWithoutExtension(path)}",
            DateTime.UtcNow.Ticks));
    }

    private void Position_PointerEntered(object sender, PointerRoutedEventArgs e) => UpdateSeekPreview(e);

    private void Position_PointerMoved(object sender, PointerRoutedEventArgs e) => UpdateSeekPreview(e);

    private void Position_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        SeekPreview.Visibility = Visibility.Collapsed;
    }

    private void UpdateSeekPreview(PointerRoutedEventArgs e)
    {
        if (Playback.Duration <= TimeSpan.Zero || PositionBar.ActualWidth <= 0)
        {
            SeekPreview.Visibility = Visibility.Collapsed;
            return;
        }
        var point = e.GetCurrentPoint(PositionBar).Position;
        var ratio = Math.Clamp(point.X / PositionBar.ActualWidth, 0, 1);
        var target = TimeSpan.FromTicks((long)(Playback.Duration.Ticks * ratio));
        SeekPreviewTime.Text = ClockText.Format(target);
        SeekPreview.Visibility = Visibility.Visible;
        string? imagePath = null;
        if (Playback.CurrentPath is string current)
        {
            var item = App.Host.Repository.FindItemByPath(current);
            if (item?.FramePaths.Length > 0)
                imagePath = item.FramePaths[Math.Min(item.FramePaths.Length - 1, (int)(ratio * item.FramePaths.Length))];
        }
        if (!string.Equals(imagePath, _seekPreviewImagePath, StringComparison.OrdinalIgnoreCase))
        {
            _seekPreviewImagePath = imagePath;
            SeekPreviewImage.Source = imagePath == null ? null : new BitmapImage(new Uri(imagePath));
        }
    }
}
