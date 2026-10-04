using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

using ModernImageViewer.Imaging;

namespace ModernImageViewer.UI.Controls;

public sealed class ThumbnailImage : Image, IDisposable
{
    private const int CacheCapacity = 24;
    private static readonly string[] OrientationQueries = ["/app1/ifd/{ushort=274}", "/ifd/{ushort=274}"];
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

    private static BitmapSource DecodeThumbnail(string path, CancellationToken token)
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

        ushort orientation = 1;
        try
        {
            if (frame.Metadata is BitmapMetadata metadata)
            {
                foreach (string query in OrientationQueries)
                {
                    try
                    {
                        if (metadata.GetQuery(query) is ushort number && number is >= 1 and <= 8)
                        {
                            orientation = number;
                            break;
                        }
                    }
                    catch (Exception exception) when (exception is NotSupportedException or ArgumentException
                        or InvalidOperationException or IOException or System.Runtime.InteropServices.COMException)
                    { }
                }
            }
        }
        catch (Exception exception) when (exception is NotSupportedException or ArgumentException
            or InvalidOperationException or IOException or System.Runtime.InteropServices.COMException)
        { }

        stream.Position = 0;
        BitmapImage image = new();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        double displayWidth = orientation >= 5 ? frame.PixelHeight : frame.PixelWidth;
        double displayHeight = orientation >= 5 ? frame.PixelWidth : frame.PixelHeight;
        double scale = Math.Min(1, Math.Min(224 / displayWidth, 140 / displayHeight));
        image.DecodePixelWidth = Math.Max(1, (int)Math.Floor(frame.PixelWidth * scale));
        image.EndInit();
        image.Freeze();
        token.ThrowIfCancellationRequested();
        if (orientation == 1)
        {
            return image;
        }
        var matrix = ImageOrientation.GetMatrix(orientation);
        TransformedBitmap oriented = new(image, new MatrixTransform(matrix.M11, matrix.M12, matrix.M21, matrix.M22, 0, 0));
        oriented.Freeze();
        return oriented;
    }
}
