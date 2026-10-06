using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

using ModernImageViewer.Application.Images;
using ModernImageViewer.Application.Integration;
using ModernImageViewer.Imaging;

namespace ModernImageViewer.Platform.Integration;

public sealed class WindowsImageClipboardService : IImageClipboardService
{
    private readonly Func<IDataObject?> _readData;
    private readonly Action<IDataObject> _writeData;

    public WindowsImageClipboardService() : this(Clipboard.GetDataObject,
        data => Clipboard.SetDataObject(data, copy: true))
    { }

    public WindowsImageClipboardService(Func<IDataObject?> readData, Action<IDataObject> writeData)
    {
        ArgumentNullException.ThrowIfNull(readData);
        ArgumentNullException.ThrowIfNull(writeData);
        _readData = readData;
        _writeData = writeData;
    }

    public ImageClipboardInput ReadInput() => Thread.CurrentThread.GetApartmentState() == ApartmentState.STA
        ? ReadSnapshot() : OnStaAsync(ReadSnapshot).GetAwaiter().GetResult();

    private ImageClipboardInput ReadSnapshot()
    {
        // Exactly one IDataObject snapshot; never request automatic format conversion.
        IDataObject? data = _readData();
        if (data is null) { return new([]); }
        if (data.GetData(DataFormats.FileDrop, autoConvert: false) is string[] paths && paths.Length != 0)
        {
            if (paths.Length > OpenRequest.MaximumPaths)
            {
                throw new ArgumentException($"At most {OpenRequest.MaximumPaths} paths can be opened at once.");
            }
            return new((string[])paths.Clone());
        }

        if (data.GetDataPresent("PNG", autoConvert: false))
        {
            return new([], ReadPng(data.GetData("PNG", autoConvert: false)));
        }
        object? bitmap = data.GetData(DataFormats.Bitmap, autoConvert: false);
        if (bitmap is null) { return new([]); }
        BitmapSource source = bitmap as BitmapSource ?? throw Corrupt();
        PixelSize size = ValidateSize(source.PixelWidth, source.PixelHeight);
        ValidateFormat(source);
        // Keep the original WPF source. Cloning or CopyPixels here would copy a full image.
        if (!source.IsFrozen)
        {
            if (!source.CanFreeze) { throw Corrupt(); }
            source.Freeze();
        }
        return new([], new MemoryImageInput(size, (maximum, budget, token) =>
            Task.Run(() => ReadPixels(source, size, maximum, budget, token), token)));
    }

    private static MemoryImageInput ReadPng(object? value)
    {
        try
        {
            if (value is byte[] bytes)
            {
                using MemoryStream stream = new(bytes, writable: false);
                return SnapshotPng(stream);
            }
            if (value is not Stream input || !input.CanRead) { throw Corrupt(); }
            long? position = input.CanSeek ? input.Position : null;
            try
            {
                if (input.CanSeek) { input.Position = 0; }
                return SnapshotPng(input);
            }
            finally
            {
                if (position.HasValue) { input.Position = position.Value; }
            }
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException or ArgumentException or FormatException)
        {
            throw Corrupt(exception);
        }
    }

