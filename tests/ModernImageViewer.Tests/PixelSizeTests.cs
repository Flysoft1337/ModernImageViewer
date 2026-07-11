using ModernImageViewer.Imaging;

namespace ModernImageViewer.Tests;

public sealed class PixelSizeTests
{
    [Fact]
    public void ConstructorWithValidDimensionsSetsProperties()
    {
        PixelSize size = new(1920, 1080);

        Assert.Equal(1920, size.Width);
        Assert.Equal(1080, size.Height);
        Assert.Equal(2_073_600, size.PixelCount);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(-1, 1)]
    [InlineData(1, -1)]
    public void ConstructorWithInvalidDimensionThrows(int width, int height)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PixelSize(width, height));
    }

    [Fact]
    public void PixelCountUsesLongArithmetic()
    {
        PixelSize size = new(int.MaxValue, int.MaxValue);

        Assert.Equal(4_611_686_014_132_420_609, size.PixelCount);
    }
}
