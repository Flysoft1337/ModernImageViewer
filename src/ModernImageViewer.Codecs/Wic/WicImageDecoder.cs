using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

using ModernImageViewer.Application.Images;
using ModernImageViewer.Imaging;

namespace ModernImageViewer.Codecs.Wic;

public sealed class WicImageDecoder : IImageDecoder
{
    private readonly ImageDecodeLimits _limits = ImageDecodeLimits.Default;

    public Task<PixelBuffer> DecodeAsync(string path, CancellationToken cancellationToken)
    {
        return Task.Run(() => Decode(path, cancellationToken), cancellationToken);
    }

    private PixelBuffer Decode(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            BitmapDecoder decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);

            Guid containerFormat = decoder.CodecInfo.ContainerFormat;
            if (decoder.Frames.Count == 0 || (containerFormat != new Guid("19E4A5AA-5662-4FC5-A0C0-1758028E1057")
                && containerFormat != new Guid("1B7CFAF4-713F-473C-BBCD-6137425FAEAF")))
            {
                throw new ImageDecodeException(ImageOpenError.UnsupportedFormat);
            }

            BitmapFrame frame = decoder.Frames[0];
            PixelSize size = new(frame.PixelWidth, frame.PixelHeight);
            int stride = _limits.ValidateAndGetStride(size);
            byte[] pixels = new byte[checked(stride * size.Height)];

            FormatConvertedBitmap converted = new(frame, PixelFormats.Pbgra32, null, 0);
            converted.CopyPixels(pixels, stride, 0);
            cancellationToken.ThrowIfCancellationRequested();

            return new PixelBuffer(size, stride, pixels);
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
