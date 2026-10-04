using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

using ModernImageViewer.Imaging;

namespace ModernImageViewer.UI.Controls;

public sealed class ThumbnailImage : Image, IDisposable
{
    private const int CacheCapacity = 24;
    private static readonly SemaphoreSlim DecodeSlots = new(2);
    private static readonly Dictionary<string, BitmapSource> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Queue<string> CacheOrder = new();
    private static readonly object CacheLock = new();
    private CancellationTokenSource? _loadCancellation;

    public static readonly DependencyProperty FilePathProperty = DependencyProperty.Register(
        nameof(FilePath), typeof(string), typeof(ThumbnailImage), new PropertyMetadata(null, OnFilePathChanged));

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

    public static void ClearCache()
    {
        lock (CacheLock)
        {
            Cache.Clear();
            CacheOrder.Clear();
        }
    }

    public void Dispose()
    {
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = null;
        Source = null;
        GC.SuppressFinalize(this);
    }

    private static void OnFilePathChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e) =>
        ((ThumbnailImage)sender).LoadThumbnail();

    private async void LoadThumbnail()
    {
        Dispose();
        if (!IsLoaded || FilePath is not { Length: > 0 } path)
        {
            return;
        }

        lock (CacheLock)
        {
            if (Cache.TryGetValue(path, out BitmapSource? cached))
            {
                Source = cached;
                return;
            }
        }

        _loadCancellation = new CancellationTokenSource();
        CancellationToken token = _loadCancellation.Token;
        try
        {
            await DecodeSlots.WaitAsync(token);
            BitmapSource thumbnail;
            try
            {
                thumbnail = await Task.Run(() => DecodeThumbnail(path, token), token);
            }
            finally
            {
                DecodeSlots.Release();
            }

            token.ThrowIfCancellationRequested();
            lock (CacheLock)
            {
                if (Cache.TryAdd(path, thumbnail))
                {
                    CacheOrder.Enqueue(path);
                    while (Cache.Count > CacheCapacity)
                    {
                        Cache.Remove(CacheOrder.Dequeue());
                    }
                }
            }

            Source = thumbnail;
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or NotSupportedException or ArgumentException or FormatException
            or System.Runtime.InteropServices.COMException or ImageSizeLimitExceededException)
        {
            // The tile keeps its placeholder. Opening the file still reports the normal error.
        }
    }

    private static BitmapImage DecodeThumbnail(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        BitmapDecoder decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        if (decoder is not (JpegBitmapDecoder or PngBitmapDecoder))
        {
            throw new NotSupportedException("Only JPEG and PNG thumbnails are supported.");
        }

        if (decoder.Frames.Count == 0)
        {
            throw new FileFormatException();
        }

        BitmapFrame frame = decoder.Frames[0];
        ImageDecodeLimits.Default.ValidateAndGetStride(new PixelSize(frame.PixelWidth, frame.PixelHeight));

        stream.Position = 0;
        BitmapImage image = new();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        // Bound both dimensions without stretching portrait or panoramic images.
        if ((double)frame.PixelWidth / frame.PixelHeight >= 224.0 / 140)
        {
            image.DecodePixelWidth = Math.Min(224, frame.PixelWidth);
        }
        else
        {
            image.DecodePixelHeight = Math.Min(140, frame.PixelHeight);
        }
        image.EndInit();
        image.Freeze();
        token.ThrowIfCancellationRequested();
        return image;
    }
}
