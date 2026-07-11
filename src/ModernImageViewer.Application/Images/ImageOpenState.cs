using ModernImageViewer.Imaging;

namespace ModernImageViewer.Application.Images;

public enum ImageOpenStatus
{
    Empty,
    Loading,
    Loaded,
    Error,
}

public enum ImageOpenError
{
    None,
    FileNotFound,
    AccessDenied,
    UnsupportedFormat,
    CorruptFile,
    ImageTooLarge,
    DecodeFailed,
}

public sealed record ImageOpenState(
    ImageOpenStatus Status,
    PixelBuffer? Image = null,
    string? FileName = null,
    ImageOpenError Error = ImageOpenError.None);
