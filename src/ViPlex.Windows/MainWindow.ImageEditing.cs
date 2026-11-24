using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using ViPlex.Core;

namespace ViPlex.Windows;

public partial class MainWindow
{
    private bool editingImage;
    private ImageDrag? imageDrag;

    private void SelectPreviewImage()
    {
        Photo.Source = selectedPage?.Page.Kind == MediaKind.Image ? selectedPage.Thumbnail : null;
        EditImageButton.Visibility = Photo.Source is null ? Visibility.Collapsed : Visibility.Visible;
        SetImageEditing(false);
    }

    private void ToggleImageEditing_Click(object sender, RoutedEventArgs e) => SetImageEditing(!editingImage);

    private void SetImageEditing(bool active)
    {
        editingImage = active && Photo.Source is BitmapSource;
        EditImageButton.Content = editingImage ? "Done editing" : "Edit image";
        ImageSelection.Visibility = ResetImageButton.Visibility = editingImage ? Visibility.Visible : Visibility.Collapsed;

        if (!editingImage && imageDrag is not null)
        {
            imageDrag = null;
            ImageSelection.ReleaseMouseCapture();
        }

        if (Photo.Source is BitmapSource photo && selectedPage is { } page && selectedSet is { } set)
        {
            var fitted = ImageComposition.Fit(photo);
            var placement = set.ImagePlacements.GetValueOrDefault(page.Index, fitted);
            ResetImageButton.IsEnabled = placement != fitted;
            RenderImagePlacement(placement);
        }
    }

    private void ResetImage_Click(object sender, RoutedEventArgs e)
    {
        if (Photo.Source is BitmapSource photo)
        {
            SetImagePlacement(ImageComposition.Fit(photo));
        }
    }

    private void SetImagePlacement(Rect placement)
    {
        if (Photo.Source is not BitmapSource photo || selectedPage is not { } page || selectedSet is not { } set)
        {
            return;
        }

        var fitted = ImageComposition.Fit(photo);
        if (placement == set.ImagePlacements.GetValueOrDefault(page.Index, fitted))
        {
            return;
        }

        if (placement == fitted)
        {
            set.ImagePlacements.Remove(page.Index);
        }
        else
        {
            set.ImagePlacements[page.Index] = placement;
        }

        RefreshPages(page.Index);
    }

    private void RenderImagePlacement(Rect placement)
    {
        Canvas.SetLeft(Photo, placement.X);
        Canvas.SetTop(Photo, placement.Y);
        Photo.Width = placement.Width;
        Photo.Height = placement.Height;
    }

    private Rect PreviewImagePlacement() => new(Canvas.GetLeft(Photo), Canvas.GetTop(Photo), Photo.Width, Photo.Height);

    private void Stage_LayoutUpdated(object? sender, EventArgs e)
    {
        if (!editingImage || !Stage.IsVisible)
        {
            return;
        }

        // The photo clips to the screen, but its handles can follow it into the surrounding preview.
        var bounds = Stage.TransformToVisual(ImageOverlay).TransformBounds(PreviewImagePlacement());
        var outline = Rect.Intersect(bounds, new Rect(ImageOverlay.RenderSize));
        if (outline.IsEmpty)
        {
            return;
        }

        Canvas.SetLeft(ImageSelection, outline.X);
        Canvas.SetTop(ImageSelection, outline.Y);
        ImageSelection.Width = outline.Width;
        ImageSelection.Height = outline.Height;
    }

    private void ImageSelection_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!editingImage || !ImageSelection.CaptureMouse())
        {
            return;
        }

        var handle = e.OriginalSource is FrameworkElement { Tag: string tag } && Enum.TryParse<ImageHandle>(tag, out var corner)
            ? corner : ImageHandle.Move;
        imageDrag = new ImageDrag(handle, e.GetPosition(Stage), PreviewImagePlacement());
        e.Handled = true;
    }

    private void ImageSelection_MouseMove(object sender, MouseEventArgs e)
    {
        if (imageDrag is { } drag)
        {
            RenderImagePlacement(drag.PlaceAt(e.GetPosition(Stage)));
            e.Handled = true;
        }
    }

    private void ImageSelection_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (imageDrag is not { } drag)
        {
            return;
        }

        var placement = drag.PlaceAt(e.GetPosition(Stage));
        imageDrag = null;
        ImageSelection.ReleaseMouseCapture();
        RenderImagePlacement(placement);
        SetImagePlacement(placement);
        e.Handled = true;
    }

    private void ImageSelection_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (imageDrag is { } drag)
        {
            imageDrag = null;
            RenderImagePlacement(drag.Bounds);
        }
    }

    private static Dictionary<int, MediaReplacement> CreateExportReplacements(NovaPackage source, string work,
        IReadOnlyDictionary<int, MediaReplacement> changes, IReadOnlyDictionary<int, Rect> placements)
    {
        var export = new Dictionary<int, MediaReplacement>(changes);
        foreach (var (index, placement) in placements)
        {
            var page = source.Pages[index];
            changes.TryGetValue(index, out var replacement);
            var path = replacement?.SourcePath ?? page.MediaPath;
            var name = replacement is null ? page.MediaName : Path.GetFileName(path);
            var directory = Path.Combine(work, "edited-images", index.ToString(CultureInfo.InvariantCulture));
            var photo = LoadPreview(path, width: 0, height: 0);
            export[index] = ImageComposition.Save(photo, placement, directory, name);
        }

        return export;
    }

    [Flags]
    private enum ImageHandle
    {
        Move = 0,
        Left = 1,
        Top = 2,
        Right = 4,
        Bottom = 8,
        TopLeft = Top | Left,
        TopRight = Top | Right,
        BottomLeft = Bottom | Left,
        BottomRight = Bottom | Right
    }

    private sealed record ImageDrag(ImageHandle Handle, Point Start, Rect Bounds)
    {
        private const double MinimumVisiblePixels = 24;

        public Rect PlaceAt(Point pointer)
        {
            var dx = Math.Round(pointer.X - Start.X);
            var dy = Math.Round(pointer.Y - Start.Y);
            if (dx == 0 && dy == 0)
            {
                return Bounds;
            }

            var minWidth = Math.Min(MinimumVisiblePixels, Bounds.Width);
            var minHeight = Math.Min(MinimumVisiblePixels, Bounds.Height);
            if (Handle == ImageHandle.Move)
            {
                return new Rect(
                    Math.Clamp(Bounds.X + dx, minWidth - Bounds.Width, ImageComposition.Width - minWidth),
                    Math.Clamp(Bounds.Y + dy, minHeight - Bounds.Height, ImageComposition.Height - minHeight),
                    Bounds.Width, Bounds.Height);
            }

            var left = Handle.HasFlag(ImageHandle.Left)
                ? Math.Min(Bounds.Left + dx, Math.Min(Bounds.Right, ImageComposition.Width) - minWidth)
                : Bounds.Left;
            var top = Handle.HasFlag(ImageHandle.Top)
                ? Math.Min(Bounds.Top + dy, Math.Min(Bounds.Bottom, ImageComposition.Height) - minHeight)
                : Bounds.Top;
            var right = Handle.HasFlag(ImageHandle.Right)
                ? Math.Max(Bounds.Right + dx, Math.Max(Bounds.Left, 0) + minWidth)
                : Bounds.Right;
            var bottom = Handle.HasFlag(ImageHandle.Bottom)
                ? Math.Max(Bounds.Bottom + dy, Math.Max(Bounds.Top, 0) + minHeight)
                : Bounds.Bottom;

            return new Rect(left, top, right - left, bottom - top);
        }
    }
}
