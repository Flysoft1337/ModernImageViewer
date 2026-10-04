using System.ComponentModel;

using ModernImageViewer.Application.Browsing;
using ModernImageViewer.Imaging;

namespace ModernImageViewer.Application.Images;

public sealed class ImageOpenCoordinator(IImageFilePicker filePicker, IImageDecoder decoder, ImageBrowseSession? browseSession = null)
    : INotifyPropertyChanged, IDisposable
{
    private CancellationTokenSource? _openCancellation;
    private long _requestVersion;
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

        long version = Interlocked.Increment(ref _requestVersion);
        _openCancellation?.Cancel();
        _openCancellation?.Dispose();
        _openCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationToken token = _openCancellation.Token;
        ImageOpenState previous = State.Status == ImageOpenStatus.Loading
            ? new(State.Image is null ? ImageOpenStatus.Empty : ImageOpenStatus.Loaded, State.Image, State.FilePath)
            : State;
        ImageBrowseSession? session = browsing ?? browseSession;
        SkippedCandidateCount = 0;
        State = new(ImageOpenStatus.Loading, previous.Image, previous.FilePath, paths[0]);
        Exception? lastError = null;
        string? failedPath = null;

        foreach (string path in paths)
        {
            PixelBuffer? decoded = null;
            try
            {
                token.ThrowIfCancellationRequested();
                decoded = await decoder.DecodeAsync(path, token);
                if (version != Volatile.Read(ref _requestVersion) || token.IsCancellationRequested)
                {
                    decoded.Dispose();
                    return false;
                }

                ImageBrowseSession.BrowseSnapshot? snapshot = null;
                if (session is not null && !selection)
                {
                    snapshot = await session.PrepareSnapshotAsync(path, token, resetSelection);
                }
                if (version != Volatile.Read(ref _requestVersion) || token.IsCancellationRequested)
                {
                    decoded.Dispose();
                    return false;
                }

                // Browsing and pixels are submitted together, only by the latest successful request.
                if (session is not null)
                {
                    if (selection)
                    {
                        session.CommitSelection(paths.Skip(SkippedCandidateCount).ToArray(), path);
                    }
                    else if (snapshot is not null)
                    {
                        session.CommitSnapshot(snapshot, path);
                    }
                }
                previous.Image?.Dispose();
                State = new(ImageOpenStatus.Loaded, decoded, Path.GetFullPath(path));
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
        }
        if (version == Volatile.Read(ref _requestVersion))
        {
            State = new(ImageOpenStatus.Error, previous.Image, previous.FilePath, failedPath, MapError(lastError!));
        }
        return false;
    }

    public void CancelPendingOpen()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Interlocked.Increment(ref _requestVersion);
        _openCancellation?.Cancel();
        if (State.Status == ImageOpenStatus.Loading)
        {
            State = new(State.Image is null ? ImageOpenStatus.Empty : ImageOpenStatus.Loaded, State.Image, State.FilePath);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Interlocked.Increment(ref _requestVersion);
        _openCancellation?.Cancel();
        _openCancellation?.Dispose();
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
