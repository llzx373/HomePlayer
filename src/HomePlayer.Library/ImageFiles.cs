using System.Drawing;
using System.Drawing.Imaging;
using HomePlayer.Core;

namespace HomePlayer.Library;

public static class ImageFiles
{
    public static string? SaveJpeg(VideoFrame? frame, string path)
    {
        if (frame == null || frame.Width <= 0 || frame.Height <= 0 || frame.Data.Length == 0)
            return null;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var bitmap = new Bitmap(frame.Width, frame.Height, PixelFormat.Format32bppArgb);
        var locked = bitmap.LockBits(new Rectangle(0, 0, frame.Width, frame.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            var rowBytes = Math.Min(frame.Stride, locked.Stride);
            for (var y = 0; y < frame.Height; y++)
            {
                var sourceY = frame.BottomUp ? frame.Height - 1 - y : y;
                System.Runtime.InteropServices.Marshal.Copy(frame.Data, sourceY * frame.Stride, locked.Scan0 + y * locked.Stride, rowBytes);
            }
        }
        finally
        {
            bitmap.UnlockBits(locked);
        }

        var encoder = ImageCodecInfo.GetImageEncoders().First(item => item.FormatID == ImageFormat.Jpeg.Guid);
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(Encoder.Quality, 80L);
        bitmap.Save(path, encoder, parameters);
        return path;
    }

    public static string? ComposeSheet(IReadOnlyList<string?> frames, string path)
    {
        var existing = frames.Where(file => file != null && File.Exists(file)).Cast<string>().ToList();
        if (existing.Count == 0)
            return null;
        using var first = new Bitmap(existing[0]);
        const int gap = 6;
        var cellW = first.Width;
        var cellH = first.Height;
        using var sheet = new Bitmap(cellW * 3 + gap * 2, cellH * 3 + gap * 2);
        using var graphics = Graphics.FromImage(sheet);
        graphics.Clear(Color.FromArgb(255, 20, 24, 28));
        for (var i = 0; i < 9; i++)
        {
            var file = i < frames.Count ? frames[i] : null;
            if (file == null || !File.Exists(file))
                continue;
            using var image = new Bitmap(file);
            var x = (i % 3) * (cellW + gap);
            var y = (i / 3) * (cellH + gap);
            graphics.DrawImage(image, new Rectangle(x, y, cellW, cellH));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        sheet.Save(path, ImageFormat.Jpeg);
        return path;
    }
}
