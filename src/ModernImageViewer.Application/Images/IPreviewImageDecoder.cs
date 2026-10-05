using ModernImageViewer.Imaging;

namespace ModernImageViewer.Application.Images;

public interface IPreviewImageDecoder : IImageDecoder
{
    Task<PixelBuffer> DecodePreviewAsync(string path, PixelSize maximumSize, CancellationToken cancellationToken);

    // Implementations should validate this budget before allocating full-resolution output.
    Task<PixelBuffer> DecodeDetailAsync(string path, long maximumDecodedBytes, CancellationToken cancellationToken) =>
        DecodeAsync(path, cancellationToken);
}
