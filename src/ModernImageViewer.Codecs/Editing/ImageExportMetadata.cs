using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media.Imaging;

using ImageMagick;

using ModernImageViewer.Application.Editing;
using ModernImageViewer.Application.Images;
using ModernImageViewer.Codecs.Modern;
using ModernImageViewer.Codecs.Raw;
using ModernImageViewer.Codecs.Wic;
using ModernImageViewer.Imaging;

using ImageMetadata = ModernImageViewer.Imaging.ImageMetadata;

namespace ModernImageViewer.Codecs.Editing;

internal static class ImageExportMetadata
{
    private const int ProfileByteLimit = 4 * 1024 * 1024;
    private const int ExifByteLimit = 65527;
    private const int CopyBufferBytes = 64 * 1024;
    private static readonly Lazy<byte[]> SrgbProfile = new(() => ColorProfiles.SRGB.ToByteArray());

    internal sealed record SourceInfo(ImageMetadata Metadata, byte[]? PixelProfile = null);

    internal static SourceInfo ReadSource(string? path, bool preserve, CancellationToken token)
    {
        if (path is null) { return new(ImageMetadata.Empty); }
        WicImageDecoder.DecodeSlot.Wait(token);
        try
        {
            if (SupportedImageFormats.IsRawExtension(Path.GetExtension(path))) { return ReadRaw(path, preserve, token); }
            if (Path.GetExtension(path).Equals(".svg", StringComparison.OrdinalIgnoreCase)) { return new(ImageMetadata.Empty); }
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            Span<byte> signature = stackalloc byte[12];
            int read = stream.ReadAtLeast(signature, signature.Length, false);
            stream.Position = 0;
            if (read == 12 && signature[..4].SequenceEqual("RIFF"u8) && signature[8..].SequenceEqual("WEBP"u8))
            {
                // Skia already converts WebP's embedded profile to sRGB during decoding.
                return new(preserve ? ReadChunkExif(stream, png: false, token) : ImageMetadata.Empty);
            }
            if (HeifContainer.TryDetect(stream, out bool avif))
            {
                // The HEIF decoder explicitly transforms pixels into sRGB. Ping reads profiles,
                // never a second raster; retain only the allowlisted EXIF fields.
                if (!preserve) { return new(ImageMetadata.Empty); }
                if (stream.Length > HeifImageDecoder.MaximumInputBytes) { throw new ImageExportException(ImageExportError.BudgetExceeded); }
                using MagickImage image = new();
                image.Progress += (_, args) => args.Cancel = token.IsCancellationRequested;
                try
                {
                    image.Ping(stream, new MagickReadSettings
                    {
                        Format = avif ? MagickFormat.Avif : MagickFormat.Heic,
                        FrameIndex = 0,
                        FrameCount = 1,
                        SyncImageWithExifProfile = false,
                    });
                    token.ThrowIfCancellationRequested();
                    byte[]? exif = image.GetExifProfile()?.ToByteArray();
                    return new(exif is { Length: <= ExifByteLimit } ? ReadExif(exif) : ImageMetadata.Empty);
                }
                catch (MagickException) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
                catch (MagickException) { return new(ImageMetadata.Empty); }
            }
            SourceInfo info = ReadWic(stream, preserve);
            if (preserve && read >= 8 && signature[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            {
                stream.Position = 0;
                info = info with { Metadata = ReadChunkExif(stream, png: true, token) };
            }
            token.ThrowIfCancellationRequested();
            return info;
        }
        finally { WicImageDecoder.DecodeSlot.Release(); }
    }

    private static SourceInfo ReadWic(Stream stream, bool preserve)
    {
        BitmapDecoder image;
        try
        {
            image = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.DelayCreation,
                BitmapCacheOption.None);
        }
        catch (FileFormatException) { return new(ImageMetadata.Empty); }
        catch (NotSupportedException) { return new(ImageMetadata.Empty); }
        if (image.Frames.Count == 0) { return new(ImageMetadata.Empty); }
        BitmapFrame frame = image.Frames[0];
        byte[]? profile = null;
        if (frame.ColorContexts is { Count: > 0 } contexts)
        {
            // WIC's FormatConvertedBitmap changes packing, not colorimetry. Tag borrowed
            // pixels with their actual RGB profile so the sRGB export canvas converts them.
            if (contexts.Count != 1) { throw new ImageExportException(ImageExportError.DecodeFailed); }
            using Stream input = contexts[0].OpenProfileStream();
            if (input.Length > ProfileByteLimit) { throw new ImageExportException(ImageExportError.BudgetExceeded); }
            profile = new byte[checked((int)input.Length)];
            input.ReadExactly(profile);
            if (profile.Length < 128 || !profile.AsSpan(16, 4).SequenceEqual("RGB "u8))
            {
                // CMYK/gray device profiles cannot describe the already-converted BGRA.
                throw new ImageExportException(ImageExportError.DecodeFailed);
            }
        }
        return new(preserve ? WicMetadataReader.Read(frame) : ImageMetadata.Empty, profile);
    }

    private static SourceInfo ReadRaw(string path, bool preserve, CancellationToken token)
    {
        if (new FileInfo(path).Length > 256L * 1024 * 1024) { throw new ImageExportException(ImageExportError.BudgetExceeded); }
        RawNativeApi api = RawNativeApi.Instance.Value;
        RawNativeApi.ProgressCallback progress = (_, _, _, _) => token.IsCancellationRequested ? 1 : 0;
        nint context = api.Create(progress);
        if (context == 0) { throw new ImageExportException(ImageExportError.BudgetExceeded); }
        try
        {
            int result = api.Open(context, Path.GetFullPath(path));
            token.ThrowIfCancellationRequested();
            if (result == 0) { result = api.Unpack(context); }
            token.ThrowIfCancellationRequested();
            if (result != 0 || api.Preview(context, out RawNativeApi.PreviewInfo info, out nint data) != 0 || data == 0)
            {
                throw new ImageExportException(ImageExportError.DecodeFailed);
            }
            if (info.Bytes is 0 or > 32 * 1024 * 1024) { throw new ImageExportException(ImageExportError.BudgetExceeded); }
            SourceInfo source;
            if (info.Format == 1)
            {
                using NativePreviewStream preview = new(data, checked((int)info.Bytes));
                source = ReadWic(preview, preserve);
            }
            else
            {
                // Bitmap thumbnails expose no profile; use the repository's sRGB default
                // for unprofiled samples, just like the RAW preview decoder.
                source = new(ImageMetadata.Empty);
            }
            static string? Text(nint address) => Marshal.PtrToStringUTF8(address, 64)?.Split('\0')[0].Trim();
            string? make = Text(api.Make(context));
            string? model = Text(api.Model(context));
            string? camera = string.IsNullOrWhiteSpace(model) ? make : string.IsNullOrWhiteSpace(make)
                || model.StartsWith(make, StringComparison.OrdinalIgnoreCase) ? model : $"{make} {model}";
            token.ThrowIfCancellationRequested();
            return preserve ? source with { Metadata = source.Metadata with { Camera = source.Metadata.Camera ?? camera } } : source;
        }
        finally { api.Close(context); GC.KeepAlive(progress); }
    }

    private static ImageMetadata ReadChunkExif(Stream stream, bool png, CancellationToken token)
    {
        stream.Position = png ? 8 : 12;
        Span<byte> header = stackalloc byte[8];
        int chunks = 0;
        while (stream.Position <= stream.Length - 8 && ++chunks <= 4096)
        {
            token.ThrowIfCancellationRequested();
            stream.ReadExactly(header);
            uint length = png ? BinaryPrimitives.ReadUInt32BigEndian(header) : BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
            ReadOnlySpan<byte> type = png ? header[4..] : header[..4];
            long extra = png ? 4 : length & 1;
            if (length > stream.Length - stream.Position - extra) { return ImageMetadata.Empty; }
            if (type.SequenceEqual(png ? "eXIf"u8 : "EXIF"u8))
            {
                if (length > ExifByteLimit) { return ImageMetadata.Empty; }
                byte[] exif = new byte[(int)length];
                stream.ReadExactly(exif);
                return ReadExif(exif);
            }
            stream.Seek(length + extra, SeekOrigin.Current);
        }
        return ImageMetadata.Empty;
    }

    private static ImageMetadata ReadExif(byte[] bytes)
    {
        try
        {
            ExifProfile profile = new(AddExifPrefix(bytes));
            string? make = profile.GetValue(ExifTag.Make)?.Value;
            string? model = profile.GetValue(ExifTag.Model)?.Value;
            return new()
            {
                Camera = model is null ? make : make is null || model.StartsWith(make, StringComparison.OrdinalIgnoreCase)
                    ? model : $"{make} {model}",
                Lens = profile.GetValue(ExifTag.LensModel)?.Value,
                CapturedAt = profile.GetValue(ExifTag.DateTimeOriginal)?.Value,
                Iso = profile.GetValue(ExifTag.ISOSpeedRatings)?.Value.FirstOrDefault(),
                ExposureSeconds = profile.GetValue(ExifTag.ExposureTime)?.Value.ToDouble(),
                Aperture = profile.GetValue(ExifTag.FNumber)?.Value.ToDouble(),
                FocalLength = profile.GetValue(ExifTag.FocalLength)?.Value.ToDouble(),
            };
        }
        catch (ArgumentException) { return ImageMetadata.Empty; }
        catch (InvalidOperationException) { return ImageMetadata.Empty; }
        catch (MagickException) { return ImageMetadata.Empty; }
    }

    internal static byte[]? CreateExif(ImageMetadata metadata, PixelSize size, ImageExportMetadataMode mode)
    {
        if (mode == ImageExportMetadataMode.Remove) { return null; }
        ExifProfile profile = new();
        void Text(ExifTag<string> tag, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) { return; }
            string text = value.Trim().Replace('\0', ' ');
            if (text.Length > 256) { text = text[..256]; }
            profile.SetValue(tag, text);
        }
        void RationalValue(ExifTag<Rational> tag, double? value)
        {
            if (value is > 0 and <= 1_000_000 && double.IsFinite(value.Value)) { profile.SetValue(tag, new Rational(value.Value)); }
        }
        Text(ExifTag.Model, metadata.Camera);
        Text(ExifTag.LensModel, metadata.Lens);
        Text(ExifTag.DateTimeOriginal, metadata.CapturedAt);
        if (metadata.Iso is > 0 and <= ushort.MaxValue) { profile.SetValue(ExifTag.ISOSpeedRatings, new[] { (ushort)metadata.Iso.Value }); }
        RationalValue(ExifTag.ExposureTime, metadata.ExposureSeconds);
        RationalValue(ExifTag.FNumber, metadata.Aperture);
        RationalValue(ExifTag.FocalLength, metadata.FocalLength);
        profile.SetValue(ExifTag.Orientation, (ushort)1);
        profile.SetValue(ExifTag.PixelXDimension, new Number((uint)size.Width));
        profile.SetValue(ExifTag.PixelYDimension, new Number((uint)size.Height));
        profile.SetValue(ExifTag.ColorSpace, (ushort)1);
        // A newly-created profile has no GPS, XMP, maker notes, thumbnail or stale dimensions.
        byte[] bytes = profile.ToByteArray();
        if (bytes.Length > ExifByteLimit) { throw new ImageExportException(ImageExportError.BudgetExceeded); }
        return bytes;
    }

