using System.IO;

using ImageMagick;
using ImageMagick.Configuration;
using ImageMagick.Formats;

using ModernImageViewer.Application.Images;
using ModernImageViewer.Imaging;

namespace ModernImageViewer.Codecs.Modern;

internal static class HeifImageDecoder
{
    internal const long MaximumInputBytes = 128L * 1024 * 1024;
    internal const long MaximumSourcePixels = 32_000_000;
    private static readonly SemaphoreSlim DecodeSlot = new(1);
    private static readonly Lazy<bool> Initialized = new(Initialize);

    internal static PixelBuffer Decode(Stream stream, bool avif, PixelSize? maximumSize,
        long? maximumDecodedBytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (stream.Length > MaximumInputBytes) { throw new ImageSizeLimitExceededException(); }
        if (!HeifContainer.TryDetect(stream, out bool actualAvif) || actualAvif != avif)
        {
            throw new ImageDecodeException(ImageOpenError.CorruptFile);
        }
        if (HeifContainer.HasSequenceBrand(stream))
        {
            throw new ImageDecodeException(ImageOpenError.UnsupportedFormat);
        }
        DecodeSlot.Wait(cancellationToken);
        try
        {
            try { _ = Initialized.Value; }
            catch (IOException) { throw new ImageDecodeException(ImageOpenError.UnsupportedFormat); }
            catch (UnauthorizedAccessException) { throw new ImageDecodeException(ImageOpenError.UnsupportedFormat); }
            cancellationToken.ThrowIfCancellationRequested();
            MagickReadSettings settings = new(new HeicReadDefines
            {
                PreserveOrientation = false,
                MaxItems = 512,
                MaxChildrenPerBox = 512,
                MaxIlocExtentsPerItem = 512,
                MaxNumberOfTiles = 4096,
            })
            {
                Format = avif ? MagickFormat.Avif : MagickFormat.Heic,
                FrameIndex = 0,
                FrameCount = 1,
                SyncImageWithExifProfile = false,
            };
            using MagickImage image = new();
            image.Progress += (_, args) => args.Cancel = cancellationToken.IsCancellationRequested;
            image.Ping(stream, settings);
            cancellationToken.ThrowIfCancellationRequested();
            PixelSize sourceSize = GetSize(image);
            ValidateSourceSize(sourceSize);
            PixelSize size = Fit(sourceSize, maximumSize);
            int stride = ImageDecodeLimits.Default.ValidateAndGetStride(size);
            if (maximumDecodedBytes is { } budget && (long)stride * size.Height > budget)
            {
                throw new ImageSizeLimitExceededException();
            }

            stream.Position = 0;
            // libheif applies container irot/imir. EXIF must not rotate the resulting pixels again.
            // The native codec reads the full source before resizing; target output is bounded,
            // but resource limits below are NOT a hard cap on delegate/process memory.
            image.Read(stream, settings);
            cancellationToken.ThrowIfCancellationRequested();
            if (GetSize(image) != sourceSize) { throw new ImageDecodeException(ImageOpenError.CorruptFile); }
            if (size != sourceSize) { image.Resize((uint)size.Width, (uint)size.Height); }
            cancellationToken.ThrowIfCancellationRequested();
            size = GetSize(image);
            stride = ImageDecodeLimits.Default.ValidateAndGetStride(size);
            if (maximumSize is { } bound && (size.Width > bound.Width || size.Height > bound.Height)
                || maximumDecodedBytes is { } finalBudget && (long)stride * size.Height > finalBudget)
            {
                throw new ImageSizeLimitExceededException();
            }
            if (image.GetColorProfile() is not null) { image.TransformColorSpace(ColorProfiles.SRGB); }
            else if (image.ColorSpace != ColorSpace.sRGB) { image.ColorSpace = ColorSpace.sRGB; }
            cancellationToken.ThrowIfCancellationRequested();
            using IPixelCollection<byte> collection = image.GetPixels();
            byte[] pixels = GC.AllocateUninitializedArray<byte>(checked(stride * size.Height), pinned: true);
            // Export one row at a time: Magick's export API otherwise creates another complete
            // native target buffer before copying it into the managed BGRA array.
            for (int row = 0; row < size.Height; row++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                byte[] rowPixels = collection.ToByteArray(0, row, (uint)size.Width, 1, PixelMapping.BGRA)
                    ?? throw new ImageDecodeException(ImageOpenError.CorruptFile);
                if (rowPixels.Length != stride) { throw new ImageDecodeException(ImageOpenError.CorruptFile); }
                Premultiply(rowPixels, stride, cancellationToken);
                rowPixels.CopyTo(pixels, row * stride);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return new PixelBuffer(size, stride, pixels, sourceSize: sourceSize);
        }
        catch (MagickException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (MagickResourceLimitErrorException) { throw new ImageSizeLimitExceededException(); }
        catch (MagickCacheErrorException) { throw new ImageSizeLimitExceededException(); }
        catch (MagickMissingDelegateErrorException) { throw new ImageDecodeException(ImageOpenError.UnsupportedFormat); }
        catch (MagickException) { throw new ImageDecodeException(ImageOpenError.CorruptFile); }
        catch (DllNotFoundException) { throw new ImageDecodeException(ImageOpenError.UnsupportedFormat); }
        catch (BadImageFormatException) { throw new ImageDecodeException(ImageOpenError.UnsupportedFormat); }
        catch (EntryPointNotFoundException) { throw new ImageDecodeException(ImageOpenError.UnsupportedFormat); }
        catch (TypeInitializationException) { throw new ImageDecodeException(ImageOpenError.UnsupportedFormat); }
        finally { DecodeSlot.Release(); }
    }

    private static bool Initialize()
    {
        IConfigurationFiles configuration = ConfigurationFiles.Default;
        configuration.Policy.Data = """
            <policymap>
              <policy domain="delegate" rights="none" pattern="*"/>
              <policy domain="filter" rights="none" pattern="*"/>
              <policy domain="coder" rights="none" pattern="*"/>
              <policy domain="coder" rights="read" pattern="{AVIF,HEIC}"/>
              <policy domain="path" rights="none" pattern="@*"/>
              <policy domain="resource" name="map" value="0"/>
            </policymap>
            """;
        string configurationDirectory = MagickNET.Initialize(configuration);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Directory.Delete(configurationDirectory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        };
        ResourceLimits.Memory = 192 * 1024 * 1024;
        ResourceLimits.Disk = 0;
        ResourceLimits.Thread = 1;
        // Includes temporary internal image caches, rather than exposed image/frame count.
        ResourceLimits.ListLength = 16;
        ResourceLimits.Width = (ulong)ImageDecodeLimits.Default.MaximumDimension;
        ResourceLimits.Height = (ulong)ImageDecodeLimits.Default.MaximumDimension;
        ResourceLimits.MaxMemoryRequest = 128 * 1024 * 1024;
        ResourceLimits.MaxProfileSize = 4 * 1024 * 1024;
        return true;
    }

    private static PixelSize GetSize(MagickImage image) => new(checked((int)image.Width), checked((int)image.Height));

    internal static void ValidateSourceSize(PixelSize size)
    {
        ImageDecodeLimits.Default.ValidateAndGetStride(size);
        if (size.PixelCount > MaximumSourcePixels) { throw new ImageSizeLimitExceededException(); }
    }

    private static PixelSize Fit(PixelSize source, PixelSize? maximum)
    {
        if (maximum is not { } bound) { return source; }
        double scale = Math.Min(1, Math.Min((double)bound.Width / source.Width, (double)bound.Height / source.Height));
        return new PixelSize(Math.Max(1, (int)Math.Floor(source.Width * scale)), Math.Max(1, (int)Math.Floor(source.Height * scale)));
    }

    internal static void Premultiply(byte[] pixels, int stride, CancellationToken cancellationToken)
    {
        for (int row = 0; row < pixels.Length; row += stride)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int offset = row; offset < row + stride; offset += 4)
            {
                int alpha = pixels[offset + 3];
                if (alpha == 255) { continue; }
                pixels[offset] = (byte)((pixels[offset] * alpha + 127) / 255);
                pixels[offset + 1] = (byte)((pixels[offset + 1] * alpha + 127) / 255);
                pixels[offset + 2] = (byte)((pixels[offset + 2] * alpha + 127) / 255);
            }
        }
    }
}





