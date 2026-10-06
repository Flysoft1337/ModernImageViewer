using System.Buffers.Binary;
using System.IO;

namespace ModernImageViewer.UI.Tests.Fixtures;

// Project-owned, deterministic baseline TIFF: little-endian, RGB8, one uncompressed strip per IFD.
// No encoder or external sample is used. Expected RGB is (page*19+x+y, y*29, x*17), modulo 256.
internal static class TiffFixtures
{
    internal readonly record struct Page(int Width, int Height, ushort Orientation = 1, bool BrokenStrip = false);
    internal static byte[] Single() => Create(new Page(8, 5));
    internal static byte[] Multiple() => Create(new Page(8, 5), new Page(5, 3), new Page(2050, 4));
    internal static byte[] Orientations() => Create(Enumerable.Range(1, 8)
        .Select(i => new Page(8 + i, 5 + i, (ushort)i)).ToArray());
    internal static byte[] BrokenPage() => Create(new Page(8, 5), new Page(5, 3, BrokenStrip: true));
    internal static byte[] OversizedPage(int width, int height)
    {
        byte[] bytes = Create(new Page(8, 5), new Page(5, 3));
        // A valid one-bit second page keeps a >100MP source below the encoded-input limit.
        int stripBytes = checked(((width + 7) / 8) * height);
        Array.Resize(ref bytes, 416 + stripBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(162, 4), (uint)width);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(174, 4), (uint)height);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(182, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(186, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(210, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(246, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(258, 4), (uint)height);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(270, 4), (uint)stripBytes);
        return bytes;
    }

    internal static byte[] Create(params Page[] pages)
    {
        const int entries = 11;
        const int directoryBytes = 2 + entries * 12 + 4 + 6;
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);
        writer.Write((ushort)0x4949);
        writer.Write((ushort)42);
        writer.Write(8u);
        int pixelOffset = 8 + pages.Length * directoryBytes;
        for (int i = 0; i < pages.Length; i++)
        {
            Page page = pages[i];
            int directoryOffset = 8 + i * directoryBytes;
            writer.Write((ushort)entries);
            Entry(writer, 256, 4, 1, (uint)page.Width);
            Entry(writer, 257, 4, 1, (uint)page.Height);
            Entry(writer, 258, 3, 3, (uint)(directoryOffset + directoryBytes - 6));
            Entry(writer, 259, 3, 1, 1);
            Entry(writer, 262, 3, 1, 2);
            Entry(writer, 273, 4, 1, page.BrokenStrip ? uint.MaxValue - 16 : (uint)pixelOffset);
            Entry(writer, 274, 3, 1, page.Orientation);
            Entry(writer, 277, 3, 1, 3);
            Entry(writer, 278, 4, 1, (uint)page.Height);
            Entry(writer, 279, 4, 1, (uint)(page.Width * page.Height * 3));
            Entry(writer, 284, 3, 1, 1);
            writer.Write(i + 1 == pages.Length ? 0u : (uint)(directoryOffset + directoryBytes));
            writer.Write((ushort)8);
            writer.Write((ushort)8);
            writer.Write((ushort)8);
            pixelOffset += page.Width * page.Height * 3;
        }
        for (int i = 0; i < pages.Length; i++)
        {
            for (int y = 0; y < pages[i].Height; y++)
            {
                for (int x = 0; x < pages[i].Width; x++)
                {
                    writer.Write((byte)(i * 19 + x + y));
                    writer.Write((byte)(y * 29));
                    writer.Write((byte)(x * 17));
                }
            }
        }
        return stream.ToArray();
    }

    private static void Entry(BinaryWriter writer, ushort tag, ushort type, uint count, uint value)
    {
        writer.Write(tag);
        writer.Write(type);
        writer.Write(count);
        writer.Write(value);
    }
}
