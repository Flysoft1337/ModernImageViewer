using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

using ModernImageViewer.Application.Images;
using ModernImageViewer.Codecs.Wic;
using ModernImageViewer.Imaging;

namespace ModernImageViewer.Codecs.Raw;

public static class RawPreviewImageDecoder
{
    private const int MaximumPreviewBytes = 32 * 1024 * 1024;
    private const long MaximumFileBytes = 256 * 1024 * 1024;
    private static readonly DecodeScheduler NativeSlot = new(serializeThumbnails: true);

    public static PixelBuffer Decode(string path, PixelSize? maximumSize, long? maximumDecodedBytes,
        CancellationToken cancellationToken) =>
        Decode(path, maximumSize, maximumDecodedBytes, DecodePriority.Foreground, cancellationToken);

    internal static PixelBuffer Decode(string path, PixelSize? maximumSize, long? maximumDecodedBytes,
        DecodePriority priority, CancellationToken cancellationToken)
    {
        using IDisposable lease = NativeSlot.AcquireAsync(priority, cancellationToken).GetAwaiter().GetResult();
        return DecodeCore(path, maximumSize, maximumDecodedBytes, cancellationToken);
    }

    private static PixelBuffer DecodeCore(string path, PixelSize? maximumSize, long? maximumDecodedBytes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using FileStream original = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (original.Length > MaximumFileBytes) { throw new ImageSizeLimitExceededException(); }
        RawNativeApi api = RawNativeApi.Instance.Value;
        RawNativeApi.ProgressCallback progress = (_, _, _, _) => cancellationToken.IsCancellationRequested ? 1 : 0;
        nint context = api.Create(progress);
        if (context == 0) { throw new ImageSizeLimitExceededException(); }
        try
        {
            Check(api.Open(context, Path.GetFullPath(path)), cancellationToken);
            Check(api.Unpack(context), cancellationToken);
            Check(api.Preview(context, out RawNativeApi.PreviewInfo info, out nint data), cancellationToken);
            if (data == 0 || info.Bytes == 0 || info.Bytes > MaximumPreviewBytes)
            {
                throw new ImageSizeLimitExceededException();
            }
            string? make = ReadText(api.Make(context));
            string? model = ReadText(api.Model(context));
            string? camera = model is null ? make : make is null || model.StartsWith(make, StringComparison.OrdinalIgnoreCase)
                ? model : $"{make} {model}";
            return info.Format == 1
                ? DecodeJpeg(data, info, camera, maximumSize, maximumDecodedBytes, cancellationToken)
                : DecodeBitmap(data, info, camera, maximumSize, maximumDecodedBytes, cancellationToken);
        }
        finally
        {
            api.Close(context);
            GC.KeepAlive(progress);
        }
    }

    private static PixelBuffer DecodeJpeg(nint data, RawNativeApi.PreviewInfo info, string? camera,
        PixelSize? maximumSize, long? maximumDecodedBytes, CancellationToken cancellationToken)
    {
        using NativePreviewStream stream = new(data, checked((int)info.Bytes));
        ushort? orientation = info.Orientation is >= 1 and <= 8 ? (ushort)info.Orientation : null;
        // A known thumbnail tflip overrides JPEG EXIF once inside WIC. Unknown tflip preserves EXIF.
        using PixelBuffer decoded = new WicImageDecoder().Decode(stream, maximumSize, cancellationToken,
            maximumDecodedBytes, orientation);
        if (!MemoryMarshal.TryGetArray(decoded.Pixels, out ArraySegment<byte> owned) || owned.Offset != 0 || owned.Array is null)
        {
            throw new ImageDecodeException(ImageOpenError.DecodeFailed);
        }
        return new PixelBuffer(decoded.Size, decoded.Stride, owned.Array, decoded.Metadata with
        {
            Camera = decoded.Metadata.Camera ?? camera,
            IsEmbeddedPreview = true,
        }, decoded.SourceSize);
    }

