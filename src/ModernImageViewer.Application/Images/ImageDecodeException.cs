namespace ModernImageViewer.Application.Images;

public sealed class ImageDecodeException(ImageOpenError error, Exception? innerException = null)
    : Exception("Image decoding failed.", innerException)
{
    public ImageOpenError Error { get; } = error;
}
