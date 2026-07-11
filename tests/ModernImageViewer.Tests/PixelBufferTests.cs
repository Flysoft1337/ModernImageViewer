using ModernImageViewer.Imaging;

namespace ModernImageViewer.Tests;

public sealed class PixelBufferTests
{
    [Fact]
    public void ConstructorRejectsBufferThatIsTooSmall()
    {
        Assert.Throws<ArgumentException>(() => new PixelBuffer(new PixelSize(2, 2), 8, new byte[15]));
    }

    [Fact]
    public void DisposeMakesPixelsUnavailable()
    {
        PixelBuffer buffer = new(new PixelSize(1, 1), 4, new byte[4]);

        buffer.Dispose();

        Assert.Throws<ObjectDisposedException>(() => buffer.Pixels);
    }

    [Fact]
    public void LimitsRejectOversizedImageBeforeAllocation()
    {
        ImageDecodeLimits limits = new(100, 10_000, 40_000);

        Assert.Throws<ImageSizeLimitExceededException>(() => limits.ValidateAndGetStride(new PixelSize(101, 1)));
    }
}
