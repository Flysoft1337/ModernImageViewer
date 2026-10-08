using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;

using ModernImageViewer.Application.Images;
using ModernImageViewer.Imaging;

using SkiaSharp;

namespace ModernImageViewer.Codecs.Frames;

// Skia deliberately clears WebP animation backgrounds to transparent. Decode the original
// ANMF bitstreams as static WebP images so Source transparency and ANIM disposal stay distinct.
internal sealed class WebPAnimationData
{
    internal sealed record Frame(PixelRect Bounds, long Offset, int Length, int Duration, bool Source, bool Background);
    private readonly byte[] _background;
    private readonly long _profileOffset;
    private readonly int _profileLength;

    private WebPAnimationData(PixelSize canvas, Frame[] frames, byte[] background, ushort loops,
        long profileOffset, int profileLength)
    {
        Canvas = canvas;
        Frames = frames;
        TotalPlays = loops == 0 ? null : loops;
        _background = background;
        _profileOffset = profileOffset;
        _profileLength = profileLength;
    }

    internal PixelSize Canvas { get; }
    internal Frame[] Frames { get; }
    internal int? TotalPlays { get; }

    internal static WebPAnimationData? TryRead(Stream stream, CancellationToken token)
    {
        long saved = stream.Position;
        try
        {
            token.ThrowIfCancellationRequested();
            if (stream.Length > ImageFrameLimits.MaximumInputBytes) { throw new ImageSizeLimitExceededException(); }
            stream.Position = 0;
            Span<byte> header = stackalloc byte[30];
            int read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
            if (read < 30 || !header[..4].SequenceEqual("RIFF"u8) || !header[8..12].SequenceEqual("WEBP"u8)
                || !header[12..16].SequenceEqual("VP8X"u8) || (header[20] & 2) == 0) { return null; }
            long end = 8L + BinaryPrimitives.ReadUInt32LittleEndian(header[4..8]);
            if (end > stream.Length || end < 30 || (end & 1) != 0
                || BinaryPrimitives.ReadUInt32LittleEndian(header[16..20]) != 10) { throw Corrupt(); }
            PixelSize canvas = new(U24(header[24..27]) + 1, U24(header[27..30]) + 1);
            bool hasProfile = (header[20] & 0x20) != 0;
            ImageDecodeLimits.Default.ValidateAndGetStride(canvas);
            List<Frame> frames = [];
            byte[]? background = null;
            ushort loops = 0;
            long profileOffset = 0;
            int profileLength = 0;
            long metadataBytes = 30;
            Span<byte> chunk = stackalloc byte[8];
            Span<byte> data = stackalloc byte[16];
            long position = 30;
            while (position < end)
            {
                token.ThrowIfCancellationRequested();
                stream.Position = position;
                if (end - position < 8 || stream.ReadAtLeast(chunk, 8, false) != 8) { throw Corrupt(); }
                uint length = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]);
                long next = position + 8 + length + (length & 1);
                if (next > end) { throw Corrupt(); }
                if (chunk[..4].SequenceEqual("ANIM"u8))
                {
                    if (length != 6 || background is not null || frames.Count != 0) { throw Corrupt(); }
                    stream.ReadExactly(data[..6]);
                    background = data[..4].ToArray();
                    loops = BinaryPrimitives.ReadUInt16LittleEndian(data[4..6]);
                    metadataBytes += 14;
                }
                else if (chunk[..4].SequenceEqual("ANMF"u8))
                {
                    if (background is null || length < 24) { throw Corrupt(); }
                    if (frames.Count == ImageFrameLimits.MaximumFrames) { throw new ImageSizeLimitExceededException(); }
                    stream.ReadExactly(data);
                    int x = U24(data[..3]) * 2;
                    int y = U24(data[3..6]) * 2;
                    int width = U24(data[6..9]) + 1;
                    int height = U24(data[9..12]) + 1;
                    if ((long)x + width > canvas.Width || (long)y + height > canvas.Height) { throw Corrupt(); }
                    long offset = position + 24;
                    int payloadLength = checked((int)length - 16);
                    ValidatePayload(stream, offset, payloadLength, new PixelSize(width, height), ref metadataBytes, token);
                    frames.Add(new(new PixelRect(x, y, width, height), offset, payloadLength,
                        U24(data[12..15]), (data[15] & 2) != 0, (data[15] & 1) != 0));
                    metadataBytes += 128;
                }
                else
                {
                    metadataBytes += 8L + length + (length & 1);
                    if (chunk[..4].SequenceEqual("ICCP"u8))
                    {
                        if (profileLength != 0 || frames.Count != 0 || length == 0) { throw Corrupt(); }
                        if (metadataBytes > ImageFrameLimits.MaximumMetadataBytes) { throw new ImageSizeLimitExceededException(); }
                        profileOffset = position;
                        profileLength = checked((int)(next - position));
                    }
                    else if (chunk[..4].SequenceEqual("VP8X"u8) || chunk[..4].SequenceEqual("VP8 "u8)
                        || chunk[..4].SequenceEqual("VP8L"u8) || chunk[..4].SequenceEqual("ALPH"u8)) { throw Corrupt(); }
                }
                if (metadataBytes > ImageFrameLimits.MaximumMetadataBytes) { throw new ImageSizeLimitExceededException(); }
                position = next;
            }
            if (background is null || frames.Count == 0 || hasProfile != (profileLength > 0)) { throw Corrupt(); }
            return new(canvas, frames.ToArray(), background, loops, profileOffset, profileLength);
        }
        finally { stream.Position = saved; }
    }

    private static void ValidatePayload(Stream stream, long offset, int length, PixelSize expected,
        ref long metadataBytes, CancellationToken token)
    {
        long end = offset + length;
        bool image = false;
        bool alpha = false;
        Span<byte> chunk = stackalloc byte[8];
        Span<byte> imageHeader = stackalloc byte[10];
        while (offset < end)
        {
            token.ThrowIfCancellationRequested();
            stream.Position = offset;
            if (end - offset < 8 || stream.ReadAtLeast(chunk, 8, false) != 8) { throw Corrupt(); }
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]);
            long next = offset + 8 + size + (size & 1);
            if (next > end) { throw Corrupt(); }
            if (chunk[..4].SequenceEqual("ALPH"u8))
            {
                if (alpha || image || size == 0) { throw Corrupt(); }
                alpha = true;
            }
            else if (chunk[..4].SequenceEqual("VP8 "u8) || chunk[..4].SequenceEqual("VP8L"u8))
            {
                if (image || size == 0 || (alpha && chunk[..4].SequenceEqual("VP8L"u8))) { throw Corrupt(); }
                int width;
                int height;
                if (chunk[..4].SequenceEqual("VP8L"u8))
                {
                    if (size < 5) { throw Corrupt(); }
                    stream.ReadExactly(imageHeader[..5]);
                    uint dimensions = BinaryPrimitives.ReadUInt32LittleEndian(imageHeader[1..5]);
                    if (imageHeader[0] != 0x2f || (dimensions >> 29) != 0) { throw Corrupt(); }
                    width = (int)(dimensions & 0x3fff) + 1;
                    height = (int)((dimensions >> 14) & 0x3fff) + 1;
                }
                else
                {
                    if (size < 10) { throw Corrupt(); }
                    stream.ReadExactly(imageHeader);
                    if ((imageHeader[0] & 1) != 0 || imageHeader[3] != 0x9d || imageHeader[4] != 0x01 || imageHeader[5] != 0x2a)
                    {
                        throw Corrupt();
                    }
                    width = BinaryPrimitives.ReadUInt16LittleEndian(imageHeader[6..8]) & 0x3fff;
                    height = BinaryPrimitives.ReadUInt16LittleEndian(imageHeader[8..10]) & 0x3fff;
                }
                if (width != expected.Width || height != expected.Height) { throw Corrupt(); }
                image = true;
            }
            else
            {
                if (chunk[..4].SequenceEqual("VP8X"u8) || chunk[..4].SequenceEqual("ANIM"u8)
                    || chunk[..4].SequenceEqual("ANMF"u8) || chunk[..4].SequenceEqual("ICCP"u8)) { throw Corrupt(); }
                metadataBytes += size;
            }
            metadataBytes += 8;
            if (metadataBytes > ImageFrameLimits.MaximumMetadataBytes) { throw new ImageSizeLimitExceededException(); }
            offset = next;
        }
        if (!image) { throw Corrupt(); }
    }

    internal void ConvertBackground(SKColorSpace? sourceColorSpace)
    {
        // Match the frame's ICC-to-sRGB transform before premultiplication; only one pixel.
        using SKColorSpace srgb = SKColorSpace.CreateSrgb();
        using SKBitmap source = new(new SKImageInfo(1, 1, SKColorType.Bgra8888, SKAlphaType.Unpremul, sourceColorSpace));
        Marshal.Copy(_background, 0, source.GetPixels(), 4);
        using SKPixmap pixmap = source.PeekPixels();
        using SKBitmap target = new(new SKImageInfo(1, 1, SKColorType.Bgra8888, SKAlphaType.Premul, srgb));
        if (!pixmap.ReadPixels(target.Info, target.GetPixels(), 4)) { throw Corrupt(); }
        Marshal.Copy(target.GetPixels(), _background, 0, 4);
    }

    internal int RequiredFrame(int index) => index == 0 || (Frames[index].Source && Frames[index].Bounds.Size == Canvas)
        ? -1 : index - 1;

    internal byte[] Decode(Stream source, int index, PixelSize size, byte[]? reference, int referenceIndex,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        long bytes = size.PixelCount * 4;
        if (bytes > ImageFrameLimits.MaximumFrameBytes) { throw new ImageSizeLimitExceededException(); }
        byte[] pixels = GC.AllocateUninitializedArray<byte>(checked((int)bytes), pinned: true);
        if (RequiredFrame(index) >= 0)
        {
            if (reference is null || referenceIndex != index - 1 || reference.Length != pixels.Length) { throw Corrupt(); }
            reference.CopyTo(pixels, 0);
            Frame previous = Frames[index - 1];
            if (previous.Background) { Fill(pixels, size.Width * 4, ScaleBounds(previous.Bounds, size), token); }
        }
        else { Fill(pixels, size.Width * 4, new SKRectI(0, 0, size.Width, size.Height), token); }
        Frame frame = Frames[index];
        SKRectI bounds = ScaleBounds(frame.Bounds, size);
        if (bounds.Width == 0 || bounds.Height == 0) { return pixels; }
        byte[] local = DecodeLocal(source, frame, bounds, token);
        int stride = checked(bounds.Width * 4);
        for (int y = 0; y < bounds.Height; y++)
        {
            token.ThrowIfCancellationRequested();
            Span<byte> destination = pixels.AsSpan(((y + bounds.Top) * size.Width + bounds.Left) * 4, stride);
            ReadOnlySpan<byte> row = local.AsSpan(y * stride, stride);
            if (frame.Source) { row.CopyTo(destination); }
            else
            {
                for (int x = 0; x < stride; x += 4)
                {
                    int inverse = 255 - row[x + 3];
                    for (int channel = 0; channel < 4; channel++)
                    {
                        destination[x + channel] = (byte)Math.Min(255, row[x + channel]
                            + ((destination[x + channel] * inverse + 127) / 255));
                    }
                }
            }
        }
        return pixels;
    }

    private byte[] DecodeLocal(Stream source, Frame frame, SKRectI bounds, CancellationToken token)
    {
        using FrameStream payload = new(source, frame, _profileOffset, _profileLength);
        using SKManagedStream managed = new(payload, disposeManagedStream: false);
        SKCodec codec = SKCodec.Create(managed) ?? throw Corrupt();
        SkiaImageFrameSession.DecoderCreated();
        try
        {
            if (codec.EncodedFormat != SKEncodedImageFormat.Webp || codec.FrameCount > 1
                || codec.Info.Width != frame.Bounds.Width || codec.Info.Height != frame.Bounds.Height) { throw Corrupt(); }
            int stride = checked(bounds.Width * 4);
            byte[] local = GC.AllocateUninitializedArray<byte>(checked(stride * bounds.Height), pinned: true);
            using SKColorSpace srgb = SKColorSpace.CreateSrgb();
            SKImageInfo info = new(bounds.Width, bounds.Height, SKColorType.Bgra8888, SKAlphaType.Premul, srgb);
            token.ThrowIfCancellationRequested();
            SKCodecResult result = codec.GetPixels(info, local);
            // An executing native decode cannot be interrupted. Do not publish its late result.
            token.ThrowIfCancellationRequested();
            if (result != SKCodecResult.Success) { throw Corrupt(); }
            return local;
        }
        finally
        {
            try { codec.Dispose(); }
            finally { SkiaImageFrameSession.DecoderReleased(); }
        }
    }

    private SKRectI ScaleBounds(PixelRect bounds, PixelSize size)
    {
        // Same conservative frame scaling as SkWebpCodec, in raw canvas pixels.
        int x = (int)((long)bounds.X * size.Width / Canvas.Width);
        int y = (int)((long)bounds.Y * size.Height / Canvas.Height);
        return new(x, y, x + (int)((long)bounds.Width * size.Width / Canvas.Width),
            y + (int)((long)bounds.Height * size.Height / Canvas.Height));
    }

    private void Fill(byte[] pixels, int stride, SKRectI bounds, CancellationToken token)
    {
        uint color = BinaryPrimitives.ReadUInt32LittleEndian(_background);
        for (int y = bounds.Top; y < bounds.Bottom; y++)
        {
            token.ThrowIfCancellationRequested();
            MemoryMarshal.Cast<byte, uint>(pixels.AsSpan(y * stride + bounds.Left * 4, bounds.Width * 4)).Fill(color);
        }
    }

    private static int U24(ReadOnlySpan<byte> value) => value[0] | (value[1] << 8) | (value[2] << 16);
    private static ImageDecodeException Corrupt() => new(ImageOpenError.CorruptFile);

    // Expose a tiny RIFF/VP8X header, optional original ICCP chunk, and the ANMF payload.
    // The shared seekable file stays owned by the session; no whole encoded-file copy.
    private sealed class FrameStream : Stream
    {
        private readonly Stream _source;
        private readonly Frame _frame;
        private readonly long _profileOffset;
        private readonly int _profileLength;
        private readonly byte[] _header = new byte[30];
        private long _position;

        internal FrameStream(Stream source, Frame frame, long profileOffset, int profileLength)
        {
            _source = source;
            _frame = frame;
            _profileOffset = profileOffset;
            _profileLength = profileLength;
            "RIFF"u8.CopyTo(_header);
            BinaryPrimitives.WriteUInt32LittleEndian(_header.AsSpan(4), checked((uint)(Length - 8)));
            "WEBPVP8X"u8.CopyTo(_header.AsSpan(8));
            BinaryPrimitives.WriteUInt32LittleEndian(_header.AsSpan(16), 10);
            _header[20] = (byte)(0x10 | (profileLength > 0 ? 0x20 : 0));
            WriteU24(_header.AsSpan(24), frame.Bounds.Width - 1);
            WriteU24(_header.AsSpan(27), frame.Bounds.Height - 1);
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _header.Length + _profileLength + _frame.Length;
        public override long Position { get => _position; set => Seek(value, SeekOrigin.Begin); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            int total = 0;
            while (buffer.Length > 0 && _position < Length)
            {
                int read;
                if (_position < _header.Length)
                {
                    read = Math.Min(buffer.Length, _header.Length - (int)_position);
                    _header.AsSpan((int)_position, read).CopyTo(buffer);
                }
                else
                {
                    long local = _position - _header.Length;
                    long offset;
                    long remaining;
                    if (local < _profileLength) { offset = _profileOffset + local; remaining = _profileLength - local; }
                    else { local -= _profileLength; offset = _frame.Offset + local; remaining = _frame.Length - local; }
                    _source.Position = offset;
                    read = _source.Read(buffer[..(int)Math.Min(buffer.Length, remaining)]);
                    if (read == 0) { throw new EndOfStreamException(); }
                }
                _position += read;
                total += read;
                buffer = buffer[read..];
            }
            return total;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            long position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => checked(_position + offset),
                SeekOrigin.End => checked(Length + offset),
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            };
            if (position < 0 || position > Length) { throw new IOException("Invalid frame stream position."); }
            return _position = position;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private static void WriteU24(Span<byte> destination, int value)
        {
            destination[0] = (byte)value;
            destination[1] = (byte)(value >> 8);
            destination[2] = (byte)(value >> 16);
        }
    }
}
