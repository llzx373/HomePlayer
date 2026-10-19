namespace HomePlayer.Core;

public static class WindowFit
{
    public readonly record struct Size(int Width, int Height);

    public static Size? ForVideo(
        int videoWidth,
        int videoHeight,
        int areaWidth,
        int areaHeight,
        int extraWidth,
        int extraHeight,
        int minWidth,
        int minHeight,
        int maxWidth,
        int maxHeight)
    {
        if (videoWidth < 1 || videoHeight < 1 || areaWidth < 1 || areaHeight < 1)
            return null;
        if (minWidth < 1 || minHeight < 1 || maxWidth < minWidth || maxHeight < minHeight)
            return null;

        extraWidth = Math.Max(0, extraWidth);
        extraHeight = Math.Max(0, extraHeight);
        var aspect = videoWidth / (double)videoHeight;
        var fittedWidth = Math.Max(1, (int)Math.Round(areaHeight * aspect));
        var fittedHeight = Math.Max(1, (int)Math.Round(areaWidth / aspect));
        if (Math.Abs(areaWidth - fittedWidth) <= 1 || Math.Abs(areaHeight - fittedHeight) <= 1)
            return null;

        var newAreaWidth = areaWidth;
        var newAreaHeight = areaHeight;
        if (areaWidth > fittedWidth)
            newAreaWidth = fittedWidth;
        else
            newAreaHeight = fittedHeight;

        var width = Math.Clamp(newAreaWidth + extraWidth, minWidth, maxWidth);
        var height = Math.Clamp(newAreaHeight + extraHeight, minHeight, maxHeight);
        var currentWidth = areaWidth + extraWidth;
        var currentHeight = areaHeight + extraHeight;
        if (width == currentWidth && height == currentHeight)
            return null;
        return new Size(width, height);
    }
}
