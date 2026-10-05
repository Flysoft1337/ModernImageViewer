using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

using ModernImageViewer.Application.Images;
using ModernImageViewer.Imaging;

using ImageMetadata = ModernImageViewer.Imaging.ImageMetadata;

namespace ModernImageViewer.Codecs.Wic;

public sealed class WicImageDecoder : IImageDecoder
{
    internal static readonly SemaphoreSlim DecodeSlot = new(1);
    private readonly ImageDecodeLimits _limits = ImageDecodeLimits.Default;
    private static readonly HashSet<Guid> Containers =
    [
        new("19E4A5AA-5662-4FC5-A0C0-1758028E1057"), // JPEG
        new("1B7CFAF4-713F-473C-BBCD-6137425FAEAF"), // PNG
        new("0AF1D87E-FCFE-4188-BDEB-A7906471CBE3"), // BMP
        new("1F8A5601-7D4D-4CBD-9C82-1BC8D4EEB9A5"), // GIF
        new("163BCC30-E2E9-4F0B-961D-A3E9FDB788A3"), // TIFF
        new("A3A860C4-338F-4C17-919A-FBA4B5628F21"), // ICO
        new("57A37CAA-367A-4540-916B-F183C5093A4B"), // JPEG XR / HD Photo
    ];

    public async Task<PixelBuffer> DecodeAsync(string path, CancellationToken cancellationToken)
    {
        // Native work cannot always stop midway. Bound full-resolution allocations.
        await DecodeSlot.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(() => Decode(path, null, cancellationToken), cancellationToken);
        }
        finally
        {
            DecodeSlot.Release();
        }
    }

    private PixelBuffer Decode(string path, PixelSize? maximumSize, CancellationToken cancellationToken)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Decode(stream, maximumSize, cancellationToken);
    }

    internal DecodedImageRegion DecodeRegion(Stream stream, PixelRect region, PixelSize expectedSourceSize,
        long maximumDecodedBytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (region.Width <= 0 || region.Height <= 0 || region.Width > 2048 || region.Height > 2048
            || maximumDecodedBytes <= 0 || (long)region.Width * region.Height * 4 > maximumDecodedBytes)
        {
            throw new ImageSizeLimitExceededException();
        }
        try
        {
            BitmapDecoder decoder = BitmapDecoder.Create(stream,
                BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            if (decoder.Frames.Count == 0 || !Containers.Contains(decoder.CodecInfo.ContainerFormat))
            {
                throw new ImageDecodeException(ImageOpenError.UnsupportedFormat);
            }
            BitmapFrame frame = decoder.Frames[0];
            PixelSize rawSize = new(frame.PixelWidth, frame.PixelHeight);
            _limits.ValidateAndGetStride(rawSize);
            ImageMetadata metadata = WicMetadataReader.Read(frame);
            PixelSize orientedSize = metadata.Orientation >= 5 ? new PixelSize(rawSize.Height, rawSize.Width) : rawSize;
            if (orientedSize != expectedSourceSize || region.Right > orientedSize.Width || region.Bottom > orientedSize.Height)
            {
                // An image replaced since its preview was decoded must not produce a mismatched overlay.
                throw new ImageDecodeException(ImageOpenError.CorruptFile);
            }
            PixelRect rawBounds = RegionOrientation.ToRawBounds(region, rawSize, metadata.Orientation);
            int rawStride = _limits.ValidateAndGetStride(rawBounds.Size);
            int outputStride = _limits.ValidateAndGetStride(region.Size);
            BitmapSource converted = frame.Format == PixelFormats.Pbgra32
                ? frame : new FormatConvertedBitmap(frame, PixelFormats.Pbgra32, null, 0);
            // Copy only the requested rectangle. Native codecs may still decode internally outside it.
            byte[] rawPixels = GC.AllocateUninitializedArray<byte>(checked(rawStride * rawBounds.Height), pinned: true);
            cancellationToken.ThrowIfCancellationRequested();
            converted.CopyPixels(new Int32Rect(rawBounds.X, rawBounds.Y, rawBounds.Width, rawBounds.Height), rawPixels, rawStride, 0);
            cancellationToken.ThrowIfCancellationRequested();
            byte[] output = rawPixels;
            if (metadata.Orientation != 1)
            {
                // A second bounded ROI buffer is needed for orientation; never allocate full-image BGRA.
                output = GC.AllocateUninitializedArray<byte>(checked(outputStride * region.Height), pinned: true);
                RegionOrientation.CopyOriented(rawPixels, rawStride, rawBounds, region, rawSize,
                    metadata.Orientation, output, outputStride, cancellationToken);
            }
            return new DecodedImageRegion(new PixelBuffer(region.Size, outputStride, output, metadata, orientedSize), region);
        }
        catch (ImageDecodeException)
        {
            throw;
        }
        catch (FileFormatException exception)
        {
            throw new ImageDecodeException(ImageOpenError.CorruptFile, exception);
        }
        catch (NotSupportedException exception)
        {
            throw new ImageDecodeException(ImageOpenError.UnsupportedFormat, exception);
        }
    }

    internal PixelBuffer Decode(Stream stream, PixelSize? maximumSize, CancellationToken cancellationToken, long? maximumDecodedBytes = null, ushort? orientationOverride = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            BitmapDecoder decoder = BitmapDecoder.Create(stream,
                BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.DelayCreation,
                BitmapCacheOption.None);
            if (decoder.Frames.Count == 0 || !Containers.Contains(decoder.CodecInfo.ContainerFormat))
            {
                throw new ImageDecodeException(ImageOpenError.UnsupportedFormat);
            }

            // Animation and multipage playback are separate features: display the first frame.
            BitmapFrame frame = decoder.Frames[0];
            PixelSize originalSize = new(frame.PixelWidth, frame.PixelHeight);
            _limits.ValidateAndGetStride(originalSize);
            ImageMetadata metadata = WicMetadataReader.Read(frame);
            if (orientationOverride is >= 1 and <= 8)
            {
                metadata = metadata with { Orientation = orientationOverride.Value };
            }
            PixelSize orientedSize = metadata.Orientation >= 5
                ? new PixelSize(originalSize.Height, originalSize.Width) : originalSize;
            if (maximumSize is null && maximumDecodedBytes is { } originalBudget
                && orientedSize.PixelCount * 4 > originalBudget)
            {
                throw new ImageSizeLimitExceededException();
            }
            cancellationToken.ThrowIfCancellationRequested();
            BitmapSource source = frame;
            if (maximumSize is PixelSize maximum)
            {
                PixelSize decodeMaximum = metadata.Orientation >= 5
                    ? new PixelSize(maximum.Height, maximum.Width) : maximum;
                double scale = Math.Min(1, Math.Min((double)decodeMaximum.Width / originalSize.Width,
                    (double)decodeMaximum.Height / originalSize.Height));
                int width = Math.Max(1, (int)Math.Floor(originalSize.Width * scale));
                int height = Math.Max(1, (int)Math.Floor(originalSize.Height * scale));
                stream.Position = 0;
                BitmapImage thumbnail = new();
                thumbnail.BeginInit();
                thumbnail.CacheOption = BitmapCacheOption.OnLoad;
                thumbnail.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
                thumbnail.StreamSource = stream;
                thumbnail.DecodePixelWidth = width;
                thumbnail.DecodePixelHeight = height;
                thumbnail.EndInit();
                source = thumbnail;
            }
            if (metadata.Orientation != 1)
            {
                var matrix = ImageOrientation.GetMatrix(metadata.Orientation);
                source = new TransformedBitmap(source, new MatrixTransform(
                    matrix.M11, matrix.M12, matrix.M21, matrix.M22, 0, 0));
            }

            PixelSize size = new(source.PixelWidth, source.PixelHeight);
            if (maximumSize is PixelSize bound && (size.Width > bound.Width || size.Height > bound.Height))
            {
                throw new ImageDecodeException(ImageOpenError.UnsupportedFormat);
            }
            int stride = _limits.ValidateAndGetStride(size);
            if (maximumDecodedBytes is { } budget && (long)stride * size.Height > budget)
            {
                throw new ImageSizeLimitExceededException();
            }
            byte[] pixels = GC.AllocateUninitializedArray<byte>(checked(stride * size.Height), pinned: true);
            BitmapSource converted = source.Format == PixelFormats.Pbgra32
                ? source : new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
            cancellationToken.ThrowIfCancellationRequested();
            converted.CopyPixels(pixels, stride, 0);
            cancellationToken.ThrowIfCancellationRequested();
            return new PixelBuffer(size, stride, pixels, metadata, orientedSize);
        }
        catch (ImageDecodeException)
        {
            throw;
        }
        catch (FileFormatException exception)
        {
            throw new ImageDecodeException(ImageOpenError.CorruptFile, exception);
        }
        catch (NotSupportedException exception)
        {
            throw new ImageDecodeException(ImageOpenError.UnsupportedFormat, exception);
        }
    }
}
