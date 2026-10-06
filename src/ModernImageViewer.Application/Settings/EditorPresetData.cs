using System.Text.Json.Serialization;

namespace ModernImageViewer.Application.Settings;

public sealed record EditorPresetData(string Name, int Width, int Height,
    string CropRatio = "Free", int Format = 0, int Quality = 90,
    bool WebpLossless = false, bool PreserveCamera = false)
{
    public const int MaximumCount = 16;
    public const int MaximumNameLength = 40;

    [JsonIgnore]
    public bool IsValid => !string.IsNullOrWhiteSpace(Name) && Name.Trim().Length <= MaximumNameLength
        && !Name.Any(char.IsControl) && Width is >= 1 and <= 32768 && Height is >= 1 and <= 32768
        && (long)Width * Height * 4 <= 64L * 1024 * 1024
        && IsKnownCropRatio(CropRatio) && Format is >= 0 and <= 2 && Quality is >= 1 and <= 100;

    public EditorPresetData? Normalize()
    {
        string name = Name?.Trim() ?? string.Empty;
        if (name.Length is < 1 or > MaximumNameLength || name.Any(char.IsControl)
            || Width is < 1 or > 32768 || Height is < 1 or > 32768
            || (long)Width * Height * 4 > 64L * 1024 * 1024)
        {
            return null;
        }
        return this with
        {
            Name = name,
            CropRatio = IsKnownCropRatio(CropRatio) ? CropRatio : "Free",
            Format = Format is >= 0 and <= 2 ? Format : 0,
            Quality = Math.Clamp(Quality, 1, 100),
        };
    }

    public static bool IsKnownCropRatio(string? value) => value is "Free" or "Original"
        or "1:1" or "4:3" or "3:2" or "16:9" or "9:16";

    public static IReadOnlyList<EditorPresetData>? NormalizeList(IReadOnlyList<EditorPresetData>? presets)
    {
        if (presets is null || presets.Count == 0) { return null; }
        List<EditorPresetData> valid = new(MaximumCount);
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (EditorPresetData? item in presets)
        {
            if (item?.Normalize() is not { } preset || !names.Add(preset.Name)) { continue; }
            valid.Add(preset);
            if (valid.Count == MaximumCount) { break; }
        }
        return valid.Count == 0 ? null : valid.AsReadOnly();
    }
}
