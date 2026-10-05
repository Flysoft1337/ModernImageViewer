using System.Buffers.Binary;
using System.IO;

namespace ModernImageViewer.Codecs.Modern;

internal static class HeifContainer
{
    // Only the file-type box is inspected. Do not scan attacker-controlled box offsets or allocate its size.
    private const int MaximumFileTypeBytes = 4096;

    internal static bool TryDetect(Stream stream, out bool avif)
    {
        avif = false;
        long position = stream.Position;
        try
        {
            Span<byte> header = stackalloc byte[16];
            if (stream.ReadAtLeast(header, header.Length, false) != header.Length || !header[4..8].SequenceEqual("ftyp"u8))
            {
                return false;
            }
            uint length = BinaryPrimitives.ReadUInt32BigEndian(header);
            if (length is < 16 or > MaximumFileTypeBytes || (length % 4) != 0 || length > stream.Length - position)
            {
                return false;
            }
            bool recognized = IsBrand(header[8..12], ref avif);
            Span<byte> brand = stackalloc byte[4];
            for (int offset = 16; offset < length; offset += 4)
            {
                stream.ReadExactly(brand);
                recognized |= IsBrand(brand, ref avif);
            }
            return recognized;
        }
        finally { stream.Position = position; }
    }

    private static bool IsBrand(ReadOnlySpan<byte> brand, ref bool avif)
    {
        if (brand.SequenceEqual("avif"u8) || brand.SequenceEqual("avis"u8))
        {
            avif = true;
            return true;
        }
        return brand.SequenceEqual("heic"u8) || brand.SequenceEqual("heix"u8)
            || brand.SequenceEqual("hevc"u8) || brand.SequenceEqual("hevx"u8)
            || brand.SequenceEqual("mif1"u8) || brand.SequenceEqual("msf1"u8);
    }

    internal static bool HasSequenceBrand(Stream stream)
    {
        long position = stream.Position;
        try
        {
            Span<byte> header = stackalloc byte[16];
            stream.ReadExactly(header);
            uint length = BinaryPrimitives.ReadUInt32BigEndian(header);
            if (header[8..12].SequenceEqual("avis"u8) || header[8..12].SequenceEqual("msf1"u8)) { return true; }
            Span<byte> brand = stackalloc byte[4];
            for (int offset = 16; offset < length; offset += 4)
            {
                stream.ReadExactly(brand);
                if (brand.SequenceEqual("avis"u8) || brand.SequenceEqual("msf1"u8)) { return true; }
            }
            return false;
        }
        finally { stream.Position = position; }
    }
}
