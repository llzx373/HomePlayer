namespace HomePlayer.Core;

public readonly record struct SeekPreviewPlacement(double Left, double ArrowCenter);

public static class SeekPreviewLayout
{
    public static double TimelineRatio(double pointerX, double trackWidth)
    {
        if (trackWidth <= 0)
            return 0;
        return Math.Clamp(pointerX / trackWidth, 0, 1);
    }

    public static SeekPreviewPlacement Place(double pointerX, double availableWidth, double previewWidth)
    {
        if (availableWidth <= 0 || previewWidth <= 0)
            return new SeekPreviewPlacement(0, 0);
        var pointer = Math.Clamp(pointerX, 0, availableWidth);
        var left = Math.Clamp(pointer - previewWidth / 2, 0, Math.Max(0, availableWidth - previewWidth));
        var arrow = Math.Clamp(pointer - left, Math.Min(8, previewWidth / 2), Math.Max(8, previewWidth - 8));
        return new SeekPreviewPlacement(left, arrow);
    }
}