    private static PixelBuffer DecodeBitmap(nint data, RawNativeApi.PreviewInfo info, string? camera,
        PixelSize? maximumSize, long? maximumDecodedBytes, CancellationToken cancellationToken)
    {
        if (info.Format is not (2 or 3) || info.Channels is not (1 or 3) || info.Bits is not (8 or 16))
        {
            throw new ImageDecodeException(ImageOpenError.RawNoPreview);
        }
        if (info.Width == 0 || info.Height == 0) { throw new ImageDecodeException(ImageOpenError.CorruptFile); }
        PixelSize raw = new(checked((int)info.Width), checked((int)info.Height));
        ImageDecodeLimits.Default.ValidateAndGetStride(raw);
        int channels = (int)info.Channels;
        int bytesPerSample = (int)info.Bits / 8;
        int rowBytes = checked(raw.Width * channels * bytesPerSample);
        if ((long)rowBytes * raw.Height > info.Bytes) { throw new ImageDecodeException(ImageOpenError.CorruptFile); }
        ushort orientation = info.Orientation is >= 1 and <= 8 ? (ushort)info.Orientation : (ushort)1;
        PixelSize source = orientation >= 5 ? new(raw.Height, raw.Width) : raw;
        double scale = maximumSize is { } maximum
            ? Math.Min(1, Math.Min((double)maximum.Width / source.Width, (double)maximum.Height / source.Height)) : 1;
        PixelSize output = new(Math.Max(1, (int)Math.Floor(raw.Width * scale)), Math.Max(1, (int)Math.Floor(raw.Height * scale)));
        int stride = ImageDecodeLimits.Default.ValidateAndGetStride(output);
        int bytes = checked(stride * output.Height);
        if (maximumDecodedBytes is { } budget && bytes > budget) { throw new ImageSizeLimitExceededException(); }
        cancellationToken.ThrowIfCancellationRequested();
        byte[] pixels = GC.AllocateUninitializedArray<byte>(bytes, pinned: true);
        byte[] top = new byte[rowBytes];
        byte[] bottom = new byte[rowBytes];
        for (int y = 0; y < output.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double sourceY = Math.Clamp(((y + 0.5) * raw.Height / output.Height) - 0.5, 0, raw.Height - 1);
            int y0 = (int)sourceY;
            int y1 = Math.Min(raw.Height - 1, y0 + 1);
            Marshal.Copy(nint.Add(data, checked(y0 * rowBytes)), top, 0, rowBytes);
            Marshal.Copy(nint.Add(data, checked(y1 * rowBytes)), bottom, 0, rowBytes);
            double fy = sourceY - y0;
            for (int x = 0; x < output.Width; x++)
            {
                double sourceX = Math.Clamp(((x + 0.5) * raw.Width / output.Width) - 0.5, 0, raw.Width - 1);
                int x0 = (int)sourceX;
                int x1 = Math.Min(raw.Width - 1, x0 + 1);
                double fx = sourceX - x0;
                for (int channel = 0; channel < 3; channel++)
                {
                    int component = channels == 1 ? 0 : channel;
                    double a = Sample(top, (x0 * channels) + component, bytesPerSample);
                    double b = Sample(top, (x1 * channels) + component, bytesPerSample);
                    double c = Sample(bottom, (x0 * channels) + component, bytesPerSample);
                    double d = Sample(bottom, (x1 * channels) + component, bytesPerSample);
                    double value = ((a + ((b - a) * fx)) * (1 - fy)) + ((c + ((d - c) * fx)) * fy);
                    pixels[(y * stride) + (x * 4) + 2 - channel] = (byte)Math.Clamp((int)Math.Round(value / (bytesPerSample == 2 ? 257 : 1)), 0, 255);
                }
                pixels[(y * stride) + (x * 4) + 3] = 255;
            }
        }
        PixelSize size = PixelOrientation.ApplyInPlace(pixels, output, orientation, cancellationToken);
        return new PixelBuffer(size, checked(size.Width * 4), pixels,
            new ImageMetadata { Camera = camera, Orientation = orientation, IsEmbeddedPreview = true }, source);
    }

    private static double Sample(byte[] row, int sample, int bytesPerSample) => bytesPerSample == 1
        ? row[sample] : BinaryPrimitives.ReadUInt16LittleEndian(row.AsSpan(sample * 2, 2));

    private static string? ReadText(nint data)
    {
        byte[] bytes = new byte[64];
        Marshal.Copy(data, bytes, 0, bytes.Length);
        int terminator = Array.IndexOf(bytes, (byte)0);
        string text = Encoding.UTF8.GetString(bytes, 0, terminator < 0 ? bytes.Length : terminator).Trim();
        return text.Length == 0 ? null : text;
    }

    private static void Check(int result, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (result == 0) { return; }
        if (result == -200001 || result == -100007) { throw new ImageSizeLimitExceededException(); }
        throw new ImageDecodeException(result is -5 or -6 or -9 ? ImageOpenError.RawNoPreview : ImageOpenError.CorruptFile);
    }
}
