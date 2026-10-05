using ModernImageViewer.Imaging;

namespace ModernImageViewer.Application.Images;

public interface IPrefetchImageDecoder
{
    // Return null immediately when the foreground decoder slot is occupied; never queue a prefetch.
    Task<PixelBuffer?> TryDecodePreviewAsync(string path, PixelSize maximumSize, CancellationToken cancellationToken);
}
