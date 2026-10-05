namespace ModernImageViewer.Application.Images;

public static class SupportedImageFormats
{
    public static IReadOnlyList<string> RawExtensions { get; } = Array.AsReadOnly<string>(
        [".dng", ".cr2", ".cr3", ".nef", ".arw", ".raf", ".rw2", ".orf", ".pef"]);

    public static IReadOnlyList<string> Extensions { get; } = Array.AsReadOnly<string>(
        [".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff", ".ico", ".webp", ".jxr", ".wdp", ".hdp", ".svg",
         ".avif", ".heif", ".heic", .. RawExtensions]);

    private static readonly HashSet<string> s_extensions = new(Extensions, StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> s_rawExtensions = new(RawExtensions, StringComparer.OrdinalIgnoreCase);

    public static bool IsRawExtension(string? extension) => extension is not null && s_rawExtensions.Contains(extension);

    public static string PickerPattern { get; } = string.Join(';', Extensions.Select(extension => "*" + extension));

    public static bool IsSupportedExtension(string? extension) =>
        extension is not null && s_extensions.Contains(extension);

    public static bool IsSupported(string path) => IsSupportedExtension(Path.GetExtension(path));
}
