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
    RawNoPreview,
}

public sealed record ImageOpenState(
    ImageOpenStatus Status,
    PixelBuffer? Image = null,
    string? FilePath = null,
    string? PendingPath = null,
    ImageOpenError Error = ImageOpenError.None,
    bool IsPreview = false,
    bool IsRefining = false,
    ImageOpenError RefinementError = ImageOpenError.None,
    DecodedImageRegion? Region = null,
    bool IsRegionLoading = false,
    ImageSource? Source = null,
    long RequestId = 0,
    ImageSequenceInfo? Sequence = null,
    int FrameIndex = 0,
    bool IsSequenceUnavailable = false)
{
    public bool IsMemorySource => Source?.Kind == ImageSourceKind.Memory;
}
