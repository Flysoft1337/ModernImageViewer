using ModernImageViewer.Imaging;

namespace ModernImageViewer.Application.Images;

public interface IThumbnailDecoder
{
    Task<PixelBuffer> DecodeThumbnailAsync(string path, PixelSize maximumSize, CancellationToken cancellationToken);
}
