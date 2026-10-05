using ModernImageViewer.Imaging;

namespace ModernImageViewer.Application.Editing;

public enum ImageExportFormat { Png, Jpeg, Webp }
public enum ImageExportError { InvalidDestination, DestinationExists, SourceChanged, BudgetExceeded, DecodeFailed, WriteFailed }

public sealed class ImageExportException(ImageExportError error, Exception? inner = null) : Exception(error.ToString(), inner)
{
    public ImageExportError Error { get; } = error;
}

// JpegQuality is shared by JPEG and lossy WebP (1-100); keep the name for existing callers.
public sealed record ImageExportRequest(string? SourcePath, string DestinationPath, ImageEditRecipe Recipe,
    ImageExportFormat Format = ImageExportFormat.Png, int JpegQuality = 90,
    long? ExpectedSourceLength = null, DateTime? ExpectedSourceModifiedUtc = null,
    ImageExportPixels? SourcePixels = null, Guid? MemorySourceIdentity = null);

public sealed record ImageExportPixels(PixelSize Size, int Stride, ReadOnlyMemory<byte> Pixels, ImageFileStamp? SourceFileStamp = null);

public interface IImageExportService
{
    Task ExportAsync(ImageExportRequest request, CancellationToken cancellationToken = default);
}
