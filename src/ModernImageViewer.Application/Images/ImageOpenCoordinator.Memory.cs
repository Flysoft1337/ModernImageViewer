using ModernImageViewer.Application.Browsing;
using ModernImageViewer.Application.Integration;
using ModernImageViewer.Imaging;

namespace ModernImageViewer.Application.Images;

public sealed partial class ImageOpenCoordinator
{
    public async Task<bool> OpenMemoryAsync(MemoryImageInput input, ImageBrowseSession? browsing = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(input);
        CancelPendingOpen();
        _openCancellation?.Dispose();
        ImageOpenState previous = State;
        long version = Volatile.Read(ref _requestVersion);
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _openCancellation = cancellation;
        CancellationToken token = cancellation.Token;
        ImageBrowseSession? session = browsing ?? browseSession;
        session?.CancelSorting();
        State = previous with { Status = ImageOpenStatus.Loading, PendingPath = null, Error = ImageOpenError.None };
        PixelBuffer? image = null;
        try
        {
            if (input.SourceSize.Width > 32768 || input.SourceSize.Height > 32768
                || input.SourceSize.PixelCount > ClipboardImageLimits.SourceBytes / 4)
            {
                throw new ImageSizeLimitExceededException();
            }
            image = await input.ReadPixelsAsync(PreviewMaximumSize, PreviewReservedBytes, token);
            ValidateMemoryPixels(image, input, PreviewMaximumSize, PreviewReservedBytes);
            if (_disposed || version != Volatile.Read(ref _requestVersion) || token.IsCancellationRequested)
            {
                return false;
            }
            session?.Clear();
            _currentSession = null;
            _neighborCache?.Clear();
            previous.Region?.Dispose();
            previous.Image?.Dispose();
            State = new(ImageOpenStatus.Loaded, image, IsPreview: image.Size != image.SourceSize,
                Source: new ImageSource(Guid.NewGuid(), ImageSourceKind.Memory, input));
            image = null;
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (!_disposed && version == Volatile.Read(ref _requestVersion)) { State = previous; }
            return false;
        }
        catch (Exception exception)
        {
            if (!_disposed && version == Volatile.Read(ref _requestVersion) && !token.IsCancellationRequested)
            {
                State = previous with { Status = ImageOpenStatus.Error, Error = MapError(exception) };
            }
            return false;
        }
        finally
        {
            image?.Dispose();
            if (ReferenceEquals(_openCancellation, cancellation)) { _openCancellation = null; }
        }
    }

    private static void ValidateMemoryPixels(PixelBuffer image, MemoryImageInput source, PixelSize maximumSize, long maximumBytes)
    {
        if (image.SourceSize != source.SourceSize || image.Size.Width > maximumSize.Width
            || image.Size.Height > maximumSize.Height || image.Size.Width > source.SourceSize.Width
            || image.Size.Height > source.SourceSize.Height || image.Pixels.Length > maximumBytes)
        {
            throw new ImageSizeLimitExceededException();
        }
    }
}
