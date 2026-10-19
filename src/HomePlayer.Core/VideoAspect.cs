namespace HomePlayer.Core;

public readonly record struct VideoQuad(
    float Left,
    float Right,
    float Top,
    float Bottom,
    float UvLeft,
    float UvRight,
    float UvTop,
    float UvBottom);

public static class VideoAspect
{
    public static VideoQuad Quad(int stageW, int stageH, int frameW, int frameH, AspectMode mode, bool bottomUp)
    {
        if (stageW < 1 || stageH < 1 || frameW < 1 || frameH < 1)
            return Stretch(bottomUp);

        return mode switch
        {
            AspectMode.Fill => Fill(stageW, stageH, frameW, frameH, bottomUp),
            AspectMode.Stretch => Stretch(bottomUp),
            AspectMode.Ratio16x9 => Fit(stageW, stageH, 16, 9, bottomUp),
            AspectMode.Ratio4x3 => Fit(stageW, stageH, 4, 3, bottomUp),
            _ => Fit(stageW, stageH, frameW, frameH, bottomUp)
        };
    }

    public static AspectMode Next(AspectMode mode) => mode switch
    {
        AspectMode.Fit => AspectMode.Fill,
        AspectMode.Fill => AspectMode.Stretch,
        AspectMode.Stretch => AspectMode.Ratio16x9,
        AspectMode.Ratio16x9 => AspectMode.Ratio4x3,
        _ => AspectMode.Fit
    };

    private static VideoQuad Fit(int stageW, int stageH, double contentW, double contentH, bool bottomUp)
    {
        var scale = Math.Min(stageW / contentW, stageH / contentH);
        if (scale <= 0)
            return Stretch(bottomUp);
        var drawWidth = Math.Clamp(contentW * scale, 1, stageW);
        var drawHeight = Math.Clamp(contentH * scale, 1, stageH);
        var originX = (stageW - drawWidth) / 2;
        var originY = (stageH - drawHeight) / 2;
        var left = (float)(originX / stageW * 2 - 1);
        var right = (float)((originX + drawWidth) / stageW * 2 - 1);
        var top = (float)(1 - originY / stageH * 2);
        var bottom = (float)(1 - (originY + drawHeight) / stageH * 2);
        return new VideoQuad(left, right, top, bottom, 0f, 1f, UvTop(0f, bottomUp), UvBottom(1f, bottomUp));
    }

    private static VideoQuad Fill(int stageW, int stageH, int frameW, int frameH, bool bottomUp)
    {
        var scale = Math.Max(stageW / (double)frameW, stageH / (double)frameH);
        if (scale <= 0)
            return Stretch(bottomUp);
        var visibleW = Math.Min(frameW, stageW / scale);
        var visibleH = Math.Min(frameH, stageH / scale);
        var uvLeft = (float)Math.Clamp((frameW - visibleW) / (2.0 * frameW), 0, 0.5);
        var uvRight = 1f - uvLeft;
        var cropV = (float)Math.Clamp((frameH - visibleH) / (2.0 * frameH), 0, 0.5);
        return new VideoQuad(-1f, 1f, 1f, -1f, uvLeft, uvRight, UvTop(cropV, bottomUp), UvBottom(1f - cropV, bottomUp));
    }

    private static VideoQuad Stretch(bool bottomUp) =>
        new(-1f, 1f, 1f, -1f, 0f, 1f, UvTop(0f, bottomUp), UvBottom(1f, bottomUp));

    private static float UvTop(float value, bool bottomUp) => bottomUp ? 1f - value : value;

    private static float UvBottom(float value, bool bottomUp) => bottomUp ? 1f - value : value;
}
