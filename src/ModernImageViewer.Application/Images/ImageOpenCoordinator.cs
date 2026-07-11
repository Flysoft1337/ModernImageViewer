using System.ComponentModel;
using System.Runtime.CompilerServices;

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

    public async Task OpenAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        long version = Interlocked.Increment(ref _requestVersion);
        _openCancellation?.Cancel();
        _openCancellation?.Dispose();
        _openCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationToken token = _openCancellation.Token;
        PixelBuffer? previousImage = State.Image;

        State = new(ImageOpenStatus.Loading, previousImage, Path.GetFileName(path));

        try
        {
            PixelBuffer decoded = await decoder.DecodeAsync(path, token);
            if (version != Volatile.Read(ref _requestVersion) || token.IsCancellationRequested)
            {
                decoded.Dispose();
                return;
            }

            previousImage?.Dispose();
            State = new(ImageOpenStatus.Loaded, decoded, Path.GetFileName(path));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (version == Volatile.Read(ref _requestVersion))
        {
            State = new(ImageOpenStatus.Error, previousImage, Path.GetFileName(path), MapError(exception));
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
