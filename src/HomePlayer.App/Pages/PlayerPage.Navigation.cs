using System.Runtime.InteropServices.WindowsRuntime;
using HomePlayer.Core;
using HomePlayer.Ffmpeg;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace HomePlayer;

public sealed partial class PlayerPage
{
    private readonly SeekPreviewService _seekPreviewService = new();
    private readonly DispatcherQueueTimer _seekPreviewTimer;
    private CancellationTokenSource? _seekPreviewCancellation;
    private string? _seekPreviewMedia;
    private TimeSpan _seekPreviewTarget;
    private double _seekPreviewPointerX;
    private long _seekPreviewRequest;
    private bool _seekPreviewHovered;

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

    private void Position_PointerExited(object sender, PointerRoutedEventArgs e) => HideSeekPreview();

    private void UpdateSeekPreview(PointerRoutedEventArgs e)
    {
        SyncSeekPreviewMedia();
        if (Playback.CurrentPath == null || Playback.Duration <= TimeSpan.Zero || PositionBar.ActualWidth <= 0)
        {
            HideSeekPreview();
            return;
        }

        var point = e.GetCurrentPoint(PositionBar).Position;
        _seekPreviewPointerX = Math.Clamp(point.X, 0, PositionBar.ActualWidth);
        var ratio = SeekPreviewLayout.TimelineRatio(_seekPreviewPointerX, PositionBar.ActualWidth);
        _seekPreviewTarget = TimeSpan.FromTicks((long)(Playback.Duration.Ticks * ratio));
        _seekPreviewHovered = true;
        _seekPreviewRequest++;
        _seekPreviewCancellation?.Cancel();
        _seekPreviewCancellation?.Dispose();
        _seekPreviewCancellation = new CancellationTokenSource();
        SeekPreviewImage.Source = null;
        SeekPreviewStatus.Text = "正在获取…";
        SeekPreviewStatusHost.Visibility = Visibility.Visible;
        SeekPreviewTime.Text = ClockText.Format(_seekPreviewTarget);
        SeekPreview.Visibility = Visibility.Visible;
        RepositionSeekPreview();
        _seekPreviewTimer.Stop();
        _seekPreviewTimer.Start();
    }

    private async void SeekPreviewTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        _seekPreviewTimer.Stop();
        var cancellation = _seekPreviewCancellation;
        var path = _seekPreviewMedia;
        if (!_seekPreviewHovered || cancellation == null || path == null)
            return;
        var request = _seekPreviewRequest;
        var target = _seekPreviewTarget;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            var result = await _seekPreviewService.CaptureAsync(path, target, timeout.Token);
            if (!IsSeekPreviewCurrent(request, path, target))
                return;
            if (result == null)
            {
                ShowSeekPreviewUnavailable();
                return;
            }

            SeekPreviewImage.Source = CreateSeekPreviewBitmap(result.Frame);
            SeekPreviewStatusHost.Visibility = Visibility.Collapsed;
        }
        catch (OperationCanceledException)
        {
            if (!cancellation.IsCancellationRequested && IsSeekPreviewCurrent(request, path, target))
                ShowSeekPreviewUnavailable();
        }
        catch
        {
            if (IsSeekPreviewCurrent(request, path, target))
                ShowSeekPreviewUnavailable();
        }
    }

    private void SyncSeekPreviewMedia()
    {
        var path = Playback.CurrentPath;
        if (string.Equals(path, _seekPreviewMedia, StringComparison.OrdinalIgnoreCase))
            return;
        _seekPreviewMedia = path;
        HideSeekPreview();
    }

    private bool IsSeekPreviewCurrent(long request, string path, TimeSpan target) =>
        _seekPreviewHovered
        && _seekPreviewRequest == request
        && _seekPreviewTarget == target
        && string.Equals(_seekPreviewMedia, path, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Playback.CurrentPath, path, StringComparison.OrdinalIgnoreCase);

    private void ShowSeekPreviewUnavailable()
    {
        SeekPreviewImage.Source = null;
        SeekPreviewStatus.Text = "暂无预览";
        SeekPreviewStatusHost.Visibility = Visibility.Visible;
    }

    private void HideSeekPreview()
    {
        _seekPreviewHovered = false;
        _seekPreviewRequest++;
        _seekPreviewTimer.Stop();
        _seekPreviewCancellation?.Cancel();
        _seekPreviewCancellation?.Dispose();
        _seekPreviewCancellation = null;
        SeekPreviewImage.Source = null;
        SeekPreview.Visibility = Visibility.Collapsed;
    }

    private void RepositionSeekPreview()
    {
        if (SeekPreview.Visibility != Visibility.Visible || PositionBar.ActualWidth <= 0)
            return;
        var placement = SeekPreviewLayout.Place(_seekPreviewPointerX, PositionBar.ActualWidth, SeekPreview.Width);
        if (SeekPreview.RenderTransform is not TranslateTransform transform)
        {
            transform = new TranslateTransform();
            SeekPreview.RenderTransform = transform;
        }
        transform.X = placement.Left;
        transform.Y = -(Math.Max(0, SeekPreview.ActualHeight) + 8);
        if (SeekPreviewArrow.RenderTransform is not TranslateTransform arrow)
        {
            arrow = new TranslateTransform();
            SeekPreviewArrow.RenderTransform = arrow;
        }
        arrow.X = placement.ArrowCenter - 13;
    }

    private static WriteableBitmap CreateSeekPreviewBitmap(VideoFrame frame)
    {
        var image = new WriteableBitmap(frame.Width, frame.Height);
        var pixels = new byte[frame.Width * frame.Height * 4];
        for (var y = 0; y < frame.Height; y++)
            Buffer.BlockCopy(frame.Data, y * frame.Stride, pixels, y * frame.Width * 4, frame.Width * 4);
        pixels.CopyTo(image.PixelBuffer);
        image.Invalidate();
        return image;
    }
}
