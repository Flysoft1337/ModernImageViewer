using System.Runtime.InteropServices;

namespace ModernImageViewer.Imaging;

public static class PixelOrientation
{
    // The caller owns tightly packed BGRA pixels; cancellation may leave them partially transformed.
    public static PixelSize ApplyInPlace(byte[] pixels, PixelSize rawSize, ushort orientation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        ArgumentOutOfRangeException.ThrowIfLessThan(orientation, (ushort)1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(orientation, (ushort)8);
        int count = checked((int)rawSize.PixelCount);
        int byteCount = checked(count * 4);
        if (pixels.Length < byteCount)
        {
            throw new ArgumentException("The pixel buffer is too small.", nameof(pixels));
        }
        cancellationToken.ThrowIfCancellationRequested();
        Span<uint> values = MemoryMarshal.Cast<byte, uint>(pixels.AsSpan(0, byteCount));
        int width = rawSize.Width;
        int height = rawSize.Height;
        if (orientation == 1)
        {
            return rawSize;
        }
        if (orientation <= 4)
        {
            // Mirrors and 180 degrees consist of independent pairs, with no auxiliary pixel storage.
            for (int index = 0; index < count; index++)
            {
                if ((index & 4095) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
                int x = index % width;
                int y = index / width;
                int target = orientation switch
                {
                    2 => (y * width) + width - 1 - x,
                    3 => count - 1 - index,
                    _ => ((height - 1 - y) * width) + x,
                };
                if (target > index)
                {
                    (values[index], values[target]) = (values[target], values[index]);
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            return rawSize;
        }

        // Rectangular transposes are permutations of the same array. One bit per source pixel
        // marks finished cycles; no second BGRA array or full-image rendering surface is allocated.
        byte[] visited = new byte[checked((count + 7) / 8)];
        int moved = 0;
        for (int start = 0; start < count; start++)
        {
            if ((start & 4095) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            if ((visited[start >> 3] & (1 << (start & 7))) != 0)
            {
                continue;
            }
            int current = start;
            uint carried = values[current];
            do
            {
                if ((moved++ & 4095) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
                int x = current % width;
                int y = current / width;
                int target = orientation switch
                {
                    5 => (x * height) + y,
                    6 => (x * height) + height - 1 - y,
                    7 => ((width - 1 - x) * height) + height - 1 - y,
                    _ => ((width - 1 - x) * height) + y,
                };
                (values[target], carried) = (carried, values[target]);
                visited[current >> 3] |= (byte)(1 << (current & 7));
                current = target;
            }
            while (current != start);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new PixelSize(height, width);
    }
}
