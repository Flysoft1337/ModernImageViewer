using System.Windows.Media.Imaging;

using ModernImageViewer.Imaging;

namespace ModernImageViewer.Codecs.Wic;

internal static class WicMetadataReader
{
    public static ImageMetadata Read(BitmapFrame frame)
    {
        try
        {
            if (frame.Metadata is not BitmapMetadata metadata)
            {
                return ImageMetadata.Empty;
            }

            string? make = Text(Query(metadata, "/app1/ifd/{ushort=271}") ?? Query(metadata, "/ifd/{ushort=271}"));
            string? model = Text(Query(metadata, "/app1/ifd/{ushort=272}") ?? Query(metadata, "/ifd/{ushort=272}"));
            string? camera = model is null ? make : make is null || model.StartsWith(make, StringComparison.OrdinalIgnoreCase) ? model : $"{make} {model}";
            string prefix = "/app1/ifd/exif/";
            object? Exif(ushort tag) => Query(metadata, $"{prefix}{{ushort={tag}}}") ?? Query(metadata, $"/ifd/exif/{{ushort={tag}}}");
            object? orientation = Query(metadata, "/app1/ifd/{ushort=274}") ?? Query(metadata, "/ifd/{ushort=274}");
            return new ImageMetadata
            {
                Camera = camera,
                Lens = Text(Exif(42036)),
                CapturedAt = Text(Exif(36867)),
                Iso = Unsigned(Exif(34855)),
                ExposureSeconds = Rational(Exif(33434)),
                Aperture = Rational(Exif(33437)),
                FocalLength = Rational(Exif(37386)),
                Orientation = orientation is ushort value && value is >= 1 and <= 8 ? value : (ushort)1,
            };
        }
        catch (Exception exception) when (IsMetadataError(exception))
        {
            // Optional/broken EXIF must never stop the image itself from opening.
            return ImageMetadata.Empty;
        }
    }

    private static object? Query(BitmapMetadata metadata, string query)
    {
        try
        {
            return metadata.GetQuery(query);
        }
        catch (Exception exception) when (IsMetadataError(exception))
        {
            return null;
        }
    }

    private static bool IsMetadataError(Exception exception) => exception is NotSupportedException
        or ArgumentException or InvalidOperationException or System.IO.IOException
        or System.Runtime.InteropServices.COMException;

    private static string? Text(object? value) => value is string text && !string.IsNullOrWhiteSpace(text)
        ? text.TrimEnd('\0').Trim() : null;

    private static uint? Unsigned(object? value) => value switch
    {
        ushort number => number,
        uint number => number,
        ushort[] { Length: > 0 } numbers => numbers[0],
        _ => null,
    };

    private static double? Rational(object? value)
    {
        if (value is not ulong packed)
        {
            return null;
        }
        uint numerator = (uint)packed;
        uint denominator = (uint)(packed >> 32);
        return denominator == 0 ? null : (double)numerator / denominator;
    }
}
