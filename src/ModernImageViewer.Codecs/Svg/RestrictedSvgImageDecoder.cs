using System.IO;
using System.Runtime.InteropServices;
using System.Xml;
using System.Xml.Linq;

using global::Svg.Skia;

using ModernImageViewer.Application.Images;
using ModernImageViewer.Imaging;

using SkiaSharp;

namespace ModernImageViewer.Codecs.Svg;

public static class RestrictedSvgImageDecoder
{
    private const long MaximumOpacityLayerBytes = 32 * 1024 * 1024;

    public static PixelBuffer Decode(Stream stream, PixelSize? maximumSize, long? maximumDecodedBytes, CancellationToken cancellationToken)
    {
        using SKSvg svg = Load(stream, cancellationToken, out int maximumOpacityDepth);
        SKPicture picture = svg.Picture ?? throw new ImageDecodeException(ImageOpenError.CorruptFile);
        PixelSize original = GetSourceSize(picture);
        PixelSize size = original;
        if (maximumSize is { } maximum)
        {
            double scale = Math.Min(1, Math.Min((double)maximum.Width / original.Width, (double)maximum.Height / original.Height));
            size = new PixelSize(Math.Max(1, (int)Math.Floor(original.Width * scale)), Math.Max(1, (int)Math.Floor(original.Height * scale)));
        }
        return Render(picture, size, original, maximumDecodedBytes, maximumOpacityDepth, cancellationToken);
    }

    private static SKSvg Load(Stream stream, CancellationToken cancellationToken, out int maximumOpacityDepth)
    {
        XDocument document = SvgInputPolicy.Read(stream, cancellationToken, out maximumOpacityDepth);
        SKSvg svg = new();
        try
        {
            svg.Settings.EnableJavaScript = false;
            svg.Settings.EnableExternalJavaScript = false;
            svg.Settings.EnableSvgFonts = false;
            svg.Settings.EnableTextReferences = false;
            svg.Settings.EnableFilterBackgroundInputs = false;
            svg.Settings.StandaloneViewport = new SKRect(0, 0, 300, 150);
            using XmlReader reader = document.CreateReader();
            if (svg.Load(reader) is null)
            {
                throw new ImageDecodeException(ImageOpenError.CorruptFile);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return svg;
        }
        catch
        {
            svg.Dispose();
            throw;
        }
    }

    private static PixelSize GetSourceSize(SKPicture picture)
    {
        SKRect bounds = picture.CullRect;
        if (!float.IsFinite(bounds.Width) || !float.IsFinite(bounds.Height)
            || bounds.Width <= 0 || bounds.Height <= 0 || bounds.Width > 32_768 || bounds.Height > 32_768)
        {
            throw new ImageSizeLimitExceededException();
        }
        PixelSize size = new((int)Math.Ceiling(bounds.Width), (int)Math.Ceiling(bounds.Height));
        ImageDecodeLimits.Default.ValidateAndGetStride(size);
        return size;
    }

    private static PixelBuffer Render(SKPicture picture, PixelSize size, PixelSize sourceSize,
        long? maximumDecodedBytes, int maximumOpacityDepth, CancellationToken cancellationToken)
    {
        int stride = ImageDecodeLimits.Default.ValidateAndGetStride(size);
        long bytes = (long)stride * size.Height;
        if (maximumDecodedBytes is { } budget && bytes > budget)
        {
            throw new ImageSizeLimitExceededException();
        }
        // Every supported opacity layer is conservatively charged at the full clipped target size,
        // including shape layers. Do not rely on Skia's optional single-leaf/group optimizations.
        if (bytes * maximumOpacityDepth > MaximumOpacityLayerBytes)
        {
            throw new ImageSizeLimitExceededException();
        }
        cancellationToken.ThrowIfCancellationRequested();
        byte[] pixels = GC.AllocateUninitializedArray<byte>(checked((int)bytes), pinned: true);
        GCHandle handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            using SKSurface? surface = SKSurface.Create(new SKImageInfo(size.Width, size.Height, SKColorType.Bgra8888, SKAlphaType.Premul), handle.AddrOfPinnedObject(), stride);
            if (surface is null)
            {
                throw new ImageSizeLimitExceededException();
            }
            SKCanvas canvas = surface.Canvas;
            canvas.Clear(SKColors.Transparent);
            SKRect bounds = picture.CullRect;
            canvas.Scale((float)size.Width / sourceSize.Width, (float)size.Height / sourceSize.Height);
            canvas.Translate(-bounds.Left, -bounds.Top);
            canvas.DrawPicture(picture);
            canvas.Flush();
            cancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            handle.Free();
        }
        return new PixelBuffer(size, stride, pixels, sourceSize: sourceSize);
    }
}
