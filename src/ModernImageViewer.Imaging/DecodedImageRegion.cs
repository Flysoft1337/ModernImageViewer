namespace ModernImageViewer.Imaging;

public sealed record DecodedImageRegion(PixelBuffer Image, PixelRect Bounds) : IDisposable
{
    public void Dispose() => Image.Dispose();
}
