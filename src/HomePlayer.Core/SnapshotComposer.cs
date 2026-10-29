namespace HomePlayer.Core;

public sealed record BgraOverlay(int Width, int Height, byte[] Pixels);

public static class SnapshotComposer
{
    public static void Apply(byte[] frame, int width, int height, BgraOverlay? overlay)
    {
        if (overlay == null || overlay.Width <= 0 || overlay.Height <= 0)
            return;
        for (var y = 0; y < height; y++)
        {
            var sourceY = Math.Min(overlay.Height - 1, y * overlay.Height / height);
            for (var x = 0; x < width; x++)
            {
                var sourceX = Math.Min(overlay.Width - 1, x * overlay.Width / width);
                var source = (sourceY * overlay.Width + sourceX) * 4;
                var alpha = overlay.Pixels[source + 3];
                if (alpha == 0)
                    continue;
                var destination = (y * width + x) * 4;
                if (alpha == 255)
                {
                    frame[destination] = overlay.Pixels[source];
                    frame[destination + 1] = overlay.Pixels[source + 1];
                    frame[destination + 2] = overlay.Pixels[source + 2];
                    continue;
                }
                for (var channel = 0; channel < 3; channel++)
                {
                    var top = overlay.Pixels[source + channel];
                    var bottom = frame[destination + channel];
                    frame[destination + channel] = (byte)Math.Clamp(top + bottom * (255 - alpha) / 255, 0, 255);
                }
            }
        }
    }
}
