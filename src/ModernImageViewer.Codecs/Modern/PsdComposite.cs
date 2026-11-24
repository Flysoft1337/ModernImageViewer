using System.Buffers.Binary;
using System.IO;

using ImageMagick;

using ModernImageViewer.Application.Images;
using ModernImageViewer.Imaging;

namespace ModernImageViewer.Codecs.Modern;

internal static class PsdComposite
{
    private const int HeaderBytes = 26;
    private const int VersionInfoResource = 1057;
    private const int MaximumResourceBytes = 16 * 1024 * 1024;
    private const int MaximumResources = 16_384;

    internal static bool TryDetect(Stream stream)
    {
        Span<byte> magic = stackalloc byte[4];
        int read = stream.ReadAtLeast(magic, magic.Length, throwOnEndOfStream: false);
        stream.Position = 0;
        return read == magic.Length && magic.SequenceEqual("8BPS"u8);
    }

    internal static Stream Open(Stream stream, long maximumPixels, CancellationToken cancellationToken, out PixelSize size)
    {
        try
        {
            Span<byte> header = stackalloc byte[HeaderBytes];
            stream.ReadExactly(header);
            if (!header[..4].SequenceEqual("8BPS"u8)) { throw Corrupt(); }
            if (BinaryPrimitives.ReadUInt16BigEndian(header[4..]) != 1)
            {
                throw new ImageDecodeException(ImageOpenError.UnsupportedFormat);
            }
            uint width = BinaryPrimitives.ReadUInt32BigEndian(header[18..]);
            uint height = BinaryPrimitives.ReadUInt32BigEndian(header[14..]);
            if (width == 0 || height == 0) { throw Corrupt(); }
            if (width > ImageDecodeLimits.Default.MaximumDimension || height > ImageDecodeLimits.Default.MaximumDimension
                || (ulong)width * height > (ulong)maximumPixels)
            {
                throw new ImageSizeLimitExceededException();
            }

            stream.Position = ReadSectionEnd(stream, stream.Length); // Color mode data
            long resourcesEnd = ReadSectionEnd(stream, stream.Length);
            if (resourcesEnd - stream.Position > MaximumResourceBytes) { throw new ImageSizeLimitExceededException(); }
            ValidateResources(stream, resourcesEnd, cancellationToken);
            long layerOffset = stream.Position;
            long compositeOffset = ReadSectionEnd(stream, stream.Length);
            byte[] layerHeader = ReadCompositeLayerHeader(stream, compositeOffset);
            stream.Position = compositeOffset;
            Span<byte> compression = stackalloc byte[2];
            stream.ReadExactly(compression);
            if (BinaryPrimitives.ReadUInt16BigEndian(compression) > 1)
            {
                throw new ImageDecodeException(ImageOpenError.UnsupportedFormat);
            }
            if (stream.Position == stream.Length) { throw Corrupt(); }
            size = new PixelSize((int)width, (int)height);
            return new CompositeStream(stream, layerOffset, layerHeader, compositeOffset);
        }
        catch (EndOfStreamException) { throw Corrupt(); }
        finally { stream.Position = 0; }
    }

    private static byte[] ReadCompositeLayerHeader(Stream stream, long sectionEnd)
    {
        if (sectionEnd == stream.Position) { return new byte[4]; }
        long layerInfoEnd = ReadSectionEnd(stream, sectionEnd);
        if (layerInfoEnd == stream.Position) { return [0, 0, 0, 8, 0, 0, 0, 0, 0, 0, 0, 0]; }
        if (layerInfoEnd - stream.Position < 2) { throw Corrupt(); }
        // Keep the merged-alpha flag from the signed count. The empty record list makes
        // any native fallback to layer decoding fail even if the merged data is corrupt.
        bool hasMergedAlpha = (stream.ReadByte() & 0x80) != 0;
        return [0, 0, 0, 10, 0, 0, 0, 2, hasMergedAlpha ? (byte)255 : (byte)0,
            hasMergedAlpha ? (byte)255 : (byte)0, 0, 0, 0, 0];
    }

