namespace ModernImageViewer.Imaging;

public sealed record ImageDecodeLimits(int MaximumDimension, long MaximumPixels, long MaximumDecodedBytes)
{
    public static ImageDecodeLimits Default { get; } = new(32_768, 100_000_000, 400_000_000);

    public int ValidateAndGetStride(PixelSize size)
    {
        if (size.Width > MaximumDimension || size.Height > MaximumDimension || size.PixelCount > MaximumPixels)
        {
            throw new ImageSizeLimitExceededException();
        }

        int stride = checked(size.Width * 4);
        long decodedBytes = checked((long)stride * size.Height);
        if (decodedBytes > MaximumDecodedBytes || decodedBytes > int.MaxValue)
        {
            throw new ImageSizeLimitExceededException();
        }

        return stride;
    }
}

public sealed class ImageSizeLimitExceededException : Exception;