    internal static string AddToEncodedFile(string encodedPath, ImageExportFormat format, PixelSize size,
        byte[]? exif, CancellationToken token)
    {
        string enrichedPath = encodedPath + ".metadata.tmp";
        try
        {
            using FileStream input = new(encodedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using FileStream output = new(enrichedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            byte[] copyBuffer = new byte[CopyBufferBytes];
            switch (format)
            {
                case ImageExportFormat.Jpeg:
                    Copy(input, output, 2, copyBuffer, token);
                    WriteJpegSegment(output, 0xe2, [.. "ICC_PROFILE\0"u8, 1, 1, .. SrgbProfile.Value]);
                    if (exif is not null) { WriteJpegSegment(output, 0xe1, AddExifPrefix(exif)); }
                    CopyJpegRaster(input, output, copyBuffer, token);
                    break;
                case ImageExportFormat.Png:
                    Copy(input, output, 33, copyBuffer, token); // Signature and IHDR, before any IDAT.
                    using (MemoryStream compressed = new())
                    {
                        compressed.Write("sRGB\0\0"u8); // Profile name, NUL, compression method.
                        using (ZLibStream zlib = new(compressed, CompressionLevel.Optimal, leaveOpen: true)) { zlib.Write(SrgbProfile.Value); }
                        WritePngChunk(output, "iCCP"u8, compressed.ToArray());
                    }
                    if (exif is not null) { WritePngChunk(output, "eXIf"u8, StripExifPrefix(exif)); }
                    CopyPngRaster(input, output, copyBuffer, token);
                    break;
                case ImageExportFormat.Webp:
                    WriteWebp(input, output, size, exif, copyBuffer, token);
                    break;
                default: throw new ArgumentOutOfRangeException(nameof(format));
            }
            token.ThrowIfCancellationRequested();
            output.Flush(flushToDisk: true);
            return enrichedPath;
        }
        catch
        {
            TryDelete(enrichedPath);
            throw;
        }
    }

    private static void WriteWebp(Stream input, Stream output, PixelSize size, byte[]? exif, byte[] buffer, CancellationToken token)
    {
        input.Position = 12;
        Span<byte> first = stackalloc byte[18];
        int firstLength = input.ReadAtLeast(first, first.Length, false);
        bool alpha = firstLength >= 18 && first[..4].SequenceEqual("VP8X"u8) && (first[8] & 0x10) != 0
            || firstLength >= 13 && first[..4].SequenceEqual("VP8L"u8) && (first[12] & 0x10) != 0;
        output.Write("RIFF\0\0\0\0WEBP"u8);
        byte[] extended = new byte[10];
        extended[0] = (byte)(0x20 | (alpha ? 0x10 : 0) | (exif is null ? 0 : 0x08));
        WriteUInt24(extended.AsSpan(4), size.Width - 1);
        WriteUInt24(extended.AsSpan(7), size.Height - 1);
        WriteWebpChunk(output, "VP8X"u8, extended);
        WriteWebpChunk(output, "ICCP"u8, SrgbProfile.Value);
        input.Position = 12;
        Span<byte> header = stackalloc byte[8];
        while (input.Position < input.Length)
        {
            token.ThrowIfCancellationRequested();
            input.ReadExactly(header);
            uint count = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
            long padded = count + (count & 1L);
            if (padded > input.Length - input.Position) { throw new ImageExportException(ImageExportError.WriteFailed); }
            if (header[..4].SequenceEqual("VP8X"u8) || header[..4].SequenceEqual("ICCP"u8)
                || header[..4].SequenceEqual("EXIF"u8) || header[..4].SequenceEqual("XMP "u8))
            {
                input.Seek(padded, SeekOrigin.Current);
            }
            else
            {
                output.Write(header);
                Copy(input, output, padded, buffer, token);
            }
        }
        if (exif is not null) { WriteWebpChunk(output, "EXIF"u8, StripExifPrefix(exif)); }
        long length = output.Position;
        if (length - 8 > uint.MaxValue) { throw new ImageExportException(ImageExportError.BudgetExceeded); }
        output.Position = 4;
        Span<byte> riffSize = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(riffSize, (uint)(length - 8));
        output.Write(riffSize);
        output.Position = length;
    }

    private static void CopyJpegRaster(Stream input, Stream output, byte[] buffer, CancellationToken token)
    {
        Span<byte> header = stackalloc byte[4];
        while (input.Position < input.Length)
        {
            token.ThrowIfCancellationRequested();
            input.ReadExactly(header);
            if (header[0] != 255) { throw new ImageExportException(ImageExportError.WriteFailed); }
            int length = BinaryPrimitives.ReadUInt16BigEndian(header[2..]) - 2;
            if (length < 0 || length > input.Length - input.Position) { throw new ImageExportException(ImageExportError.WriteFailed); }
            input.ReadExactly(buffer.AsSpan(0, length));
            bool metadata = header[1] == 0xe1 || header[1] == 0xe2 && buffer.AsSpan(0, length).StartsWith("ICC_PROFILE\0"u8);
            if (!metadata) { output.Write(header); output.Write(buffer, 0, length); }
            if (header[1] == 0xda)
            {
                Copy(input, output, input.Length - input.Position, buffer, token);
                return;
            }
        }
        throw new ImageExportException(ImageExportError.WriteFailed);
    }

    private static void CopyPngRaster(Stream input, Stream output, byte[] buffer, CancellationToken token)
    {
        Span<byte> header = stackalloc byte[8];
        while (input.Position < input.Length)
        {
            token.ThrowIfCancellationRequested();
            input.ReadExactly(header);
            uint length = BinaryPrimitives.ReadUInt32BigEndian(header);
            if (length + 4L > input.Length - input.Position) { throw new ImageExportException(ImageExportError.WriteFailed); }
            ReadOnlySpan<byte> type = header[4..];
            bool metadata = type.SequenceEqual("iCCP"u8) || type.SequenceEqual("sRGB"u8)
                || type.SequenceEqual("gAMA"u8) || type.SequenceEqual("cHRM"u8) || type.SequenceEqual("eXIf"u8);
            if (metadata) { input.Seek(length + 4L, SeekOrigin.Current); }
            else
            {
                output.Write(header);
                Copy(input, output, length + 4L, buffer, token);
            }
        }
    }

    private static void WriteUInt24(Span<byte> destination, int value)
    {
        destination[0] = (byte)value;
        destination[1] = (byte)(value >> 8);
        destination[2] = (byte)(value >> 16);
    }

    private static void Copy(Stream input, Stream output, long remaining, byte[] buffer, CancellationToken token)
    {
        while (remaining > 0)
        {
            token.ThrowIfCancellationRequested();
            int read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read == 0) { throw new EndOfStreamException(); }
            output.Write(buffer, 0, read);
            remaining -= read;
        }
    }

    private static ReadOnlySpan<byte> StripExifPrefix(byte[] exif) => exif.AsSpan().StartsWith("Exif\0\0"u8) ? exif.AsSpan(6) : exif;
    private static byte[] AddExifPrefix(byte[] exif) => exif.AsSpan().StartsWith("Exif\0\0"u8) ? exif : [.. "Exif\0\0"u8, .. exif];

    private static void WriteJpegSegment(Stream stream, byte marker, byte[] data)
    {
        if (data.Length > 65533) { throw new ImageExportException(ImageExportError.BudgetExceeded); }
        Span<byte> header = stackalloc byte[4];
        header[0] = 255;
        header[1] = marker;
        BinaryPrimitives.WriteUInt16BigEndian(header[2..], (ushort)(data.Length + 2));
        stream.Write(header);
        stream.Write(data);
    }

    private static void WriteWebpChunk(Stream stream, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        stream.Write(type);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(length, (uint)data.Length);
        stream.Write(length);
        stream.Write(data);
        if ((data.Length & 1) != 0) { stream.WriteByte(0); }
    }

    private static void WritePngChunk(Stream stream, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(number, (uint)data.Length);
        stream.Write(number);
        stream.Write(type);
        stream.Write(data);
        uint crc = uint.MaxValue;
        foreach (byte value in type) { crc = UpdateCrc(crc, value); }
        foreach (byte value in data) { crc = UpdateCrc(crc, value); }
        BinaryPrimitives.WriteUInt32BigEndian(number, ~crc);
        stream.Write(number);
    }

    private static uint UpdateCrc(uint crc, byte value)
    {
        crc ^= value;
        for (int bit = 0; bit < 8; bit++) { crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0); }
        return crc;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