    private static MemoryImageInput SnapshotPng(Stream input)
    {
        if (input.CanSeek && input.Length > ClipboardImageLimits.EncodedBytes) { throw TooLarge(); }
        Span<byte> header = stackalloc byte[33];
        input.ReadExactly(header);
        if (!header[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
            || BinaryPrimitives.ReadUInt32BigEndian(header[8..]) != 13
            || !header.Slice(12, 4).SequenceEqual("IHDR"u8)) { throw Corrupt(); }
        uint width = BinaryPrimitives.ReadUInt32BigEndian(header[16..]);
        uint height = BinaryPrimitives.ReadUInt32BigEndian(header[20..]);
        if (width > 32768 || height > 32768) { throw TooLarge(); }
        PixelSize size = ValidateSize((int)width, (int)height);
        if (header[24] > 8) { throw Unsupported(); }
        if (header[24] is not (1 or 2 or 4 or 8) || header[25] is not (0 or 2 or 3 or 4 or 6)
            || (header[25] is 2 or 4 or 6 && header[24] != 8)
            || header[26] != 0 || header[27] != 0 || header[28] > 1) { throw Corrupt(); }
        // Snapshot encoded bytes only, with a hard cap even for non-seekable streams.
        LimitedPngStream encoded = new(CancellationToken.None);
        try
        {
            encoded.Write(header);
            input.CopyTo(encoded);
        }
        catch
        {
            encoded.Dispose();
            throw;
        }
        // No PNG decoder runs on the snapshot STA. Lazy caches one frozen source (or failure).
        Lazy<BitmapSource> decoded = new(() => DecodePng(encoded, size));
        return new(size, (maximum, budget, token) => Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            ValidateBudget(Fit(size, maximum), budget);
            return ReadPixels(decoded.Value, size, maximum, budget, token);
        }, token));
    }

    private static BitmapSource DecodePng(MemoryStream encoded, PixelSize size)
    {
        using (encoded)
        {
            try
            {
                encoded.Position = 0;
                PngBitmapDecoder decoder = new(encoded, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                if (decoder.Frames.Count != 1) { throw Corrupt(); }
                BitmapSource source = decoder.Frames[0];
                if (source.PixelWidth != size.Width || source.PixelHeight != size.Height) { throw Corrupt(); }
                ValidateFormat(source);
                if (!source.CanFreeze) { throw Corrupt(); }
                source.Freeze();
                return source;
            }
            catch (Exception exception) when (exception is IOException or NotSupportedException or ArgumentException or FormatException
                or System.Runtime.InteropServices.COMException)
            {
                throw Corrupt(exception);
            }
        }
    }

    private static void ValidateFormat(BitmapSource source)
    {
        if (source.Format.BitsPerPixel <= 0) { throw Corrupt(); }
        // Restrict accepted source formats; this bounds pixel payload, not native/process overhead.
        if (source.Format.BitsPerPixel > 32 || source.Format == PixelFormats.Gray16
            || source.Format == PixelFormats.Gray32Float || source.Format == PixelFormats.Bgr101010) { throw Unsupported(); }
    }

    private static PixelBuffer ReadPixels(BitmapSource source, PixelSize sourceSize, PixelSize maximum,
        long budget, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        PixelSize outputSize = Fit(sourceSize, maximum);
        ValidateBudget(outputSize, budget);
        try
        {
            BitmapSource output = source;
            if (outputSize != sourceSize)
            {
                output = new TransformedBitmap(output, new ScaleTransform(
                    (double)outputSize.Width / sourceSize.Width, (double)outputSize.Height / sourceSize.Height));
            }
            if (output.Format != PixelFormats.Pbgra32)
            {
                output = new FormatConvertedBitmap(output, PixelFormats.Pbgra32, null, 0);
            }
            // WPF rounding is checked before requesting any managed pixel storage.
            outputSize = ValidateSize(output.PixelWidth, output.PixelHeight);
            if (outputSize.Width > maximum.Width || outputSize.Height > maximum.Height) { throw TooLarge(); }
            ValidateBudget(outputSize, budget);
            token.ThrowIfCancellationRequested();
            int stride = outputSize.Width * 4;
            byte[] pixels = GC.AllocateUninitializedArray<byte>(checked(stride * outputSize.Height));
            for (int y = 0; y < outputSize.Height; y += 64)
            {
                token.ThrowIfCancellationRequested();
                output.CopyPixels(new Int32Rect(0, y, outputSize.Width, Math.Min(64, outputSize.Height - y)),
                    pixels, stride, y * stride);
            }
            token.ThrowIfCancellationRequested();
            return new(outputSize, stride, pixels, sourceSize: sourceSize);
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException or ArgumentException)
        {
            throw Corrupt(exception);
        }
    }

    public async Task WriteAsync(ImageClipboardPixels image, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        cancellationToken.ThrowIfCancellationRequested();
        Dispatcher? dispatcher = Thread.CurrentThread.GetApartmentState() == ApartmentState.STA
            ? Dispatcher.CurrentDispatcher : null;
        var prepared = await Task.Run(() => PrepareOutput(image, cancellationToken), cancellationToken).ConfigureAwait(false);
        try
        {
            void Publish()
            {
                cancellationToken.ThrowIfCancellationRequested();
                DataObject data = new();
                data.SetData("PNG", prepared.Png, autoConvert: false);
                data.SetData(DataFormats.Bitmap, prepared.Bitmap, autoConvert: false);
                cancellationToken.ThrowIfCancellationRequested();
                // Successful publication transfers the stream lifetime to the IDataObject.
                // The native writer persists formats with copy:true before returning.
                _writeData(data);
            }
            if (dispatcher is not null)
            {
                await dispatcher.InvokeAsync(Publish, DispatcherPriority.Normal, cancellationToken).Task.ConfigureAwait(false);
            }
            else
            {
                await OnStaAsync(() => { Publish(); return true; }).ConfigureAwait(false);
            }
        }
        catch
        {
            prepared.Png.Dispose();
            throw;
        }
    }

    private static (BitmapSource Bitmap, MemoryStream Png) PrepareOutput(ImageClipboardPixels image, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        PixelSize source = image.Size;
        if (source.Width <= 0 || source.Height <= 0) { throw Corrupt(); }
        if (source.Width > 32768 || source.Height > 32768
            || source.PixelCount * 4 > ImageOpenCoordinator.MainPixelBudgetBytes) { throw TooLarge(); }
        if (image.OriginalSize) { ValidateBudget(source, ClipboardImageLimits.SourceBytes); }
        if (image.Stride < source.Width * 4 || (long)image.Stride * source.Height > image.Pixels.Length)
        {
            throw Corrupt();
        }
        if ((long)image.Stride * source.Height > ImageOpenCoordinator.MainPixelBudgetBytes) { throw TooLarge(); }
        PixelSize display = image.Orientation.GetDisplaySize(source);
        PixelSize size = image.OriginalSize ? display : Fit(display, ClipboardImageLimits.PreviewSize);
        ValidateBudget(size, ClipboardImageLimits.SourceBytes);
        byte[] bitmapPixels;
        int stride;
        if (size == source && image.Orientation.IsIdentity
            && MemoryMarshal.TryGetArray(image.Pixels, out ArraySegment<byte> segment)
            && segment.Offset == 0 && segment.Array is byte[] sharedPixels)
        {
            // BitmapSource.Create makes its required native copy; avoid a redundant managed copy.
            bitmapPixels = sharedPixels;
            stride = image.Stride;
        }
        else
        {
            bitmapPixels = CreateOutputPixels(image, size, token);
            stride = size.Width * 4;
        }
        token.ThrowIfCancellationRequested();
        BitmapSource bitmap = BitmapSource.Create(size.Width, size.Height, 96, 96,
            PixelFormats.Pbgra32, null, bitmapPixels, stride);
        bitmap.Freeze();
        LimitedPngStream png = new(token);
        try
        {
            PngBitmapEncoder encoder = new();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            encoder.Save(png);
            token.ThrowIfCancellationRequested();
            png.Position = 0;
            return (bitmap, png);
        }
        catch
        {
            png.Dispose();
            throw;
        }
    }

    internal static byte[] CreateOutputPixels(ImageClipboardPixels image, PixelSize size, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ValidateBudget(size, ClipboardImageLimits.SourceBytes);
        PixelSize source = image.Size;
        PixelSize display = image.Orientation.GetDisplaySize(source);
        int stride = size.Width * 4;
        byte[] destination = GC.AllocateUninitializedArray<byte>(checked(stride * size.Height));
        ReadOnlySpan<byte> pixels = image.Pixels.Span;
        bool downsample = size != display;
        // Sample the inverse display transform directly into one owned, bounded destination.
        for (int y = 0; y < size.Height; y++)
        {
            token.ThrowIfCancellationRequested();
            for (int x = 0; x < size.Width; x++)
            {
                var point = image.Orientation.ToSourcePoint(source,
                    (x + .5) * display.Width / size.Width, (y + .5) * display.Height / size.Height);
                Span<byte> target = destination.AsSpan((y * stride) + (x * 4), 4);
                if (downsample)
                {
                    SampleBilinear(pixels, image.Stride, source, point.X - .5, point.Y - .5, target);
                }
                else
                {
                    int sx = Math.Clamp((int)point.X, 0, source.Width - 1);
                    int sy = Math.Clamp((int)point.Y, 0, source.Height - 1);
                    pixels.Slice((sy * image.Stride) + (sx * 4), 4).CopyTo(target);
                }
            }
        }
        token.ThrowIfCancellationRequested();
        return destination;
    }

    private static void SampleBilinear(ReadOnlySpan<byte> pixels, int stride, PixelSize size,
        double x, double y, Span<byte> target)
    {
        x = Math.Clamp(x, 0, size.Width - 1);
        y = Math.Clamp(y, 0, size.Height - 1);
        int x0 = (int)x;
        int y0 = (int)y;
        int x1 = Math.Min(x0 + 1, size.Width - 1);
        int y1 = Math.Min(y0 + 1, size.Height - 1);
        double fx = x - x0;
        double fy = y - y0;
        int topLeft = (y0 * stride) + (x0 * 4);
        int topRight = (y0 * stride) + (x1 * 4);
        int bottomLeft = (y1 * stride) + (x0 * 4);
        int bottomRight = (y1 * stride) + (x1 * 4);
        // Interpolate color and alpha together in premultiplied space; no unpremultiply halos.
        for (int channel = 0; channel < 4; channel++)
        {
            double top = (pixels[topLeft + channel] * (1 - fx)) + (pixels[topRight + channel] * fx);
            double bottom = (pixels[bottomLeft + channel] * (1 - fx)) + (pixels[bottomRight + channel] * fx);
            target[channel] = (byte)Math.Clamp((int)((top * (1 - fy)) + (bottom * fy) + .5), 0, 255);
        }
    }

    private static PixelSize Fit(PixelSize source, PixelSize maximum)
    {
        if (maximum.Width <= 0 || maximum.Height <= 0) { throw TooLarge(); }
        double scale = Math.Min(1, Math.Min((double)maximum.Width / source.Width, (double)maximum.Height / source.Height));
        return new(Math.Max(1, (int)(source.Width * scale)), Math.Max(1, (int)(source.Height * scale)));
    }

    private static PixelSize ValidateSize(int width, int height)
    {
        if (width <= 0 || height <= 0) { throw Corrupt(); }
        if (width > 32768 || height > 32768 || (long)width * height * 4 > ClipboardImageLimits.SourceBytes)
        {
            throw TooLarge();
        }
        return new(width, height);
    }

    private static void ValidateBudget(PixelSize size, long budget)
    {
        if (size.PixelCount * 4 > Math.Min(budget, ClipboardImageLimits.SourceBytes)) { throw TooLarge(); }
    }

    private static ImageDecodeException TooLarge() => new(ImageOpenError.ImageTooLarge);
    private static ImageDecodeException Unsupported() => new(ImageOpenError.UnsupportedFormat);
    private static ImageDecodeException Corrupt(Exception? exception = null) => new(ImageOpenError.CorruptFile, exception);

    private static Task<T> OnStaAsync<T>(Func<T> action)
    {
        TaskCompletionSource<T> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread thread = new(() =>
        {
            try { completion.SetResult(action()); }
            catch (Exception exception) { completion.SetException(exception); }
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    internal sealed class LimitedPngStream(CancellationToken token) : MemoryStream
    {
        private void Check(long length)
        {
            token.ThrowIfCancellationRequested();
            if (length > ClipboardImageLimits.EncodedBytes) { throw TooLarge(); }
            if (length > Capacity)
            {
                // Reserve explicitly so MemoryStream cannot double an irregular capacity past the cap.
                Capacity = (int)Math.Max(length, Math.Min(ClipboardImageLimits.EncodedBytes,
                    Math.Max(256L, (long)Capacity * 2)));
            }
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            Check(Position + count);
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Check(Position + buffer.Length);
            base.Write(buffer);
        }

        public override void WriteByte(byte value)
        {
            Check(Position + 1);
            base.WriteByte(value);
        }

        public override void SetLength(long value)
        {
            Check(value);
            base.SetLength(value);
        }
    }
}
