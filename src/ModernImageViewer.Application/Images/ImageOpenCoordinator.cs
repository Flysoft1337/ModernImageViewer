using System.ComponentModel;

using ModernImageViewer.Imaging;

namespace ModernImageViewer.Application.Images;

public sealed class ImageOpenCoordinator(IImageFilePicker filePicker, IImageDecoder decoder)
    : INotifyPropertyChanged, IDisposable
{
    private CancellationTokenSource? _openCancellation;
    private long _requestVersion;
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

    public async Task PickAndOpenAsync(CancellationToken cancellationToken = default)
    {
        string? path = await filePicker.PickImageAsync(cancellationToken);
        if (path is not null)
        {
            await OpenAsync(path, cancellationToken);
        }
    }

    public async Task<bool> OpenAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        long version = Interlocked.Increment(ref _requestVersion);
        _openCancellation?.Cancel();
        _openCancellation?.Dispose();
        _openCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationToken token = _openCancellation.Token;
        ImageOpenState previous = State.Status == ImageOpenStatus.Loading
            ? new(State.Image is null ? ImageOpenStatus.Empty : ImageOpenStatus.Loaded, State.Image, State.FilePath)
            : State;

        State = new(ImageOpenStatus.Loading, previous.Image, previous.FilePath, path);

        try
        {
            PixelBuffer decoded = await decoder.DecodeAsync(path, token);
            if (version != Volatile.Read(ref _requestVersion) || token.IsCancellationRequested)
            {
                decoded.Dispose();
                return false;
            }

            previous.Image?.Dispose();
            State = new(ImageOpenStatus.Loaded, decoded, Path.GetFullPath(path));
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (version == Volatile.Read(ref _requestVersion))
            {
                State = previous;
            }

            return false;
        }
        catch (Exception exception) when (version == Volatile.Read(ref _requestVersion))
        {
            State = new(ImageOpenStatus.Error, previous.Image, previous.FilePath, path, MapError(exception));
            return false;
        }
    }

    public void Dispose()
    {
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
