using System.IO;
using System.Runtime.InteropServices;

using ModernImageViewer.Application.Images;
using ModernImageViewer.Codecs.Modern;
using ModernImageViewer.Codecs.Raw;
using ModernImageViewer.Codecs.Svg;
using ModernImageViewer.Codecs.Wic;
using ModernImageViewer.Imaging;

using SkiaSharp;

namespace ModernImageViewer.Codecs;

public sealed partial class ImageDecoder : IPreviewImageDecoder, IThumbnailDecoder, IRegionImageDecoder, IPrefetchImageDecoder, IMemoryImageDecoder, IImageFrameDecoder
{
    private readonly WicImageDecoder _wic = new();

    public Task<PixelBuffer> DecodeAsync(string path, CancellationToken cancellationToken) =>
        DecodeScheduledAsync(path, null, DecodePriority.Foreground, cancellationToken);

    public Task<PixelBuffer> DecodePreviewAsync(string path, PixelSize maximumSize, CancellationToken cancellationToken) =>
        DecodeScheduledAsync(path, maximumSize, DecodePriority.Foreground, cancellationToken);

    public Task<PixelBuffer> DecodeDetailAsync(string path, long maximumDecodedBytes, CancellationToken cancellationToken) =>
        DecodeScheduledAsync(path, null, DecodePriority.Detail, cancellationToken, maximumDecodedBytes);

    public Task<PixelBuffer> DecodeThumbnailAsync(string path, PixelSize maximumSize, CancellationToken cancellationToken) =>
        DecodeScheduledAsync(path, maximumSize, DecodePriority.Thumbnail, cancellationToken);

    public Task<PixelBuffer?> TryDecodePreviewAsync(string path, PixelSize maximumSize, CancellationToken cancellationToken) =>
        WicImageDecoder.DecodeSlot.TryRunPrefetchAsync(() => Decode(path, maximumSize, null, DecodePriority.Thumbnail, cancellationToken), cancellationToken);

