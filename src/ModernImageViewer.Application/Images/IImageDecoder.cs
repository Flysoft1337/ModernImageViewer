using ModernImageViewer.Imaging;

namespace ModernImageViewer.Application.Images;

public interface IImageDecoder
{
    Task<PixelBuffer> DecodeAsync(string path, CancellationToken cancellationToken);
}
