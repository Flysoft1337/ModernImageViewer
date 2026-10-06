using System.ComponentModel;

using ModernImageViewer.Application.Browsing;
using ModernImageViewer.Imaging;

namespace ModernImageViewer.Application.Images;

public sealed partial class ImageOpenCoordinator(IImageFilePicker filePicker, IImageDecoder decoder, ImageBrowseSession? browseSession = null)
    : INotifyPropertyChanged, IDisposable
{
    public static PixelSize PreviewMaximumSize => PreviewDecodePolicy.FallbackTarget;
    public const long MainPixelBudgetBytes = 160L * 1024 * 1024;
    private const long PreviewReservedBytes = PreviewDecodePolicy.MaximumBytes;
    private readonly NeighborPreviewCache? _neighborCache = decoder is IPrefetchImageDecoder prefetchDecoder
        ? new NeighborPreviewCache(prefetchDecoder) : null;
    private ImageBrowseSession? _currentSession;
    private int _browseDirection = 1;
    private CancellationTokenSource? _refinementCancellation;
    private long _refinementVersion;
    private CancellationTokenSource? _openCancellation;
    private long _requestVersion;
    public long RequestVersion => Volatile.Read(ref _requestVersion);
    private CancellationTokenSource? _indexCancellation;
    private Task _indexingTask = Task.CompletedTask;
    private ImageBrowseSession? _indexingSession;
    private bool _disposed;
    private ImageOpenState _state = new(ImageOpenStatus.Empty);

    public event PropertyChangedEventHandler? PropertyChanged;

    public ImageOpenState State
    {
        get => _state;
        private set
        {
            _state = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(State)));
        }
    }

    public int SkippedCandidateCount { get; private set; }

    public bool IsIndexing => _indexingSession?.IsIndexing == true;

    public Task PickAndOpenAsync(CancellationToken cancellationToken = default) =>
        PickAndOpenAsync(null, cancellationToken);

    public async Task PickAndOpenAsync(ImageBrowseSession? browsing, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string? path = await filePicker.PickImageAsync(cancellationToken);
        if (path is not null && !cancellationToken.IsCancellationRequested)
        {
            await OpenCandidatesAsync([path], browsing, resetSelection: true, cancellationToken: cancellationToken);
        }
    }

    public Task<bool> OpenAsync(string path, CancellationToken cancellationToken = default) =>
        OpenCandidatesAsync([path], cancellationToken: cancellationToken);

    public async Task<bool> OpenCandidatesAsync(IReadOnlyList<string> paths, ImageBrowseSession? browsing = null,
        bool selection = false, bool resetSelection = false, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count == 0)
        {
            return false;
        }
        foreach (string path in paths)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
        }

        (browsing ?? browseSession)?.CancelSorting();
        StopFrameSession();
        CancelPendingIndexing();
        CancelPendingRefinement();
        CancelPendingPreviewUpgrade();
        _neighborCache?.CancelPending();
        long version = Interlocked.Increment(ref _requestVersion);
        _openCancellation?.Cancel();
        _openCancellation?.Dispose();
        _openCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationToken token = _openCancellation.Token;
        ImageOpenState previous = State.Status == ImageOpenStatus.Loading
            ? State with { Status = State.Image is null ? ImageOpenStatus.Empty : ImageOpenStatus.Loaded, PendingPath = null }
            : State;
        ImageBrowseSession? session = browsing ?? browseSession;
        if (resetSelection || selection || !ReferenceEquals(session, _currentSession)
            || !StringComparer.OrdinalIgnoreCase.Equals(Path.GetDirectoryName(State.FilePath), Path.GetDirectoryName(Path.GetFullPath(paths[0]))))
        {
            _neighborCache?.Clear();
        }
        _currentSession = session;
        string targetPath = Path.GetFullPath(paths[0]);
        if (StringComparer.OrdinalIgnoreCase.Equals(session?.GetPreviousPath(), targetPath))
        {
            _browseDirection = -1;
        }
        else if (StringComparer.OrdinalIgnoreCase.Equals(session?.GetNextPath(), targetPath) || resetSelection || selection)
        {
            _browseDirection = 1;
        }
        SkippedCandidateCount = 0;
        PixelSize previewTarget = _previewTarget;
        State = previous with { Status = ImageOpenStatus.Loading, PendingPath = paths[0], Error = ImageOpenError.None, RequestId = version };
        Exception? lastError = null;
        string? failedPath = null;

        foreach (string path in paths)
        {
            PixelBuffer? decoded = null;
            IImageFrameSession? frameSession = null;
            bool frameBudgetLimited = false;
            try
            {
                token.ThrowIfCancellationRequested();
                if (decoder is IImageFrameDecoder frameDecoder)
                {
                    try { frameSession = await frameDecoder.TryOpenFrameSessionAsync(path, token); }
                    catch (ImageSizeLimitExceededException) { frameBudgetLimited = true; }
                }
                decoded = frameSession is not null || _neighborCache is null ? null : await _neighborCache.TryTakeAsync(path, token);
                bool cached = decoded is not null;
                decoded ??= frameSession is not null
                    ? await frameSession.DecodeFrameAsync(0, previewTarget,
                        frameSession.Info.Kind == ImageSequenceKind.Animation ? ImageFrameLimits.MaximumFrameBytes : PreviewReservedBytes, token)
                    : decoder is IPreviewImageDecoder previewDecoder
                    ? await previewDecoder.DecodePreviewAsync(path, previewTarget, token)
                    : await decoder.DecodeAsync(path, token);
                if (_disposed || version != Volatile.Read(ref _requestVersion) || token.IsCancellationRequested)
                {
                    decoded.Dispose();
                    return false;
                }
                if (decoded.Pixels.Length > PreviewReservedBytes || decoded.Size.Width > decoded.SourceSize.Width
                    || decoded.Size.Height > decoded.SourceSize.Height
                    || (frameSession is not null && (decoded.SourceSize != frameSession.Info.Frames[0].CanvasSize
                        || (frameSession.Info.Kind == ImageSequenceKind.Animation && decoded.Pixels.Length > ImageFrameLimits.MaximumFrameBytes)))
                    || (!cached && decoder is IPreviewImageDecoder
                        && (decoded.Size.Width > previewTarget.Width || decoded.Size.Height > previewTarget.Height)))
                {
                    throw new ImageSizeLimitExceededException();
                }

                long? indexingRevision = null;
                if (session is not null)
                {
                    if (selection)
                    {
                        session.CommitSelection(paths.Skip(SkippedCandidateCount).ToArray(), path);
                    }
                    else if (!session.TryCommitCached(path, resetSelection))
                    {
                        indexingRevision = session.BeginIndexing(path);
                    }
                }
                _previewNeedsTarget = cached;
                _frameSession = frameSession;
                frameSession = null;
                State = new(ImageOpenStatus.Loaded, decoded, Path.GetFullPath(path), IsPreview: decoded.Size != decoded.SourceSize,
                    Source: new ImageSource(Guid.NewGuid(), ImageSourceKind.File), RequestId: version, Sequence: _frameSession?.Info,
                    RefinementError: frameBudgetLimited ? ImageOpenError.ImageTooLarge : ImageOpenError.None,
                    IsSequenceUnavailable: frameBudgetLimited);
                previous.Region?.Dispose();
                previous.Image?.Dispose();
                if (session is not null && indexingRevision is { } revision
                    && !_disposed && version == Volatile.Read(ref _requestVersion))
                {
                    _indexingSession = session;
                    _indexCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
                    _indexingTask = IndexDirectoryAsync(session, path, revision, version, _indexCancellation.Token);
                }
                ScheduleNeighborPreview();
                return true;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                decoded?.Dispose();
                if (version == Volatile.Read(ref _requestVersion))
                {
                    State = previous;
                }
                return false;
            }
            catch (Exception exception)
            {
                decoded?.Dispose();
                if (version != Volatile.Read(ref _requestVersion) || token.IsCancellationRequested)
                {
                    return false;
                }
                lastError = exception;
                failedPath = path;
                SkippedCandidateCount++;
            }
            finally
            {
                frameSession?.Dispose();
            }
        }
        if (version == Volatile.Read(ref _requestVersion))
        {
            State = previous with { Status = ImageOpenStatus.Error, PendingPath = failedPath, Error = MapError(lastError!) };
        }
        return false;
    }

    public async Task<bool> RefineAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (State.Sequence is not null)
        {
            return await RefineFrameAsync(cancellationToken);
        }
        ImageOpenState current = State;
        if (current.Status != ImageOpenStatus.Loaded || !current.IsPreview || current.IsRefining
            || current.Image is null || (current.IsMemorySource ? current.Source?.Memory is null
                : current.FilePath is null || decoder is not IPreviewImageDecoder))
        {
            return false;
        }

        CancelPendingRegion();
        CancelPendingPreviewUpgrade();
        _neighborCache?.CancelPending();
        current = State;
        PixelBuffer preview = current.Image!;
        // Reserve the current and next previews, including a cancelled detail finishing late.
        long maximumDecodedBytes = Math.Min(MainPixelBudgetBytes - preview.Pixels.Length,
            MainPixelBudgetBytes - 2 * PreviewReservedBytes - NeighborPreviewCache.MaximumCachedBytes);
        if (preview.SourceSize.PixelCount > maximumDecodedBytes / 4)
        {
            State = current with { RefinementError = ImageOpenError.ImageTooLarge };
            return false;
        }

        long openVersion = Volatile.Read(ref _requestVersion);
        long refinementVersion = Interlocked.Increment(ref _refinementVersion);
        CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _refinementCancellation = cancellation;
        CancellationToken token = cancellation.Token;
        State = current with { IsRefining = true, RefinementError = ImageOpenError.None };
        PixelBuffer? detail = null;
        try
        {
            detail = current.Source?.Memory is { } memory
                ? await ReadMemoryPixelsAsync(memory, memory.SourceSize, maximumDecodedBytes, true, token)
                : await ((IPreviewImageDecoder)decoder).DecodeDetailAsync(current.FilePath!, maximumDecodedBytes, token);
            if (current.IsMemorySource)
            {
                ValidateMemoryPixels(detail, current.Source!.Memory!, preview.SourceSize, maximumDecodedBytes);
            }
            if (!IsCurrentRefinement(openVersion, refinementVersion, preview) || token.IsCancellationRequested)
            {
                return false;
            }
            // Also enforce the output bound for custom implementations of the decoder interface.
            if (detail.Pixels.Length > maximumDecodedBytes)
            {
                throw new ImageSizeLimitExceededException();
            }
            if (preview.SourceFileStamp is { } sourceStamp && detail.SourceFileStamp is { } detailStamp && sourceStamp != detailStamp)
            {
                throw new IOException("The source changed during browsing.");
            }
            State = State with
            {
                Image = detail,
                IsPreview = detail.Size != detail.SourceSize,
                IsRefining = false,
                RefinementError = ImageOpenError.None,
                Region = null
            };
            current.Region?.Dispose();
            detail = null;
            preview.Dispose();
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception exception)
        {
            if (IsCurrentRefinement(openVersion, refinementVersion, preview) && !token.IsCancellationRequested)
            {
                State = State with { IsRefining = false, RefinementError = MapError(exception) };
            }
            return false;
        }
        finally
        {
            detail?.Dispose();
            if (IsCurrentRefinement(openVersion, refinementVersion, preview) && State.IsRefining)
            {
                State = State with { IsRefining = false };
            }
            if (ReferenceEquals(_refinementCancellation, cancellation))
            {
                _refinementCancellation = null;
            }
            cancellation.Dispose();
        }
    }

    private bool IsCurrentRefinement(long openVersion, long refinementVersion, PixelBuffer preview) =>
        !_disposed && openVersion == Volatile.Read(ref _requestVersion)
        && refinementVersion == Volatile.Read(ref _refinementVersion) && ReferenceEquals(State.Image, preview);

    private void CancelPendingRefinement()
    {
        CancelPendingRegion();
        Interlocked.Increment(ref _refinementVersion);
        _refinementCancellation?.Cancel();
        _refinementCancellation = null;
        if (!_disposed && State.IsRefining)
        {
            State = State with { IsRefining = false };
        }
    }

    // Opening completes when pixels are ready; callers that need navigation can await the index separately.
    public Task WaitForIndexingAsync(CancellationToken cancellationToken = default) =>
        _indexingTask.WaitAsync(cancellationToken);

    public void CancelPendingIndexing()
    {
        bool wasIndexing = IsIndexing;
        _indexCancellation?.Cancel();
        _indexCancellation?.Dispose();
        _indexCancellation = null;
        _indexingSession?.CancelIndexing();
        _indexingSession = null;
        _indexingTask = Task.CompletedTask;
        if (wasIndexing && !_disposed)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsIndexing)));
        }
    }

    private async Task IndexDirectoryAsync(ImageBrowseSession session, string path, long revision, long version, CancellationToken token)
    {
        ImageBrowseSession.BrowseSnapshot? snapshot = null;
        try
        {
            snapshot = await session.PrepareSnapshotAsync(path, token, resetSelection: true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (IOException) { }
        catch (Exception)
        {
            // Index failures retain the displayed image and its provisional single-item navigation.
        }
        finally
        {
            if (!_disposed && version == Volatile.Read(ref _requestVersion)
                && session.CompleteIndexing(revision, token.IsCancellationRequested ? null : snapshot, path))
            {
                _indexingSession = null;
                ScheduleNeighborPreview();
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsIndexing)));
            }
        }
    }

    public void CancelPendingOpen()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        StopFrameSession();
        _neighborCache?.CancelPending();
        CancelPendingIndexing();
        CancelPendingRefinement();
        CancelPendingPreviewUpgrade();
        Interlocked.Increment(ref _requestVersion);
        _openCancellation?.Cancel();
        if (State.Status == ImageOpenStatus.Loading)
        {
            State = State with { Status = State.Image is null ? ImageOpenStatus.Empty : ImageOpenStatus.Loaded, PendingPath = null };
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopFrameSession();
        CancelPendingIndexing();
        CancelPendingRefinement();
        CancelPendingPreviewUpgrade();
        Interlocked.Increment(ref _requestVersion);
        _openCancellation?.Cancel();
        _openCancellation?.Dispose();
        _neighborCache?.Dispose();
        State.Region?.Dispose();
        State.Image?.Dispose();
    }

    private static ImageOpenError MapError(Exception exception)
    {
        return exception switch
        {
            FileNotFoundException => ImageOpenError.FileNotFound,
            UnauthorizedAccessException => ImageOpenError.AccessDenied,
            ImageSizeLimitExceededException => ImageOpenError.ImageTooLarge,
            ImageDecodeException decodeException => decodeException.Error,
            _ => ImageOpenError.DecodeFailed,
        };
    }
}