    public Task<DecodedImageRegion> DecodeRegionAsync(string path, PixelRect region,
        PixelSize expectedSourceSize, long maximumDecodedBytes, CancellationToken cancellationToken) =>
        WicImageDecoder.DecodeSlot.RunAsync(DecodePriority.Detail, () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                ImageFileStamp stamp = ReadFileStamp(path, stream);
                if (SupportedImageFormats.IsRawExtension(Path.GetExtension(path)))
                {
                    throw new ImageDecodeException(ImageOpenError.UnsupportedFormat);
                }
                if (string.Equals(Path.GetExtension(path), ".svg", StringComparison.OrdinalIgnoreCase))
                {
                    // Skia's curve antialiasing changes at local clip edges. Keep preview/full output
                    // until a region path can guarantee consistent compositing at those boundaries.
                    throw new ImageDecodeException(ImageOpenError.UnsupportedFormat);
                }
                if (IsWebP(stream))
                {
                    // Skia's WebP path cannot guarantee bounded region output without a full decode.
                    throw new ImageDecodeException(ImageOpenError.UnsupportedFormat);
                }
                if (HeifContainer.TryDetect(stream, out _))
                {
                    throw new ImageDecodeException(ImageOpenError.UnsupportedFormat);
                }
                using DecodedImageRegion decoded = _wic.DecodeRegion(stream, region, expectedSourceSize, maximumDecodedBytes, cancellationToken);
                ValidateFileStamp(path, stream, stamp);
                return new DecodedImageRegion(TransferWithFileStamp(decoded.Image, stamp), decoded.Bounds);
            }, cancellationToken);

    private Task<PixelBuffer> DecodeScheduledAsync(string path, PixelSize? maximumSize,
        DecodePriority priority, CancellationToken cancellationToken, long? maximumDecodedBytes = null) =>
        WicImageDecoder.DecodeSlot.RunAsync(priority, () => Decode(path, maximumSize, maximumDecodedBytes, priority, cancellationToken), cancellationToken);

    private PixelBuffer Decode(string path, PixelSize? maximumSize, long? maximumDecodedBytes, DecodePriority priority, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        ImageFileStamp stamp = ReadFileStamp(path, stream);
        using PixelBuffer decoded = DecodeStream(path, stream, maximumSize, maximumDecodedBytes, priority, cancellationToken);
        ValidateFileStamp(path, stream, stamp);
        return TransferWithFileStamp(decoded, stamp);
    }

    private static PixelBuffer TransferWithFileStamp(PixelBuffer decoded, ImageFileStamp stamp)
    {
        if (!MemoryMarshal.TryGetArray(decoded.Pixels, out ArraySegment<byte> pixels) || pixels.Array is null || pixels.Offset != 0)
        {
            throw new ImageDecodeException(ImageOpenError.DecodeFailed);
        }
        // Transfer the same immutable storage into the published result; no pixel-array copy.
        return new PixelBuffer(decoded.Size, decoded.Stride, pixels.Array, decoded.Metadata, decoded.SourceSize, stamp);
    }

    internal static ImageFileStamp ReadFileStamp(string path, FileStream stream) =>
        new(stream.Length, File.GetLastWriteTimeUtc(path));

    internal static void ValidateFileStamp(string path, FileStream stream, ImageFileStamp expected)
    {
        if (ReadFileStamp(path, stream) != expected)
        {
            throw new IOException("The source image changed during decoding.");
        }
    }

    private PixelBuffer DecodeStream(string path, Stream stream, PixelSize? maximumSize,
        long? maximumDecodedBytes, DecodePriority priority, CancellationToken cancellationToken)
    {
        if (SupportedImageFormats.IsRawExtension(Path.GetExtension(path)))
        {
            return RawPreviewImageDecoder.Decode(path, maximumSize, maximumDecodedBytes, priority, cancellationToken);
        }
        if (string.Equals(Path.GetExtension(path), ".svg", StringComparison.OrdinalIgnoreCase))
        {
            return RestrictedSvgImageDecoder.Decode(stream, maximumSize, maximumDecodedBytes, cancellationToken);
        }
        if (HeifContainer.TryDetect(stream, out bool avif))
        {
            return HeifImageDecoder.Decode(stream, avif, maximumSize, maximumDecodedBytes, priority, cancellationToken);
        }
        Span<byte> signature = stackalloc byte[6];
        int signatureLength = stream.ReadAtLeast(signature, signature.Length, throwOnEndOfStream: false);
        stream.Position = 0;
        if (signatureLength == signature.Length && (signature.SequenceEqual("GIF87a"u8) || signature.SequenceEqual("GIF89a"u8)))
        {
            return DecodeGifRepresentative(stream, maximumSize, maximumDecodedBytes, cancellationToken);
        }
        if (!IsWebP(stream))
        {
            // Do not initialize Skia's native codec on the JPEG/PNG startup path.
            return _wic.Decode(stream, maximumSize, cancellationToken, maximumDecodedBytes);
        }
        return DecodeWebP(stream, maximumSize, maximumDecodedBytes, cancellationToken);
    }

    private static bool IsWebP(Stream stream)
    {
        Span<byte> signature = stackalloc byte[12];
        int read = stream.ReadAtLeast(signature, signature.Length, throwOnEndOfStream: false);
        bool webP = read == signature.Length && signature[..4].SequenceEqual("RIFF"u8)
            && signature[8..].SequenceEqual("WEBP"u8);
        stream.Position = 0;
        return webP;
    }

    internal static PixelBuffer DecodeWebP(Stream stream, PixelSize? maximumSize, long? maximumDecodedBytes, CancellationToken cancellationToken)
    {
        // The caller owns the read lock through the final source-version check.
        using SKManagedStream codecStream = new(stream, disposeManagedStream: false);
        using SKCodec? codec = SKCodec.Create(codecStream);
        if (codec is null)
        {
            throw new ImageDecodeException(ImageOpenError.CorruptFile);
        }
        if (codec.EncodedFormat != SKEncodedImageFormat.Webp)
        {
            throw new ImageDecodeException(ImageOpenError.UnsupportedFormat);
        }
        PixelSize original = new(codec.Info.Width, codec.Info.Height);
        ImageDecodeLimits.Default.ValidateAndGetStride(original);
        ushort orientation = (ushort)codec.EncodedOrigin;
        if (orientation is < 1 or > 8) { orientation = 1; }
        PixelSize orientedSource = orientation >= 5 ? new(original.Height, original.Width) : original;
        PixelSize size = original;
        if (maximumSize is PixelSize maximum)
        {
            PixelSize rawMaximum = orientation >= 5 ? new(maximum.Height, maximum.Width) : maximum;
            float scale = Math.Min(1, Math.Min((float)rawMaximum.Width / original.Width,
                (float)rawMaximum.Height / original.Height));
            SKSizeI scaled = codec.GetScaledDimensions(scale);
            size = new PixelSize(scaled.Width, scaled.Height);
            if (size.Width > rawMaximum.Width || size.Height > rawMaximum.Height)
            {
                // A codec must honor the requested output bound; never silently decode full-size.
                throw new ImageDecodeException(ImageOpenError.UnsupportedFormat);
            }
        }
        int stride = ImageDecodeLimits.Default.ValidateAndGetStride(size);
        if (maximumDecodedBytes is { } budget && (long)stride * size.Height > budget)
        {
            throw new ImageSizeLimitExceededException();
        }
        byte[] pixels = GC.AllocateUninitializedArray<byte>(checked(stride * size.Height), pinned: true);
        using SKColorSpace srgb = SKColorSpace.CreateSrgb();
        SKImageInfo info = new(size.Width, size.Height, SKColorType.Bgra8888, SKAlphaType.Premul, srgb);
        cancellationToken.ThrowIfCancellationRequested();
        SKCodecResult result = codec.GetPixels(info, pixels);
        cancellationToken.ThrowIfCancellationRequested();
        if (result != SKCodecResult.Success)
        {
            throw new ImageDecodeException(ImageOpenError.CorruptFile);
        }
        PixelSize orientedSize = PixelOrientation.ApplyInPlace(pixels, size, orientation, cancellationToken);
        return new PixelBuffer(orientedSize, checked(orientedSize.Width * 4), pixels,
            metadata: ImageMetadata.Empty with { Orientation = orientation }, sourceSize: orientedSource);
    }
}
