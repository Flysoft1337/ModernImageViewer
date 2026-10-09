using ModernImageViewer.Imaging;

namespace ModernImageViewer.Application.Images;

public sealed partial class ImageOpenCoordinator
{
    private IImageFrameSession? _frameSession;
    private CancellationTokenSource? _frameCancellation;
    private long _frameVersion;
    private bool _frameDetail;
    private PixelSize? _framePreviewTarget;
    private object? _framePreviewUpgrade;

    public long RetainedFrameBytes => _frameSession?.RetainedPixelBytes ?? 0;
    public bool HasFrameSession => _frameSession is not null;

    public async Task CloseImageAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Task released = _frameSession?.ReleaseCompletion ?? Task.CompletedTask;
        CancelPendingOpen();
        _neighborCache?.Clear();
        PixelBuffer? image = State.Image;
        DecodedImageRegion? region = State.Region;
        State = new(ImageOpenStatus.Empty, RequestId: RequestVersion);
        region?.Dispose();
        image?.Dispose();
        await released;
    }

    public Task<bool> PresentPlaybackFrameAsync(int index, CancellationToken cancellationToken = default)
        => _framePreviewUpgrade is null ? PresentFrameAsync(index, cancellationToken) : Task.FromResult(false);

    public Task<bool> PresentFrameAsync(int index, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_frameSession is not null && State.Sequence?.Kind == ImageSequenceKind.Pages && index == State.FrameIndex)
        {
            CancelPendingRegion();
            return Task.FromResult(true);
        }
        if (State.Sequence?.Kind == ImageSequenceKind.Pages && index != State.FrameIndex) { _frameDetail = false; }
        PixelSize target = _frameDetail && State.Image is { } image ? image.SourceSize : _framePreviewTarget ?? _previewTarget;
        long budget = State.Sequence?.Kind == ImageSequenceKind.Animation
            ? ImageFrameLimits.MaximumFrameBytes : _frameDetail ? FullResolutionOutputLimit : PreviewReservedBytes;
        return ReadFrameAsync(index, target, budget, cancellationToken);
    }

    private async Task<bool> ReadFrameAsync(int index, PixelSize target, long budget, CancellationToken cancellationToken)
    {
        if (_frameSession is not { } session || State is not { Status: ImageOpenStatus.Loaded, Image: { } old } current
            || index < 0 || index >= session.Info.Count)
        {
            return false;
        }
        CancelPendingFrame();
        CancelPendingRefinement();
        CancelPendingPreviewUpgrade();
        _neighborCache?.CancelPending();
        long generation = _frameVersion;
        long openVersion = RequestVersion;
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _frameCancellation = cancellation;
        CancellationToken token = cancellation.Token;
        bool IsCurrent() => !_disposed && generation == _frameVersion && openVersion == RequestVersion
            && ReferenceEquals(session, _frameSession) && current.Source?.Identity == State.Source?.Identity
            && !token.IsCancellationRequested;
        PixelBuffer? pixels = null;
        try
        {
            pixels = await session.DecodeFrameAsync(index, target, budget, token);
            if (!IsCurrent()) { return false; }
            if (pixels.Pixels.Length > budget || pixels.Size.Width > target.Width || pixels.Size.Height > target.Height
                || pixels.Size.Width > pixels.SourceSize.Width || pixels.Size.Height > pixels.SourceSize.Height
                || pixels.SourceSize != session.Info.Frames[index].CanvasSize)
            {
                throw new ImageSizeLimitExceededException();
            }
            if (pixels.SourceFileStamp is { } stamp && stamp != session.FileStamp)
            {
                throw new IOException("The source changed during browsing.");
            }
            DecodedImageRegion? region = State.Region;
            State = State with
            {
                Image = pixels,
                FrameIndex = index,
                IsPreview = pixels.Size != pixels.SourceSize,
                Region = null,
                IsRefining = false,
                IsRegionLoading = false,
                PendingRegionBounds = null,
                RefinementError = ImageOpenError.None
            };
            pixels = null;
            region?.Dispose();
            old.Dispose();
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return false; }
        catch (Exception exception)
        {
            if (IsCurrent()) { State = State with { RefinementError = MapError(exception) }; }
            return false;
        }
        finally
        {
            pixels?.Dispose();
            if (ReferenceEquals(_frameCancellation, cancellation)) { _frameCancellation = null; }
        }
    }

    private async Task<bool> UpgradeFramePreviewAsync(PixelSize target, CancellationToken cancellationToken)
    {
        if (_frameSession is null || _frameDetail || State.Image is not { } image || State.Status != ImageOpenStatus.Loaded)
        {
            return false;
        }
        long budget = State.Sequence?.Kind == ImageSequenceKind.Animation ? ImageFrameLimits.MaximumFrameBytes : PreviewReservedBytes;
        PixelSize desired = ImageFrameLimits.Fit(image.SourceSize, target, budget);
        if (desired.Width <= image.Size.Width * 1.15 && desired.Height <= image.Size.Height * 1.15) { return false; }
        object upgrade = new();
        _framePreviewUpgrade = upgrade;
        try
        {
            bool success = await ReadFrameAsync(State.FrameIndex, target, budget, cancellationToken);
            if (success) { _framePreviewTarget = target; }
            return success;
        }
        finally
        {
            if (ReferenceEquals(_framePreviewUpgrade, upgrade)) { _framePreviewUpgrade = null; }
        }
    }

    private async Task<bool> RefineFrameAsync(CancellationToken cancellationToken)
    {
        if (_frameSession is null || State.Image is not { } image || State.Status != ImageOpenStatus.Loaded) { return false; }
        if (!State.IsPreview) { return true; }
        long budget = State.Sequence?.Kind == ImageSequenceKind.Animation ? ImageFrameLimits.MaximumFrameBytes : FullResolutionOutputLimit;
        if (image.SourceSize.PixelCount > budget / 4)
        {
            State = State with { RefinementError = ImageOpenError.ImageTooLarge };
            return false;
        }
        bool success = await ReadFrameAsync(State.FrameIndex, image.SourceSize, budget, cancellationToken);
        if (success) { _frameDetail = true; }
        return success;
    }

    private async Task<bool> RequestFrameRegionAsync(PixelRect bounds, CancellationToken cancellationToken)
    {
        if (_frameSession is not { } session || session.Info.Kind != ImageSequenceKind.Pages
            || State is not { Status: ImageOpenStatus.Loaded, IsPreview: true, Image: { } preview } current)
        {
            return false;
        }
        if (current.Region?.Bounds.Contains(bounds) != true && _frameCancellation is { IsCancellationRequested: false }
            && current.PendingRegionBounds is { } pending && pending.Contains(bounds))
        {
            return false;
        }
        CancelPendingRegion();
        if (current.Region is { } retained && retained.Bounds.Contains(bounds)) { return true; }
        CancelPendingPreviewUpgrade();
        long generation = _frameVersion;
        long openVersion = RequestVersion;
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _frameCancellation = cancellation;
        CancellationToken token = cancellation.Token;
        bool IsCurrent() => !_disposed && generation == _frameVersion && openVersion == RequestVersion
            && ReferenceEquals(session, _frameSession) && State.FrameIndex == current.FrameIndex
            && ReferenceEquals(State.Image, preview) && !token.IsCancellationRequested;
        State = current with { IsRegionLoading = true, PendingRegionBounds = bounds, RefinementError = ImageOpenError.None };
        DecodedImageRegion? decoded = null;
        try
        {
            decoded = await session.DecodeRegionAsync(current.FrameIndex, bounds, preview.SourceSize, MaximumRegionBytes, token);
            if (!IsCurrent()) { return false; }
            if (decoded.Bounds != bounds || decoded.Image.Size != bounds.Size || decoded.Image.SourceSize != preview.SourceSize
                || decoded.Image.Pixels.Length > MaximumRegionBytes || decoded.Image.SourceFileStamp != session.FileStamp)
            {
                throw new ImageDecodeException(ImageOpenError.CorruptFile);
            }
            DecodedImageRegion? previous = State.Region;
            State = State with { Region = decoded, IsRegionLoading = false, PendingRegionBounds = null };
            decoded = null;
            previous?.Dispose();
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return false; }
        catch (Exception exception)
        {
            if (IsCurrent()) { State = State with { IsRegionLoading = false, PendingRegionBounds = null, RefinementError = MapError(exception) }; }
            return false;
        }
        finally
        {
            decoded?.Dispose();
            if (IsCurrent() && State.IsRegionLoading) { State = State with { IsRegionLoading = false, PendingRegionBounds = null }; }
            if (ReferenceEquals(_frameCancellation, cancellation)) { _frameCancellation = null; }
        }
    }

    private void CancelPendingFrame()
    {
        ++_frameVersion;
        _frameCancellation?.Cancel();
        _frameCancellation = null;
    }

    private void StopFrameSession()
    {
        CancelPendingFrame();
        IImageFrameSession? session = _frameSession;
        _frameSession = null;
        _frameDetail = false;
        _framePreviewTarget = null;
        _framePreviewUpgrade = null;
        session?.Dispose();
    }
}
