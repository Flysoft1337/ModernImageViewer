namespace ModernImageViewer.Imaging;

public sealed record ImageMetadata
{
    public static ImageMetadata Empty { get; } = new();
    public string? Camera { get; init; }
    public string? Lens { get; init; }
    public string? CapturedAt { get; init; }
    public uint? Iso { get; init; }
    public double? ExposureSeconds { get; init; }
    public double? Aperture { get; init; }
    public double? FocalLength { get; init; }
    public ushort Orientation { get; init; } = 1;
    public bool HasExif => Camera is not null || Lens is not null || CapturedAt is not null
        || Iso is not null || ExposureSeconds is not null || Aperture is not null || FocalLength is not null;
}
