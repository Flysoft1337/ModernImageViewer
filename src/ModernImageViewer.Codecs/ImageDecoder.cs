using System.IO;

using ModernImageViewer.Application.Images;
using ModernImageViewer.Codecs.Wic;
using ModernImageViewer.Imaging;

using SkiaSharp;

namespace ModernImageViewer.Codecs;

public sealed class ImageDecoder : IPreviewImageDecoder, IThumbnailDecoder
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
        Span<byte> signature = stackalloc byte[12];
        int read = stream.ReadAtLeast(signature, signature.Length, throwOnEndOfStream: false);
        bool webP = read == signature.Length && signature[..4].SequenceEqual("RIFF"u8)
            && signature[8..].SequenceEqual("WEBP"u8);
        stream.Position = 0;
        if (!webP)
        {
            // Do not initialize Skia's native codec on the JPEG/PNG startup path.
            return _wic.Decode(stream, maximumSize, cancellationToken, maximumDecodedBytes);
        }
        return DecodeWebP(stream, maximumSize, maximumDecodedBytes, cancellationToken);
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
        PixelSize size = original;
        if (maximumSize is PixelSize maximum)
        {
            float scale = Math.Min(1, Math.Min((float)maximum.Width / original.Width,
                (float)maximum.Height / original.Height));
            SKSizeI scaled = codec.GetScaledDimensions(scale);
            size = new PixelSize(scaled.Width, scaled.Height);
            if (size.Width > maximum.Width || size.Height > maximum.Height)
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
        SKImageInfo info = new(size.Width, size.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        cancellationToken.ThrowIfCancellationRequested();
        SKCodecResult result = codec.GetPixels(info, pixels);
        cancellationToken.ThrowIfCancellationRequested();
        if (result != SKCodecResult.Success)
        {
            throw new ImageDecodeException(ImageOpenError.CorruptFile);
        }
        return new PixelBuffer(size, stride, pixels, sourceSize: original);
    }
}
