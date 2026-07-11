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

    public void ActualSize(PixelSize image, double viewportWidth, double viewportHeight)
    {
        Mode = ViewportMode.ActualSize;
        Scale = 1;
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

    private void Center(PixelSize image, double viewportWidth, double viewportHeight)
    {
        OffsetX = (viewportWidth - (image.Width * Scale)) / 2;
        OffsetY = (viewportHeight - (image.Height * Scale)) / 2;
    }
}
