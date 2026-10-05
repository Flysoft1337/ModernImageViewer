using System.Runtime.InteropServices;

using ModernImageViewer.Imaging;

namespace ModernImageViewer.Tests;

public sealed class PixelOrientationTests
{
    [Theory]
    [InlineData(3, 2)]
    [InlineData(4, 3)]
    [InlineData(1, 7)]
    [InlineData(5, 1)]
    [InlineData(1, 1)]
    public void AllEightOrientationsMatchSourceCoordinateMapping(int width, int height)
    {
        for (ushort orientation = 1; orientation <= 8; orientation++)
        {
            uint[] source = Enumerable.Range(0, width * height).Select(index => (uint)index + 0xFF000000).ToArray();
            byte[] pixels = MemoryMarshal.AsBytes(source.AsSpan()).ToArray();
            PixelSize output = PixelOrientation.ApplyInPlace(pixels, new PixelSize(width, height), orientation,
                TestContext.Current.CancellationToken);
            Assert.Equal(orientation >= 5 ? new PixelSize(height, width) : new PixelSize(width, height), output);
            uint[] actual = MemoryMarshal.Cast<byte, uint>(pixels).ToArray();
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    (int dx, int dy) = orientation switch
                    {
                        1 => (x, y),
                        2 => (width - 1 - x, y),
                        3 => (width - 1 - x, height - 1 - y),
                        4 => (x, height - 1 - y),
                        5 => (y, x),
                        6 => (height - 1 - y, x),
                        7 => (height - 1 - y, width - 1 - x),
                        _ => (y, width - 1 - x),
                    };
                    Assert.Equal(source[(y * width) + x], actual[(dy * output.Width) + dx]);
                }
            }
        }
    }

    [Fact]
    public void CancellationAndInvalidInputsAreRejectedBeforeMutation()
    {
        byte[] pixels = Enumerable.Range(0, 24).Select(index => (byte)index).ToArray();
        byte[] original = (byte[])pixels.Clone();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            PixelOrientation.ApplyInPlace(pixels, new PixelSize(3, 2), 6, cancellation.Token));
        Assert.Equal(original, pixels);
        Assert.Throws<ArgumentException>(() =>
            PixelOrientation.ApplyInPlace(new byte[23], new PixelSize(3, 2), 1, CancellationToken.None));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PixelOrientation.ApplyInPlace(pixels, new PixelSize(3, 2), 9, CancellationToken.None));
    }
}
