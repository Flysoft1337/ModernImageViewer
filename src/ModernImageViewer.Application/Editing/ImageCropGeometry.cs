using ModernImageViewer.Imaging;

namespace ModernImageViewer.Application.Editing;

/// <summary>Crop tools operate in displayed axes, returning EXIF-corrected source coordinates.</summary>
public static class ImageCropGeometry
{
    public static PixelRect Fit(PixelRect crop, ViewOrientation orientation, PixelSize ratio)
    {
        PixelSize displayed = orientation.GetDisplaySize(crop.Size);
        PixelSize fitted = FitSize(displayed, ratio);
        PixelSize source = orientation.GetDisplaySize(fitted);
        return new(crop.X + ((crop.Width - source.Width) / 2), crop.Y + ((crop.Height - source.Height) / 2),
            source.Width, source.Height);
    }

    public static PixelRect? Drag(PixelSize source, ViewOrientation orientation,
        (double X, double Y) start, (double X, double Y) end, PixelSize? ratio = null)
    {
        PixelSize displayed = orientation.GetDisplaySize(source);
        var first = orientation.ToDisplayPoint(source, start.X, start.Y);
        var last = orientation.ToDisplayPoint(source, end.X, end.Y);
        first = (Math.Clamp(first.X, 0, displayed.Width), Math.Clamp(first.Y, 0, displayed.Height));
        last = (Math.Clamp(last.X, 0, displayed.Width), Math.Clamp(last.Y, 0, displayed.Height));
        if (Math.Abs(first.X - last.X) < .001 || Math.Abs(first.Y - last.Y) < .001) { return null; }
        int left;
        int top;
        PixelSize size;
        if (ratio is { } aspect)
        {
            int anchorX = (int)Math.Round(first.X);
            int anchorY = (int)Math.Round(first.Y);
            int width = Math.Abs((int)Math.Round(last.X) - anchorX);
            int height = Math.Abs((int)Math.Round(last.Y) - anchorY);
            if (width == 0 || height == 0) { return null; }
            size = FitSize(new(width, height), aspect);
            left = last.X > first.X ? anchorX : anchorX - size.Width;
            top = last.Y > first.Y ? anchorY : anchorY - size.Height;
        }
        else
        {
            left = (int)Math.Floor(Math.Min(first.X, last.X));
            top = (int)Math.Floor(Math.Min(first.Y, last.Y));
            size = new((int)Math.Ceiling(Math.Max(first.X, last.X)) - left,
                (int)Math.Ceiling(Math.Max(first.Y, last.Y)) - top);
        }
        var a = orientation.ToSourcePoint(source, left, top);
        var b = orientation.ToSourcePoint(source, left + size.Width, top + size.Height);
        return new((int)Math.Min(a.X, b.X), (int)Math.Min(a.Y, b.Y),
            (int)Math.Abs(a.X - b.X), (int)Math.Abs(a.Y - b.Y));
    }

    private static PixelSize FitSize(PixelSize bounds, PixelSize ratio)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ratio.Width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ratio.Height);
        double scale = Math.Min((double)bounds.Width / ratio.Width, (double)bounds.Height / ratio.Height);
        // Integer pixels may round the ideal ratio by less than one pixel per axis.
        return new(Math.Clamp((int)Math.Floor((ratio.Width * scale) + 1e-9), 1, bounds.Width),
            Math.Clamp((int)Math.Floor((ratio.Height * scale) + 1e-9), 1, bounds.Height));
    }
}
