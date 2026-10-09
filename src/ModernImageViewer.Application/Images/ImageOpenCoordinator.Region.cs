using ModernImageViewer.Imaging;

namespace ModernImageViewer.Application.Images;

public sealed partial class ImageOpenCoordinator
{
    public const long MaximumRegionBytes = 16L * 1024 * 1024;
    public static long FullResolutionOutputLimit => MainPixelBudgetBytes - 2 * PreviewReservedBytes
        - NeighborPreviewCache.MaximumCachedBytes;
    private CancellationTokenSource? _regionCancellation;
    private long _regionVersion;

    public bool CanRefineWholeImage => State.Image is { } image && image.SourceSize.PixelCount <=
        FullResolutionOutputLimit / 4;

    public async Task<bool> RequestRegionAsync(PixelRect bounds, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ImageOpenState current = State;
        if (current.Sequence is not null)
        {
            return await RequestFrameRegionAsync(bounds, cancellationToken);
        }
        if (current.Status != ImageOpenStatus.Loaded || !current.IsPreview || current.Image is null
            || current.FilePath is null || CanRefineWholeImage || decoder is not IRegionImageDecoder regionDecoder)
        {
            return false;
        }
        if (current.Region is { } existing && existing.Bounds.Contains(bounds))
        {
            CancelPendingRegion();
            CancelPendingPreviewUpgrade();
            return true;
        }
        // A zoom into an in-flight region still needs the same pixels; do not restart its native decode.
        if (_regionCancellation is { IsCancellationRequested: false }
            && current.PendingRegionBounds is { } pending && pending.Contains(bounds))
        {
            return false;
        }
        CancelPendingRegion();
        CancelPendingPreviewUpgrade();
        _neighborCache?.CancelPending();
        PixelBuffer preview = current.Image;
        long openVersion = Volatile.Read(ref _requestVersion);
        long regionVersion = Interlocked.Increment(ref _regionVersion);
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _regionCancellation = cancellation;
        CancellationToken token = cancellation.Token;
        State = State with { IsRegionLoading = true, PendingRegionBounds = bounds, RefinementError = ImageOpenError.None };
        DecodedImageRegion? region = null;
        try
        {
            region = await regionDecoder.DecodeRegionAsync(current.FilePath, bounds, preview.SourceSize, MaximumRegionBytes, token);
            if (!IsCurrentRegion(openVersion, regionVersion, preview) || token.IsCancellationRequested)
            {
                return false;
            }
            if (region.Bounds != bounds || region.Image.Size != bounds.Size
                || region.Image.SourceSize != preview.SourceSize
                || region.Image.Pixels.Length > MaximumRegionBytes)
            {
                throw new ImageSizeLimitExceededException();
            }
            if (preview.SourceFileStamp is { } sourceStamp && region.Image.SourceFileStamp is { } regionStamp && sourceStamp != regionStamp)
            {
                throw new IOException("The source changed during browsing.");
            }
            DecodedImageRegion? previous = State.Region;
            State = State with { Region = region, IsRegionLoading = false, PendingRegionBounds = null, RefinementError = ImageOpenError.None };
            region = null;
            previous?.Dispose();
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return false; }
        catch (Exception exception)
        {
            if (IsCurrentRegion(openVersion, regionVersion, preview) && !token.IsCancellationRequested)
            {
                State = State with { IsRegionLoading = false, PendingRegionBounds = null, RefinementError = MapError(exception) };
            }
            return false;
        }
        finally
        {
            region?.Dispose();
            if (IsCurrentRegion(openVersion, regionVersion, preview) && State.IsRegionLoading)
            {
                State = State with { IsRegionLoading = false, PendingRegionBounds = null };
            }
            if (ReferenceEquals(_regionCancellation, cancellation))
            {
                _regionCancellation = null;
            }
        }
    }

    private bool IsCurrentRegion(long openVersion, long regionVersion, PixelBuffer preview) =>
        !_disposed && openVersion == Volatile.Read(ref _requestVersion)
        && regionVersion == Volatile.Read(ref _regionVersion) && ReferenceEquals(State.Image, preview);

    public void CancelPendingRegion()
    {
        CancelPendingFrame();
        Interlocked.Increment(ref _regionVersion);
        _regionCancellation?.Cancel();
        _regionCancellation = null;
        if (!_disposed && State.IsRegionLoading)
        {
            State = State with { IsRegionLoading = false, PendingRegionBounds = null };
        }
    }

    public void ClearPreviewCache()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _neighborCache?.Clear();
        CancelPendingRefinement();
        CancelPendingPreviewUpgrade();
        CancelPendingRegion();
        DecodedImageRegion? region = State.Region;
        if (region is not null)
        {
            State = State with { Region = null };
            region.Dispose();
        }
    }

    public void RefreshNeighborPrefetch()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _neighborCache?.CancelPending();
        ScheduleNeighborPreview();
    }

    private void ScheduleNeighborPreview()
    {
        if (_disposed || _neighborCache is null || _currentSession is not { IsIndexing: false } session
            || State.Status != ImageOpenStatus.Loaded)
        {
            return;
        }
        string? path = _browseDirection < 0 ? session.GetPreviousPath() : session.GetNextPath();
        if (path is not null)
        {
            _neighborCache.Schedule(path);
        }
    }
}
