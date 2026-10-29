using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI.ViewManagement;

namespace HomePlayer;

public sealed class OverlayFade(FrameworkElement element)
{
    private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(120);
    private Storyboard? _storyboard;

    public void Set(bool open)
    {
        var from = element.Opacity;
        _storyboard?.Stop();
        _storyboard = null;
        element.Opacity = from;
        element.IsHitTestVisible = open;
        if (!new UISettings().AnimationsEnabled)
        {
            element.Opacity = open ? 1 : 0;
            element.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            return;
        }

        element.Visibility = Visibility.Visible;
        var animation = new DoubleAnimationUsingKeyFrames();
        animation.KeyFrames.Add(new SplineDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(Duration),
            Value = open ? 1 : 0,
            KeySpline = new KeySpline
            {
                ControlPoint1 = new Windows.Foundation.Point(0.23, 1),
                ControlPoint2 = new Windows.Foundation.Point(0.32, 1)
            }
        });
        Storyboard.SetTarget(animation, element);
        Storyboard.SetTargetProperty(animation, "Opacity");
        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        storyboard.Completed += (_, _) =>
        {
            if (!ReferenceEquals(_storyboard, storyboard))
                return;
            _storyboard = null;
            element.Opacity = open ? 1 : 0;
            if (!open)
                element.Visibility = Visibility.Collapsed;
        };
        _storyboard = storyboard;
        storyboard.Begin();
    }
}
