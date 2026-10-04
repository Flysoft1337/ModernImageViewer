using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

using ModernImageViewer.Application.Images;
using ModernImageViewer.Imaging;

namespace ModernImageViewer.UI.Controls;

public sealed class ThumbnailImage : Image, IDisposable
{
    private static readonly PixelSize MaximumSize = new(224, 140);
    private static readonly SemaphoreSlim DecodeSlots = new(2);
    private static readonly ThumbnailCache<BitmapSource> Cache = new(24, 2 * 1024 * 1024);
    private CancellationTokenSource? _loadCancellation;
    private int _loadVersion;

    public static readonly DependencyProperty FilePathProperty = DependencyProperty.Register(
        nameof(FilePath), typeof(string), typeof(ThumbnailImage), new PropertyMetadata(null, OnInputChanged));

    public static readonly DependencyProperty DecoderProperty = DependencyProperty.Register(
        nameof(Decoder), typeof(IThumbnailDecoder), typeof(ThumbnailImage), new PropertyMetadata(null, OnInputChanged));

    public ThumbnailImage()
    {
        Loaded += (_, _) => LoadThumbnail();
        Unloaded += (_, _) => Dispose();
    }

    public string? FilePath
    {
        get => (string?)GetValue(FilePathProperty);
        set => SetValue(FilePathProperty, value);
    }

    public IThumbnailDecoder? Decoder
    {
        get => (IThumbnailDecoder?)GetValue(DecoderProperty);
        set => SetValue(DecoderProperty, value);
    }

    public static void ClearCache() => Cache.Clear();

    public void Dispose()
    {
        _loadVersion++;
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = null;
        Source = null;
        GC.SuppressFinalize(this);
    }

    private static void OnInputChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e) =>
        ((ThumbnailImage)sender).LoadThumbnail();

    private async void LoadThumbnail()
    {
        Dispose();
        if (!IsLoaded || FilePath is not { Length: > 0 } path || Decoder is not { } decoder)
        {
            return;
        }

        int version = _loadVersion;
        _loadCancellation = new CancellationTokenSource();
        CancellationToken token = _loadCancellation.Token;
        try
        {
            // File metadata, decoder work and WPF pixel copies all stay off the UI thread.
            var result = await Task.Run(() => GetThumbnailAsync(path, decoder, token), token);
            token.ThrowIfCancellationRequested();
            if (IsLoaded && version == _loadVersion && StringComparer.OrdinalIgnoreCase.Equals(path, FilePath)
                && ReferenceEquals(decoder, Decoder) && Cache.Generation == result.Generation)
            {
                Source = result.Source;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or NotSupportedException or ArgumentException or FormatException or ImageDecodeException
            or System.Runtime.InteropServices.COMException or ImageSizeLimitExceededException)
        {
            // Keep the tile placeholder; opening the file reports its decoding error.
        }
    }

    private static async Task<(BitmapSource Source, long Generation)> GetThumbnailAsync(
        string path, IThumbnailDecoder decoder, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        long generation = Cache.Generation;
        ThumbnailCacheKey key = ReadFileStamp(path);
        if (Cache.TryGet(key, out BitmapSource? cached))
        {
            token.ThrowIfCancellationRequested();
            return (cached!, generation);
        }

        await DecodeSlots.WaitAsync(token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();
            if (Cache.TryGet(key, out cached))
            {
                return (cached!, generation);
            }

            using PixelBuffer pixels = await decoder.DecodeThumbnailAsync(path, MaximumSize, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            byte[] copy = pixels.Pixels.ToArray();
            BitmapSource thumbnail = BitmapSource.Create(pixels.Size.Width, pixels.Size.Height, 96, 96,
                PixelFormats.Pbgra32, null, copy, pixels.Stride);
            thumbnail.Freeze();
            token.ThrowIfCancellationRequested();

            // A changed file or an F5 cache clear must not be repopulated by an old decode.
            if (key == ReadFileStamp(path))
            {
                long pixelBytes = checked((long)pixels.Size.Width * pixels.Size.Height * 4);
                Cache.Store(key, thumbnail, pixelBytes, generation);
            }

            return (thumbnail, generation);
        }
        finally
        {
            DecodeSlots.Release();
        }
    }

    private static ThumbnailCacheKey ReadFileStamp(string path)
    {
        FileInfo file = new(path);
        return new ThumbnailCacheKey(file.FullName, file.LastWriteTimeUtc.Ticks, file.Length);
    }
}
