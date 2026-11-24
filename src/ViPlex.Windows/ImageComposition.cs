using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ViPlex.Core;

namespace ViPlex.Windows;

internal static class ImageComposition
{
    internal const int Width = 256;
    internal const int Height = 768;

    internal static Rect Fit(BitmapSource source)
    {
        var scale = Math.Min((double)Width / source.PixelWidth, (double)Height / source.PixelHeight);
        var width = source.PixelWidth * scale;
        var height = source.PixelHeight * scale;
        return new Rect((Width - width) / 2, (Height - height) / 2, width, height);
    }

    internal static MediaReplacement Save(BitmapSource source, Rect placement, string directory, string originalFileName)
    {
        var screen = new Rect(0, 0, Width, Height);
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (var drawing = visual.RenderOpen())
        {
            drawing.PushClip(new RectangleGeometry(screen));
            drawing.DrawRectangle(Brushes.Black, null, screen);
            drawing.DrawImage(source, placement);
            drawing.Pop();
        }

        var bitmap = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var frame = BitmapFrame.Create(bitmap);

        Directory.CreateDirectory(directory);
        var imagePath = Path.Combine(directory, GetImageFileName(originalFileName, ".png"));
        var thumbnailPath = Path.Combine(directory, "thumbnail.jpg");
        Write(frame, new PngBitmapEncoder(), imagePath);
        Write(frame, new JpegBitmapEncoder { QualityLevel = 90 }, thumbnailPath);
        return new MediaReplacement(imagePath, thumbnailPath);
    }

    private static void Write(BitmapFrame frame, BitmapEncoder encoder, string path)
    {
        encoder.Frames.Add(frame);
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    internal static string GetImageFileName(string originalFileName, string extension)
    {
        var stem = Path.GetFileNameWithoutExtension(originalFileName).TrimEnd(' ', '.');
        var safeStem = stem.Length == 0 || stem.Length + extension.Length > 255 ||
            stem.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || IsDeviceName(stem) ? "image" : stem;
        return safeStem + extension;
    }

    private static bool IsDeviceName(string stem)
    {
        var prefix = stem.Split('.', 2)[0].TrimEnd(' ').ToUpperInvariant();
        return prefix is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
            prefix.Length == 4 && (prefix.StartsWith("COM", StringComparison.Ordinal) ||
                                   prefix.StartsWith("LPT", StringComparison.Ordinal)) && "123456789¹²³".Contains(prefix[3]);
    }
}
