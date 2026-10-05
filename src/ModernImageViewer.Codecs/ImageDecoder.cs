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

public sealed class ImageDecoder : IPreviewImageDecoder, IThumbnailDecoder, IRegionImageDecoder, IPrefetchImageDecoder
{
    private static readonly SemaphoreSlim ThumbnailSlots = new(2);
    private readonly WicImageDecoder _wic = new();

    public Task<PixelBuffer> DecodeAsync(string path, CancellationToken cancellationToken) =>
        DecodeScheduledAsync(path, null, WicImageDecoder.DecodeSlot, cancellationToken);

    public Task<PixelBuffer> DecodePreviewAsync(string path, PixelSize maximumSize, CancellationToken cancellationToken) =>
        DecodeScheduledAsync(path, maximumSize, WicImageDecoder.DecodeSlot, cancellationToken);

    public Task<PixelBuffer> DecodeDetailAsync(string path, long maximumDecodedBytes, CancellationToken cancellationToken) =>
        DecodeScheduledAsync(path, null, WicImageDecoder.DecodeSlot, cancellationToken, maximumDecodedBytes);

    public Task<PixelBuffer> DecodeThumbnailAsync(string path, PixelSize maximumSize, CancellationToken cancellationToken) =>
        DecodeScheduledAsync(path, maximumSize, ThumbnailSlots, cancellationToken);

    public async Task<PixelBuffer?> TryDecodePreviewAsync(string path, PixelSize maximumSize, CancellationToken cancellationToken)
    {
        // Idle work never queues behind a foreground decode or creates another full-image slot.
        if (!await WicImageDecoder.DecodeSlot.WaitAsync(0, cancellationToken))
        {
            return null;
        }
        try
        {
            return await Task.Run(() => Decode(path, maximumSize, null, cancellationToken), cancellationToken);
        }
        finally
        {
            WicImageDecoder.DecodeSlot.Release();
        }
    }

    public async Task<DecodedImageRegion> DecodeRegionAsync(string path, PixelRect region,
        PixelSize expectedSourceSize, long maximumDecodedBytes, CancellationToken cancellationToken)
    {
        await WicImageDecoder.DecodeSlot.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
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
                return _wic.DecodeRegion(stream, region, expectedSourceSize, maximumDecodedBytes, cancellationToken);
            }, cancellationToken);
        }
        finally
        {
            WicImageDecoder.DecodeSlot.Release();
        }
    }

    private async Task<PixelBuffer> DecodeScheduledAsync(string path, PixelSize? maximumSize,
        SemaphoreSlim slots, CancellationToken cancellationToken, long? maximumDecodedBytes = null)
    {
        await slots.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(() => Decode(path, maximumSize, maximumDecodedBytes, cancellationToken), cancellationToken);
        }
        finally
        {
            slots.Release();
        }
    }

    private PixelBuffer Decode(string path, PixelSize? maximumSize, long? maximumDecodedBytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        ImageFileStamp stamp = new(stream.Length, File.GetLastWriteTimeUtc(path));
        using PixelBuffer decoded = DecodeStream(path, stream, maximumSize, maximumDecodedBytes, cancellationToken);
        if (!MemoryMarshal.TryGetArray(decoded.Pixels, out ArraySegment<byte> pixels) || pixels.Array is null || pixels.Offset != 0)
        {
            throw new ImageDecodeException(ImageOpenError.DecodeFailed);
        }
        // Transfer the same immutable storage into the published result; no pixel-array copy.
        return new PixelBuffer(decoded.Size, decoded.Stride, pixels.Array, decoded.Metadata, decoded.SourceSize, stamp);
    }

    private PixelBuffer DecodeStream(string path, Stream stream, PixelSize? maximumSize,
        long? maximumDecodedBytes, CancellationToken cancellationToken)
    {
        if (SupportedImageFormats.IsRawExtension(Path.GetExtension(path)))
        {
            return RawPreviewImageDecoder.Decode(path, maximumSize, maximumDecodedBytes, cancellationToken);
        }
        if (string.Equals(Path.GetExtension(path), ".svg", StringComparison.OrdinalIgnoreCase))
        {
            return RestrictedSvgImageDecoder.Decode(stream, maximumSize, maximumDecodedBytes, cancellationToken);
        }
        if (HeifContainer.TryDetect(stream, out bool avif))
        {
            return HeifImageDecoder.Decode(stream, avif, maximumSize, maximumDecodedBytes, cancellationToken);
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

    private static PixelBuffer DecodeWebP(Stream stream, PixelSize? maximumSize, long? maximumDecodedBytes, CancellationToken cancellationToken)
    {
        using SKCodec? codec = SKCodec.Create(stream);
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
