using System.IO;
using System.Runtime.InteropServices;

using ModernImageViewer.Application.Editing;
using ModernImageViewer.Application.Images;
using ModernImageViewer.Imaging;
using ModernImageViewer.Rendering.Editing;

using SkiaSharp;

namespace ModernImageViewer.Codecs.Editing;

public sealed class ImageExportService(IImageDecoder decoder) : IImageExportService
{
    public const long OutputByteLimit = 64L * 1024 * 1024;
    public const long SourceByteLimit = 64L * 1024 * 1024;
    private const long RegionByteLimit = 16L * 1024 * 1024;
    // Output + compositing layer + two conservative filter work surfaces. This is an
    // allocation guard, not a promise about Skia/native process peak memory.
    public const long EffectWorkingByteLimit = 128L * 1024 * 1024;
    private static readonly SemaphoreSlim ExportSlot = new(1, 1);

    public Task<byte[]?> GetSourceColorProfileAsync(string path, CancellationToken cancellationToken = default) =>
        Task.Run(() => ImageExportMetadata.ReadSource(path, preserve: false, cancellationToken).PixelProfile, cancellationToken);

    public async Task ExportAsync(ImageExportRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request);
        await ExportSlot.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await Task.Run(() => ExportCoreAsync(request, cancellationToken), cancellationToken).ConfigureAwait(false); }
        finally { ExportSlot.Release(); }
    }

    private async Task ExportCoreAsync(ImageExportRequest request, CancellationToken token)
    {
        string? source = request.SourcePath is null ? null : Path.GetFullPath(request.SourcePath);
        string destination = Path.GetFullPath(request.DestinationPath);
        string? temporary = null;
        string? metadataTemporary = null;
        try
        {
            // Keep this handle through commit: Windows denies writes/deletes through hard links,
            // junctions and alternative spellings too, rather than trusting a string comparison.
            using FileStream? sourceLock = source is null ? null : new(source, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (sourceLock is not null && ((request.ExpectedSourceLength is long length && sourceLock.Length != length)
                || (request.ExpectedSourceModifiedUtc is DateTime modified && File.GetLastWriteTimeUtc(source!) != modified)))
            {
                throw new ImageExportException(ImageExportError.SourceChanged);
            }
            if (File.Exists(destination)) { throw new ImageExportException(ImageExportError.DestinationExists); }
            token.ThrowIfCancellationRequested();
            ImageExportPixels? memorySource = request.MemorySourceIdentity is not null ? request.SourcePixels : null;
            using PixelBuffer? wholeSource = source is null ? null : TryReuseSourcePixels(request)
                ?? await DecodeWholeWithinBudgetAsync(source, request.Recipe, token).ConfigureAwait(false);
            if (memorySource is null && wholeSource is null && decoder is not IRegionImageDecoder)
            {
                throw new ImageExportException(ImageExportError.BudgetExceeded);
            }
            token.ThrowIfCancellationRequested();
            ImageExportMetadata.SourceInfo sourceInfo = ImageExportMetadata.ReadSource(source,
                request.MetadataMode == ImageExportMetadataMode.PreserveCamera, token);
            using SKColorSpace srgb = SKColorSpace.CreateSrgb();
            using SKColorSpace sourceColor = sourceInfo.PixelProfile is null ? SKColorSpace.CreateSrgb()
                : SKColorSpace.CreateIcc(sourceInfo.PixelProfile) ?? throw new ImageExportException(ImageExportError.DecodeFailed);
            PixelSize output = request.Recipe.OutputSize;
            using SKBitmap bitmap = new(new SKImageInfo(output.Width, output.Height, SKColorType.Bgra8888, SKAlphaType.Premul, srgb));
            if (bitmap.GetPixels() == IntPtr.Zero) { throw new ImageExportException(ImageExportError.BudgetExceeded); }
            using (SKCanvas canvas = new(bitmap))
            {
                canvas.Clear(request.Format == ImageExportFormat.Jpeg ? SKColors.White : SKColors.Transparent);
                using SKPaint? effects = request.Recipe.Adjustments.IsIdentity ? null : ImageEditEffects.CreatePaint(request.Recipe.Adjustments);
                if (effects is not null) { canvas.SaveLayer(new SKRect(0, 0, output.Width, output.Height), effects); }
                canvas.Save();
                var m = request.Recipe.GetMatrix();
                canvas.Concat(new SKMatrix((float)m.M11, (float)m.M21, (float)m.OffsetX,
                    (float)m.M12, (float)m.M22, (float)m.OffsetY, 0, 0, 1));
                canvas.ClipRect(ToRect(request.Recipe.Crop));
                if (memorySource is not null || wholeSource is not null)
                {
                    using SKBitmap pixels = memorySource is not null
                        ? AttachPixels(memorySource.Size, memorySource.Stride, memorySource.Pixels, srgb) : AttachPixels(wholeSource!, sourceColor);
                    if (memorySource is not null || wholeSource!.Size == wholeSource.SourceSize)
                    {
                        using SKBitmap cropped = new();
                        PixelRect crop = request.Recipe.Crop;
                        // A subset shares pixels but clamps sampling at the crop boundary.
                        if (!pixels.ExtractSubset(cropped, new SKRectI(crop.X, crop.Y, crop.Right, crop.Bottom)))
                        {
                            throw new ImageExportException(ImageExportError.DecodeFailed);
                        }
                        canvas.DrawBitmap(cropped, ToRect(crop), new SKSamplingOptions(SKFilterMode.Linear));
                    }
                    else
                    {
                        // Pre-shrinking is allowed only when exporting the entire source.
                        canvas.DrawBitmap(pixels, new SKRect(0, 0, request.Recipe.SourceSize.Width, request.Recipe.SourceSize.Height),
                            new SKSamplingOptions(SKFilterMode.Linear));
                    }
                }
                else
                {
                    if (decoder is not IRegionImageDecoder regions)
                    {
                        throw new ImageExportException(ImageExportError.BudgetExceeded);
                    }
                    PixelRect crop = request.Recipe.Crop;
                    // One-pixel overlap gives bilinear sampling its neighboring texels at tile edges.
                    for (int y = crop.Y; y < crop.Bottom; y += 2046)
                    {
                        for (int x = crop.X; x < crop.Right; x += 2046)
                        {
                            token.ThrowIfCancellationRequested();
                            PixelRect core = new(x, y, Math.Min(2046, crop.Right - x), Math.Min(2046, crop.Bottom - y));
                            int left = Math.Max(crop.X, x - 1);
                            int top = Math.Max(crop.Y, y - 1);
                            PixelRect padded = new(left, top, Math.Min(crop.Right, core.Right + 1) - left,
                                Math.Min(crop.Bottom, core.Bottom + 1) - top);
                            using DecodedRegionOwner owner = new(await regions.DecodeRegionAsync(source!, padded,
                                request.Recipe.SourceSize, RegionByteLimit, token).ConfigureAwait(false));
                            if (owner.Region.Bounds != padded || owner.Region.Image.SourceSize != request.Recipe.SourceSize
                                || owner.Region.Image.Size != padded.Size)
                            {
                                throw new ImageExportException(ImageExportError.SourceChanged);
                            }
                            using SKBitmap pixels = AttachPixels(owner.Region.Image, sourceColor);
                            canvas.Save();
                            canvas.ClipRect(ToRect(core));
                            canvas.DrawBitmap(pixels, ToRect(padded), new SKSamplingOptions(SKFilterMode.Linear));
                            canvas.Restore();
                        }
                    }
                }
                canvas.Restore();
                if (effects is not null) { canvas.Restore(); }
                token.ThrowIfCancellationRequested();
                ImageAnnotationRenderer.Draw(canvas, request.Recipe, bitmap);
            }
            token.ThrowIfCancellationRequested();
            temporary = Path.Combine(Path.GetDirectoryName(destination)!, $".miv-export-{Guid.NewGuid():N}.tmp");
            using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using SKPixmap pixels = bitmap.PeekPixels();
                // Lossless changes the codec mode, not merely the lossy quality setting.
                bool encoded = request.Format == ImageExportFormat.Webp
                    ? pixels.Encode(stream, new SKWebpEncoderOptions(request.WebpLossless
                        ? SKWebpEncoderCompression.Lossless : SKWebpEncoderCompression.Lossy, request.JpegQuality))
                    : bitmap.Encode(stream, request.Format == ImageExportFormat.Png ? SKEncodedImageFormat.Png : SKEncodedImageFormat.Jpeg,
                        request.JpegQuality);
                if (!encoded)
                {
                    throw new ImageExportException(ImageExportError.WriteFailed);
                }
                stream.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            byte[]? exif = ImageExportMetadata.CreateExif(sourceInfo.Metadata, output, request.MetadataMode);
            metadataTemporary = ImageExportMetadata.AddToEncodedFile(temporary, request.Format, output, exif, token);
            File.Delete(temporary);
            temporary = metadataTemporary;
            metadataTemporary = null;
            token.ThrowIfCancellationRequested();
            // Never replace an existing file, even if it appears after the save dialog closes.
            File.Move(temporary, destination, overwrite: false);
            temporary = null;
        }
        catch (OperationCanceledException) { throw; }
        catch (ImageExportException) { throw; }
        catch (OutOfMemoryException exception) { throw new ImageExportException(ImageExportError.BudgetExceeded, exception); }
        catch (FileNotFoundException exception) when (string.Equals(exception.FileName, source, StringComparison.OrdinalIgnoreCase))
        {
            throw new ImageExportException(ImageExportError.SourceChanged, exception);
        }
        catch (ImageDecodeException exception)
        {
            throw new ImageExportException(exception.Error == ImageOpenError.UnsupportedFormat
                ? ImageExportError.BudgetExceeded : ImageExportError.DecodeFailed, exception);
        }
        catch (ImageSizeLimitExceededException exception) { throw new ImageExportException(ImageExportError.BudgetExceeded, exception); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or System.Security.SecurityException)
        {
            throw new ImageExportException(File.Exists(destination) ? ImageExportError.DestinationExists : ImageExportError.WriteFailed, exception);
        }
        finally
        {
            foreach (string? path in new[] { temporary, metadataTemporary })
            {
                if (path is null) { continue; }
                try { File.Delete(path); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private async Task<PixelBuffer?> DecodeWholeWithinBudgetAsync(string source, ImageEditRecipe recipe, CancellationToken token)
    {
        if (decoder is not IPreviewImageDecoder bounded) { return null; }
        if (recipe.SourceSize.PixelCount * 4 <= SourceByteLimit)
        {
            PixelBuffer full = await bounded.DecodeDetailAsync(source, SourceByteLimit, token).ConfigureAwait(false);
            if (full.SourceSize != recipe.SourceSize || full.Size != full.SourceSize || full.Pixels.Length > SourceByteLimit)
            {
                full.Dispose();
                throw new ImageExportException(ImageExportError.SourceChanged);
            }
            return full;
        }
        if (recipe.Crop != new PixelRect(0, 0, recipe.SourceSize.Width, recipe.SourceSize.Height)) { return null; }
        PixelSize natural = recipe.NaturalSize;
        double density = Math.Min(1, Math.Max((double)recipe.OutputSize.Width / natural.Width,
            (double)recipe.OutputSize.Height / natural.Height));
        PixelSize target = new(Math.Min(recipe.SourceSize.Width, Math.Max(1, (int)Math.Ceiling(recipe.SourceSize.Width * density) + 1)),
            Math.Min(recipe.SourceSize.Height, Math.Max(1, (int)Math.Ceiling(recipe.SourceSize.Height * density) + 1)));
        if (target.PixelCount * 4 > SourceByteLimit) { return null; }
        PixelBuffer image = density < 1
            ? await bounded.DecodePreviewAsync(source, target, token).ConfigureAwait(false)
            : await bounded.DecodeDetailAsync(source, SourceByteLimit, token).ConfigureAwait(false);
        if (image.SourceSize != recipe.SourceSize || image.Size.PixelCount * 4 > SourceByteLimit
            || (density == 1 && image.Size != recipe.SourceSize))
        {
            image.Dispose();
            throw new ImageExportException(ImageExportError.SourceChanged);
        }
        return image;
    }

    private static PixelBuffer? TryReuseSourcePixels(ImageExportRequest request)
    {
        // The immutable memory snapshot keeps the array alive even if browsing disposes its owner.
        // Its file stamp was checked under the source lock before entering here.
        if (request.SourcePixels is not { } pixels || request.ExpectedSourceLength is null
            || request.ExpectedSourceModifiedUtc is null || pixels.Size != request.Recipe.SourceSize
            || pixels.SourceFileStamp != new ImageFileStamp(request.ExpectedSourceLength.Value, request.ExpectedSourceModifiedUtc.Value)
            || pixels.Stride < (long)pixels.Size.Width * 4 || (long)pixels.Stride * pixels.Size.Height > SourceByteLimit
            || pixels.Pixels.Length < (long)pixels.Stride * pixels.Size.Height
            || !MemoryMarshal.TryGetArray(pixels.Pixels, out ArraySegment<byte> array) || array.Array is null || array.Offset != 0)
        {
            return null;
        }
        return new PixelBuffer(pixels.Size, pixels.Stride, array.Array);
    }

    private static void Validate(ImageExportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.Recipe);
        bool memory = request.MemorySourceIdentity is { } identity && identity != Guid.Empty;
        if (string.IsNullOrWhiteSpace(request.DestinationPath)
            || (memory ? request.SourcePath is not null
                : request.MemorySourceIdentity is not null || string.IsNullOrWhiteSpace(request.SourcePath))
            || (!memory && string.Equals(Path.GetFullPath(request.SourcePath!), Path.GetFullPath(request.DestinationPath), StringComparison.OrdinalIgnoreCase)))
        {
            throw new ImageExportException(ImageExportError.InvalidDestination);
        }
        string extension = Path.GetExtension(request.DestinationPath);
        bool matchingExtension = request.Format switch
        {
            ImageExportFormat.Png => extension.Equals(".png", StringComparison.OrdinalIgnoreCase),
            ImageExportFormat.Jpeg => extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase),
            ImageExportFormat.Webp => extension.Equals(".webp", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
        if (!matchingExtension)
        {
            throw new ImageExportException(ImageExportError.InvalidDestination);
        }
        if (request.JpegQuality is < 1 or > 100) { throw new ArgumentOutOfRangeException(nameof(request)); }
        if (!Enum.IsDefined(request.MetadataMode)) { throw new ArgumentOutOfRangeException(nameof(request)); }
        PixelSize size = request.Recipe.OutputSize;
        PixelSize source = request.Recipe.SourceSize;
        if (source.Width > 32768 || source.Height > 32768 || source.PixelCount > 100_000_000
            || size.Width > 32768 || size.Height > 32768 || size.PixelCount * 4 > OutputByteLimit)
        {
            throw new ImageExportException(ImageExportError.BudgetExceeded);
        }
        if (!request.Recipe.Adjustments.IsIdentity && size.PixelCount * 4 * 4 > EffectWorkingByteLimit)
        {
            throw new ImageExportException(ImageExportError.BudgetExceeded);
        }
        if (memory)
        {
            if (request.SourcePixels is { IsSrgb: false }) { throw new ImageExportException(ImageExportError.DecodeFailed); }
            if (source.PixelCount * 4 > SourceByteLimit)
            {
                throw new ImageExportException(ImageExportError.BudgetExceeded);
            }
            if (request.SourcePixels is not { } pixels || pixels.Size != source
                || request.ExpectedSourceLength is not null || request.ExpectedSourceModifiedUtc is not null
                || pixels.SourceFileStamp is not null || pixels.Stride < (long)source.Width * 4
                || pixels.Pixels.Length < (long)pixels.Stride * source.Height
                || !MemoryMarshal.TryGetArray(pixels.Pixels, out ArraySegment<byte> array) || array.Array is null)
            {
                throw new ImageExportException(ImageExportError.SourceChanged);
            }
            if ((long)pixels.Stride * source.Height > SourceByteLimit || pixels.Pixels.Length > SourceByteLimit)
            {
                throw new ImageExportException(ImageExportError.BudgetExceeded);
            }
        }
    }

    private static SKRect ToRect(PixelRect rect) => new(rect.X, rect.Y, rect.Right, rect.Bottom);

    private static SKBitmap AttachPixels(PixelBuffer image, SKColorSpace colorSpace) => AttachPixels(image.Size, image.Stride, image.Pixels, colorSpace);

    private static SKBitmap AttachPixels(PixelSize size, int stride, ReadOnlyMemory<byte> memory, SKColorSpace colorSpace)
    {
        if (!MemoryMarshal.TryGetArray(memory, out ArraySegment<byte> pixels) || pixels.Array is null)
        {
            throw new InvalidOperationException("An array-backed pixel buffer is required.");
        }
        PinnedPixels pin = new(pixels.Array, pixels.Offset);
        SKBitmap bitmap = new();
        try
        {
            if (!bitmap.InstallPixels(new(size.Width, size.Height, SKColorType.Bgra8888, SKAlphaType.Premul, colorSpace),
                pin.Address, stride, (_, context) => ((PinnedPixels)context).Dispose(), pin))
            {
                throw new InvalidOperationException("Unable to attach decoded pixels.");
            }
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            pin.Dispose();
            throw;
        }
    }

    private sealed class PinnedPixels : IDisposable
    {
        private IntPtr _handle;
        public PinnedPixels(byte[] pixels, int offset)
        {
            GCHandle handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            _handle = GCHandle.ToIntPtr(handle);
            Address = IntPtr.Add(handle.AddrOfPinnedObject(), offset);
        }
        public IntPtr Address { get; }
        public void Dispose()
        {
            IntPtr handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
            if (handle != IntPtr.Zero) { GCHandle.FromIntPtr(handle).Free(); }
        }
    }

    private sealed class DecodedRegionOwner(DecodedImageRegion region) : IDisposable
    {
        public DecodedImageRegion Region { get; } = region;
        public void Dispose() => Region.Image.Dispose();
    }
}
