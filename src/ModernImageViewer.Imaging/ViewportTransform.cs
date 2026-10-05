namespace ModernImageViewer.Imaging;

public enum ViewportMode
{
    Fit,
    ActualSize,
    Custom,
}

public sealed class ViewportTransform
{
    private const double MinimumScale = 0.01;
    private const double MaximumScale = 64;

    public ViewportMode Mode { get; private set; } = ViewportMode.Fit;

    public double Scale { get; private set; } = 1;

    public double OffsetX { get; private set; }

    public double OffsetY { get; private set; }

    public void Fit(PixelSize image, double viewportWidth, double viewportHeight)
    {
        if (viewportWidth <= 0 || viewportHeight <= 0)
        {
            return;
        }

        Mode = ViewportMode.Fit;
        Scale = Math.Min(viewportWidth / image.Width, viewportHeight / image.Height);
        Center(image, viewportWidth, viewportHeight);
    }

    public void ActualSize(PixelSize image, double viewportWidth, double viewportHeight, double pixelScale = 1)
    {
        Mode = ViewportMode.ActualSize;
        Scale = pixelScale;
        Center(image, viewportWidth, viewportHeight);
    }

    public void ZoomAt(double factor, double anchorX, double anchorY)
    {
        if (factor <= 0)
        {
            return;
        }

        double oldScale = Scale;
        double newScale = Math.Clamp(oldScale * factor, MinimumScale, MaximumScale);
        double imageX = (anchorX - OffsetX) / oldScale;
        double imageY = (anchorY - OffsetY) / oldScale;

        Scale = newScale;
        OffsetX = anchorX - (imageX * newScale);
        OffsetY = anchorY - (imageY * newScale);
        Mode = ViewportMode.Custom;
    }

    public void Pan(double deltaX, double deltaY)
    {
        OffsetX += deltaX;
        OffsetY += deltaY;
        Mode = ViewportMode.Custom;
    }

    public void Reorient(PixelSize source, ViewOrientation previous, ViewOrientation current,
        double viewportWidth, double viewportHeight, double pixelScale = 1)
    {
        PixelSize display = current.GetDisplaySize(source);
        if (Mode == ViewportMode.Fit)
        {
            Fit(display, viewportWidth, viewportHeight);
        }
        else if (Mode == ViewportMode.ActualSize)
        {
            ActualSize(display, viewportWidth, viewportHeight, pixelScale);
        }
        else
        {
            var anchor = previous.ToSourcePoint(source,
                ((viewportWidth / 2) - OffsetX) / Scale, ((viewportHeight / 2) - OffsetY) / Scale);
            var point = current.ToDisplayPoint(source, anchor.X, anchor.Y);
            OffsetX = (viewportWidth / 2) - (point.X * Scale);
            OffsetY = (viewportHeight / 2) - (point.Y * Scale);
        }
    }

    public PixelRect? GetVisibleSourceRegion(PixelSize source, ViewOrientation orientation,
        double viewportWidth, double viewportHeight, int maximumEdge = 2048)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumEdge);
        if (viewportWidth <= 0 || viewportHeight <= 0 || Scale <= 0)
        {
            return null;
        }
        double left = -OffsetX / Scale;
        double top = -OffsetY / Scale;
        double right = (viewportWidth - OffsetX) / Scale;
        double bottom = (viewportHeight - OffsetY) / Scale;
        var first = orientation.ToSourcePoint(source, left, top);
        var second = orientation.ToSourcePoint(source, right, top);
        var third = orientation.ToSourcePoint(source, left, bottom);
        var fourth = orientation.ToSourcePoint(source, right, bottom);
        int sourceLeft = (int)Math.Clamp(Math.Floor(Math.Min(Math.Min(first.X, second.X), Math.Min(third.X, fourth.X))), 0, source.Width);
        int sourceTop = (int)Math.Clamp(Math.Floor(Math.Min(Math.Min(first.Y, second.Y), Math.Min(third.Y, fourth.Y))), 0, source.Height);
        int sourceRight = (int)Math.Clamp(Math.Ceiling(Math.Max(Math.Max(first.X, second.X), Math.Max(third.X, fourth.X))), 0, source.Width);
        int sourceBottom = (int)Math.Clamp(Math.Ceiling(Math.Max(Math.Max(first.Y, second.Y), Math.Max(third.Y, fourth.Y))), 0, source.Height);
        if (sourceRight <= sourceLeft || sourceBottom <= sourceTop)
        {
            return null;
        }
        int width = Math.Min(maximumEdge, sourceRight - sourceLeft);
        int height = Math.Min(maximumEdge, sourceBottom - sourceTop);
        return new PixelRect(sourceLeft + ((sourceRight - sourceLeft - width) / 2),
            sourceTop + ((sourceBottom - sourceTop - height) / 2), width, height);
    }

    private void Center(PixelSize image, double viewportWidth, double viewportHeight)
    {
        OffsetX = (viewportWidth - (image.Width * Scale)) / 2;
        OffsetY = (viewportHeight - (image.Height * Scale)) / 2;
    }
}
