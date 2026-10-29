using System.Runtime.InteropServices.WindowsRuntime;
using HomePlayer.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace HomePlayer;

public sealed partial class PlayerPage
{
    private Canvas? _bitmapSubtitleHost;
    private (TimeSpan Start, TimeSpan End)? _bitmapCue;

    private void UpdateBitmapSubtitle(SubtitleCue? cue)
    {
        if (cue?.Bitmaps.Count > 0)
        {
            if (_bitmapSubtitleHost == null)
            {
                _bitmapSubtitleHost = new Canvas();
                SubtitleLayer.Children.Insert(0, _bitmapSubtitleHost);
            }
            if (_bitmapCue != (cue.Start, cue.End))
            {
                _bitmapCue = (cue.Start, cue.End);
                _bitmapSubtitleHost.Children.Clear();
                foreach (var bitmap in cue.Bitmaps)
                {
                    var image = new Image
                    {
                        Width = bitmap.Width,
                        Height = bitmap.Height,
                        Source = CreateBitmap(bitmap)
                    };
                    Canvas.SetLeft(image, bitmap.X);
                    Canvas.SetTop(image, bitmap.Y);
                    _bitmapSubtitleHost.Children.Add(image);
                }
            }
            LayoutBitmapHost();
        }
        else
        {
            _bitmapCue = null;
            _bitmapSubtitleHost?.Children.Clear();
            if (_bitmapSubtitleHost != null)
                _bitmapSubtitleHost.Visibility = Visibility.Collapsed;
        }
    }

    private void LayoutBitmapHost()
    {
        if (_bitmapSubtitleHost == null || Playback.VideoWidth <= 0 || Playback.VideoHeight <= 0)
            return;
        var scale = Math.Min(VideoHost.ActualWidth / Playback.VideoWidth, VideoHost.ActualHeight / Playback.VideoHeight);
        if (scale <= 0)
            return;
        _bitmapSubtitleHost.Visibility = Visibility.Visible;
        _bitmapSubtitleHost.Width = Playback.VideoWidth;
        _bitmapSubtitleHost.Height = Playback.VideoHeight;
        _bitmapSubtitleHost.RenderTransform = new Microsoft.UI.Xaml.Media.ScaleTransform { ScaleX = scale, ScaleY = scale };
        _bitmapSubtitleHost.RenderTransformOrigin = new Windows.Foundation.Point(0, 0);
        _bitmapSubtitleHost.Margin = new Thickness(
            (VideoHost.ActualWidth - Playback.VideoWidth * scale) / 2,
            (VideoHost.ActualHeight - Playback.VideoHeight * scale) / 2,
            0,
            0);
    }

    private static WriteableBitmap CreateBitmap(SubtitleBitmap bitmap)
    {
        var image = new WriteableBitmap(bitmap.Width, bitmap.Height);
        bitmap.Bgra.CopyTo(image.PixelBuffer);
        image.Invalidate();
        return image;
    }

    private void ApplyAssStyle(string? assEvent)
    {
        var style = AssSubtitleStyle.Parse(assEvent);
        if (style == null)
            return;
        if (!string.IsNullOrWhiteSpace(style.FontName))
            SubtitleText.FontFamily = new Microsoft.UI.Xaml.Media.FontFamily(style.FontName);
        if (style.FontSize is double fontSize)
            SubtitleText.FontSize = Math.Clamp(fontSize * VideoHost.ActualHeight / 1080.0, 8, 160);
        if (style.BgrColor is uint bgr)
            SubtitleText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(
                255,
                (byte)(bgr & 0xff),
                (byte)((bgr >> 8) & 0xff),
                (byte)((bgr >> 16) & 0xff)));
        SubtitleText.FontWeight = style.Bold ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal;
        SubtitleText.FontStyle = style.Italic ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal;
        if (style.Alignment is int alignment)
        {
            SubtitlePill.HorizontalAlignment = alignment % 3 == 1 ? HorizontalAlignment.Left : alignment % 3 == 0 ? HorizontalAlignment.Right : HorizontalAlignment.Center;
            SubtitlePill.VerticalAlignment = alignment >= 7 ? VerticalAlignment.Top : alignment <= 3 ? VerticalAlignment.Bottom : VerticalAlignment.Center;
        }
        if (style.X is double x && style.Y is double y)
        {
            var scale = Math.Min(VideoHost.ActualWidth / 1920.0, VideoHost.ActualHeight / 1080.0);
            SubtitlePill.HorizontalAlignment = HorizontalAlignment.Left;
            SubtitlePill.VerticalAlignment = VerticalAlignment.Top;
            SubtitlePill.Margin = new Thickness(x * scale, y * scale, 0, 0);
        }
    }

    private async Task<BgraOverlay?> CaptureSubtitleOverlayAsync()
    {
        if (!App.Host.Settings.SnapshotIncludeSubtitles || SubtitleLayer.Visibility != Visibility.Visible)
            return null;
        var width = (int)Math.Ceiling(VideoHost.ActualWidth);
        var height = (int)Math.Ceiling(VideoHost.ActualHeight);
        if (width <= 0 || height <= 0)
            return null;
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(SubtitleLayer);
        var buffer = await bitmap.GetPixelsAsync();
        var pixels = buffer.ToArray();
        return new BgraOverlay(width, height, pixels);
    }
}
