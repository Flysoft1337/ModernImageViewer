using ModernImageViewer.Imaging;

namespace ModernImageViewer.Application.Images;

public sealed partial class ImageOpenCoordinator
{
    private PixelSize _previewTarget = PreviewDecodePolicy.FallbackTarget;
    private CancellationTokenSource? _previewCancellation;
    private long _previewVersion;
    private bool _previewNeedsTarget;

    public long CachedNeighborBytes => _neighborCache?.RetainedBytes ?? 0;

    public void SetPreviewTarget(PixelSize target) => _previewTarget = PreviewDecodePolicy.Constrain(target);

    public async Task<bool> UpgradePreviewAsync(PixelSize target, ViewOrientation orientation = default,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        SetPreviewTarget(target);
        ImageOpenState current = State;
        PixelSize decodeTarget = orientation.QuarterTurns % 2 == 0 ? _previewTarget : new(_previewTarget.Height, _previewTarget.Width);
        if (current.Sequence is not null)
        {
            return await UpgradeFramePreviewAsync(decodeTarget, cancellationToken);
        }
        if (current is not { Status: ImageOpenStatus.Loaded, IsPreview: true, Image: { } preview }
            || current.IsRefining || current.IsRegionLoading || !PreviewDecodePolicy.NeedsUpgrade(preview, decodeTarget, _previewNeedsTarget)
            || (!current.IsMemorySource && decoder is not IPreviewImageDecoder))
        {
            return false;
        }
        CancelPendingPreviewUpgrade();
        _neighborCache?.CancelPending();
        long version = _previewVersion;
        long openVersion = RequestVersion;
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _previewCancellation = cancellation;
        CancellationToken token = cancellation.Token;
        PixelBuffer? replacement = null;
        bool IsCurrent() => !_disposed && version == _previewVersion && openVersion == RequestVersion
            && ReferenceEquals(State.Image, preview) && !token.IsCancellationRequested;
        try
        {
            replacement = current.Source?.Memory is { } memory
                ? await ReadMemoryPixelsAsync(memory, decodeTarget, PreviewReservedBytes, false, token)
                : await ((IPreviewImageDecoder)decoder).DecodePreviewAsync(current.FilePath!, decodeTarget, token);
            if (!IsCurrent()) { return false; }
            if (replacement.SourceSize != preview.SourceSize || replacement.Size.Width > decodeTarget.Width
                || replacement.Size.Height > decodeTarget.Height || replacement.Size.Width > preview.SourceSize.Width
                || replacement.Size.Height > preview.SourceSize.Height || replacement.Pixels.Length > PreviewReservedBytes)
            {
                throw new ImageSizeLimitExceededException();
            }
            if (replacement.SourceFileStamp is { } stamp && preview.SourceFileStamp is { } previousStamp && stamp != previousStamp)
            {
                // External edits require a fresh open/source identity, never a refinement of old coordinates.
                return false;
            }
            if (replacement.Size.Width <= preview.Size.Width && replacement.Size.Height <= preview.Size.Height) { return false; }
            State = State with { Image = replacement, IsPreview = replacement.Size != replacement.SourceSize };
            _previewNeedsTarget = false;
            replacement = null;
            preview.Dispose();
            ScheduleNeighborPreview();
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return false; }
        catch (Exception) { return false; }
        finally
        {
            replacement?.Dispose();
            if (ReferenceEquals(_previewCancellation, cancellation)) { _previewCancellation = null; }
        }
    }

    private void CancelPendingPreviewUpgrade()
    {
        ++_previewVersion;
        _previewCancellation?.Cancel();
        _previewCancellation = null;
    }
}
