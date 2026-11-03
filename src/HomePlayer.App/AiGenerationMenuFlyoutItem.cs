using Microsoft.UI.Xaml.Controls;

namespace HomePlayer;

public sealed class AiGenerationMenuFlyoutItem : MenuFlyoutItem
{
    private ProgressRing? _ring;

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _ring = GetTemplateChild("GenerationRing") as ProgressRing;
    }

    public void StopProgress()
    {
        if (_ring != null)
            _ring.IsActive = false;
    }
}
