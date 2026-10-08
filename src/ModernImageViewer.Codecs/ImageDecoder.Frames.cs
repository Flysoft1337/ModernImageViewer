using System.IO;
using System.Runtime.InteropServices;

using ModernImageViewer.Application.Images;
using ModernImageViewer.Codecs.Frames;
using ModernImageViewer.Imaging;

using SkiaSharp;

namespace ModernImageViewer.Codecs;

public sealed partial class ImageDecoder
{
    private static PixelBuffer DecodeWebPRepresentative(Stream stream, WebPAnimationData animation,
        PixelSize? maximumSize, long? maximumDecodedBytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using SKManagedStream codecStream = new(stream, disposeManagedStream: false);
        using SKCodec codec = SKCodec.Create(codecStream) ?? throw new ImageDecodeException(ImageOpenError.CorruptFile);
        if (codec.EncodedFormat != SKEncodedImageFormat.Webp || codec.Info.Width != animation.Canvas.Width
            || codec.Info.Height != animation.Canvas.Height) { throw new ImageDecodeException(ImageOpenError.CorruptFile); }
        ushort orientation = (ushort)codec.EncodedOrigin;
        if (orientation is < 1 or > 8) { orientation = 1; }
        PixelSize source = animation.Canvas;
        PixelSize orientedSource = orientation >= 5 ? new(source.Height, source.Width) : source;
        long budget = Math.Min(maximumDecodedBytes ?? ImageFrameLimits.MaximumFrameBytes, ImageFrameLimits.MaximumFrameBytes);
        PixelSize size = source;
        if (maximumSize is PixelSize maximum)
        {
            PixelSize rawMaximum = orientation >= 5 ? new(maximum.Height, maximum.Width) : maximum;
            size = ImageFrameLimits.Fit(source, rawMaximum, budget);
        }
        else if (source.PixelCount * 4 > budget) { throw new ImageSizeLimitExceededException(); }
        using SKColorSpace? sourceColorSpace = codec.Info.ColorSpace;
        animation.ConvertBackground(sourceColorSpace);
        byte[] pixels = animation.Decode(stream, 0, size, null, -1, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        PixelSize outputSize = PixelOrientation.ApplyInPlace(pixels, size, orientation, cancellationToken);
        return new PixelBuffer(outputSize, checked(outputSize.Width * 4), pixels,
            ImageMetadata.Empty with { Orientation = orientation }, orientedSource);
    }

    internal static PixelBuffer DecodeGifRepresentative(Stream stream, PixelSize? maximumSize,
        long? maximumDecodedBytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using SKManagedStream codecStream = new(stream, disposeManagedStream: false);
        using SKCodec codec = SKCodec.Create(codecStream) ?? throw new ImageDecodeException(ImageOpenError.CorruptFile);
        if (codec.EncodedFormat != SKEncodedImageFormat.Gif)
        {
            throw new ImageDecodeException(ImageOpenError.UnsupportedFormat);
        }
        PixelSize source = new(codec.Info.Width, codec.Info.Height);
        ImageDecodeLimits.Default.ValidateAndGetStride(source);
        PixelSize size = source;
        if (maximumSize is PixelSize maximum)
        {
            float scale = Math.Min(1, Math.Min((float)maximum.Width / source.Width, (float)maximum.Height / source.Height));
            SKSizeI dimensions = codec.GetScaledDimensions(scale);
            size = new PixelSize(dimensions.Width, dimensions.Height);
            if (size.Width > maximum.Width || size.Height > maximum.Height)
            {
                throw new ImageDecodeException(ImageOpenError.UnsupportedFormat);
            }
        }
        int stride = ImageDecodeLimits.Default.ValidateAndGetStride(size);
        long bytes = (long)stride * size.Height;
        if (maximumDecodedBytes is { } budget && bytes > budget) { throw new ImageSizeLimitExceededException(); }
        // A representative uses no animation state or frame table. Scaling can still allocate a
        // source-sized native workspace; the output budget and playback admission cap do not bound it.
        byte[] pixels = GC.AllocateUninitializedArray<byte>(checked((int)bytes), pinned: true);
        using SKColorSpace srgb = SKColorSpace.CreateSrgb();
        SKImageInfo info = new(size.Width, size.Height, SKColorType.Bgra8888, SKAlphaType.Premul, srgb);
        cancellationToken.ThrowIfCancellationRequested();
        GCHandle pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        SKCodecResult result;
        try { result = codec.GetPixels(info, pin.AddrOfPinnedObject(), stride, new SKCodecOptions(0, -1)); }
        finally { pin.Free(); }
        cancellationToken.ThrowIfCancellationRequested();
        if (result != SKCodecResult.Success) { throw new ImageDecodeException(ImageOpenError.CorruptFile); }
        return new PixelBuffer(size, stride, pixels, sourceSize: source);
    }

    public async Task<IImageFrameSession?> TryOpenFrameSessionAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string extension = Path.GetExtension(path);
        if (!extension.Equals(".gif", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".webp", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".tif", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".tiff", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        byte[] signature = await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using FileStream file = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            byte[] header = new byte[12];
            int length = file.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
            cancellationToken.ThrowIfCancellationRequested();
            return header[..length];
        }, cancellationToken).ConfigureAwait(false);
        if ((signature.Length >= 6 && (signature.AsSpan(0, 6).SequenceEqual("GIF87a"u8)
                || signature.AsSpan(0, 6).SequenceEqual("GIF89a"u8)))
            || (signature.Length == 12 && signature.AsSpan(0, 4).SequenceEqual("RIFF"u8)
                && signature.AsSpan(8, 4).SequenceEqual("WEBP"u8)))
        {
            return await SkiaImageFrameSession.TryOpenAsync(path, cancellationToken).ConfigureAwait(false);
        }
        if (signature.Length >= 4 && (signature.AsSpan(0, 4).SequenceEqual("II\x2a\0"u8)
            || signature.AsSpan(0, 4).SequenceEqual("MM\0\x2a"u8)
            || signature.AsSpan(0, 4).SequenceEqual("II\x2b\0"u8)
            || signature.AsSpan(0, 4).SequenceEqual("MM\0\x2b"u8)))
        {
            return await TiffImageFrameSession.TryOpenAsync(path, cancellationToken).ConfigureAwait(false);
        }
        return null;
    }
}
