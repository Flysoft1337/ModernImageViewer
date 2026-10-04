using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

using ModernImageViewer.Application.Images;
using ModernImageViewer.Imaging;

using ImageMetadata = ModernImageViewer.Imaging.ImageMetadata;

namespace ModernImageViewer.Codecs.Wic;

public sealed class WicImageDecoder : IImageDecoder
{
    private static readonly SemaphoreSlim DecodeSlot = new(1);
    private readonly ImageDecodeLimits _limits = ImageDecodeLimits.Default;

    public async Task<PixelBuffer> DecodeAsync(string path, CancellationToken cancellationToken)
    {
        // Native WIC work cannot always stop midway. Queue later requests instead of
        // allocating several full-resolution images while the user navigates quickly.
        await DecodeSlot.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(() => Decode(path, cancellationToken), cancellationToken);
        }
        finally
        {
            DecodeSlot.Release();
        }
    }

    private PixelBuffer Decode(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            BitmapDecoder decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.DelayCreation,
                BitmapCacheOption.None);

            Guid containerFormat = decoder.CodecInfo.ContainerFormat;
            if (decoder.Frames.Count == 0 || (containerFormat != new Guid("19E4A5AA-5662-4FC5-A0C0-1758028E1057")
                && containerFormat != new Guid("1B7CFAF4-713F-473C-BBCD-6137425FAEAF")))
            {
                throw new ImageDecodeException(ImageOpenError.UnsupportedFormat);
            }

            BitmapFrame frame = decoder.Frames[0];
            PixelSize size = new(frame.PixelWidth, frame.PixelHeight);
            int stride = _limits.ValidateAndGetStride(size);
            ImageMetadata metadata = WicMetadataReader.Read(frame);
            cancellationToken.ThrowIfCancellationRequested();
            BitmapSource source = frame;
            if (metadata.Orientation != 1)
            {
                var matrix = ImageOrientation.GetMatrix(metadata.Orientation);
                source = new TransformedBitmap(frame, new MatrixTransform(
                    matrix.M11, matrix.M12, matrix.M21, matrix.M22, 0, 0));
                size = new PixelSize(source.PixelWidth, source.PixelHeight);
                stride = _limits.ValidateAndGetStride(size);
            }

            byte[] pixels = GC.AllocateUninitializedArray<byte>(checked(stride * size.Height), pinned: true);
            BitmapSource converted = source.Format == PixelFormats.Pbgra32
                ? source : new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
            cancellationToken.ThrowIfCancellationRequested();
            converted.CopyPixels(pixels, stride, 0);
            cancellationToken.ThrowIfCancellationRequested();

            return new PixelBuffer(size, stride, pixels, metadata);
        }
        catch (ImageDecodeException)
        {
            throw;
        }
        catch (FileFormatException exception)
        {
            throw new ImageDecodeException(ImageOpenError.CorruptFile, exception);
        }
        catch (NotSupportedException exception)
        {
            throw new ImageDecodeException(ImageOpenError.UnsupportedFormat, exception);
        }
    }
}