    private sealed class CompositeStream(Stream source, long layerOffset, byte[] layerHeader, long compositeOffset) : Stream
    {
        private readonly long _payloadOffset = layerOffset + layerHeader.Length;
        private long _position;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _payloadOffset + source.Length - compositeOffset;
        public override long Position { get => _position; set => Seek(value, SeekOrigin.Begin); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ValidateBufferArguments(buffer, offset, count);
            return Read(buffer.AsSpan(offset, count));
        }

        public override int Read(Span<byte> buffer)
        {
            if (buffer.IsEmpty || _position >= Length) { return 0; }
            int read = 0;
            if (_position < layerOffset)
            {
                source.Position = _position;
                int count = (int)Math.Min(buffer.Length, layerOffset - _position);
                source.ReadExactly(buffer[..count]);
                _position += count;
                read = count;
            }
            if (_position >= layerOffset && _position < _payloadOffset)
            {
                int count = (int)Math.Min(buffer.Length - read, _payloadOffset - _position);
                layerHeader.AsSpan((int)(_position - layerOffset), count).CopyTo(buffer[read..]);
                _position += count;
                read += count;
            }
            if (_position >= _payloadOffset && read < buffer.Length)
            {
                source.Position = compositeOffset + _position - _payloadOffset;
                int count = source.Read(buffer[read..]);
                _position += count;
                read += count;
            }
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            long start = origin switch
            {
                SeekOrigin.Begin => 0,
                SeekOrigin.Current => _position,
                SeekOrigin.End => Length,
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            };
            long position = checked(start + offset);
            if (position < 0) { throw new IOException(); }
            return _position = position;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static void ValidateResources(Stream stream, long end, CancellationToken cancellationToken)
    {
        Span<byte> prefix = stackalloc byte[7];
        for (int index = 0; stream.Position < end; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (index >= MaximumResources) { throw new ImageSizeLimitExceededException(); }
            if (end - stream.Position < prefix.Length) { throw Corrupt(); }
            stream.ReadExactly(prefix);
            if (!prefix[..4].SequenceEqual("8BIM"u8)) { throw Corrupt(); }
            int id = BinaryPrimitives.ReadUInt16BigEndian(prefix[4..]);
            int nameBytes = prefix[6];
            stream.Position += nameBytes | 1; // Remaining bytes of an even-length Pascal string.
            long dataEnd = ReadSectionEnd(stream, end);
            long next = dataEnd + ((dataEnd - stream.Position) & 1);
            if (next > end) { throw Corrupt(); }
            if (id == VersionInfoResource && dataEnd - stream.Position >= 5)
            {
                stream.Position += 4;
                if (stream.ReadByte() == 0) { throw new ImageDecodeException(ImageOpenError.UnsupportedFormat); }
            }
            stream.Position = next;
        }
    }

    private static long ReadSectionEnd(Stream stream, long limit)
    {
        Span<byte> size = stackalloc byte[4];
        if (limit - stream.Position < size.Length) { throw Corrupt(); }
        stream.ReadExactly(size);
        uint length = BinaryPrimitives.ReadUInt32BigEndian(size);
        if (length > limit - stream.Position) { throw Corrupt(); }
        return stream.Position + length;
    }

    internal static MagickReadSettings CreateReadSettings() => new()
    {
        Format = MagickFormat.Psd,
        FrameIndex = 0,
        FrameCount = 1,
        SyncImageWithExifProfile = false,
    };

    internal static void ConvertToSrgb(MagickImage image)
    {
        if (image.GetColorProfile() is not null) { image.TransformColorSpace(ColorProfiles.SRGB); }
        else if (image.ColorSpace != ColorSpace.sRGB) { image.ColorSpace = ColorSpace.sRGB; }
    }

    internal static void PremultiplyRow(Span<byte> pixels)
    {
        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            int alpha = pixels[offset + 3];
            if (alpha == 255) { continue; }
            for (int channel = 0; channel < 3; channel++)
            {
                int index = offset + channel;
                pixels[index] = (byte)((pixels[index] * alpha + 127) / 255);
            }
        }
    }

    private static ImageDecodeException Corrupt() => new(ImageOpenError.CorruptFile);
}
