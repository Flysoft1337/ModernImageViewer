namespace ModernImageViewer.Application.Images;

public static class SupportedImageFormats
{
    public static IReadOnlyList<string> Extensions { get; } = Array.AsReadOnly<string>(
        [".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff", ".ico", ".webp", ".jxr", ".wdp", ".hdp", ".svg"]);

    private static readonly HashSet<string> s_extensions = new(Extensions, StringComparer.OrdinalIgnoreCase);

    public static string PickerPattern { get; } = string.Join(';', Extensions.Select(extension => "*" + extension));

    public static bool IsSupportedExtension(string? extension) =>
        extension is not null && s_extensions.Contains(extension);

    public static bool IsSupported(string path) => IsSupportedExtension(Path.GetExtension(path));
}
