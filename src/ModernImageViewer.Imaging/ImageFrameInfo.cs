namespace ModernImageViewer.Imaging;

public enum ImageSequenceKind { Static, Animation, Pages }
public enum ImageFrameBlend { Source, Over }
public enum ImageFrameDisposal { Keep, Background, Previous }

public sealed record ImageFrameInfo(int Index, PixelSize CanvasSize, PixelRect Bounds,
    int RawDurationMilliseconds = 0, int DurationMilliseconds = 0,
    ImageFrameBlend Blend = ImageFrameBlend.Source,
    ImageFrameDisposal Disposal = ImageFrameDisposal.Keep,
    int RequiredFrame = -1, bool RequiresComposition = false);

public sealed record ImageSequenceInfo(ImageSequenceKind Kind, IReadOnlyList<ImageFrameInfo> Frames,
    int? TotalPlays = 1, bool CanRandomAccess = true)
{
    public int Count => Frames.Count;
    public long DurationMilliseconds => Frames.Sum(frame => (long)frame.DurationMilliseconds);
}

public static class ImageFrameLimits
{
    public const long MaximumPixelBytes = 32L * 1024 * 1024;
    public const long MaximumFrameBytes = MaximumPixelBytes / 4;
    public const long MaximumInputBytes = 256L * 1024 * 1024;
    public const int MaximumFrames = 10_000;
    public const long MaximumMetadataBytes = 2L * 1024 * 1024;

    public static PixelSize Fit(PixelSize source, PixelSize target, long maximumBytes = MaximumFrameBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        if (maximumBytes < 4) { throw new ImageSizeLimitExceededException(); }
        double scale = Math.Min(1, Math.Min((double)target.Width / source.Width, (double)target.Height / source.Height));
        scale = Math.Min(scale, Math.Sqrt((double)(maximumBytes / 4) / source.PixelCount));
        int width = Math.Max(1, (int)Math.Floor(source.Width * scale));
        int height = Math.Max(1, (int)Math.Floor(source.Height * scale));
        long pixels = maximumBytes / 4;
        if ((long)width * height > pixels)
        {
            if (width >= height) { width = (int)(pixels / height); }
            else { height = (int)(pixels / width); }
        }
        return new(width, height);
    }
}
