using ModernImageViewer.Application.Browsing;

namespace ModernImageViewer.Application.Settings;

public sealed record WindowPlacementData(
    double Left, double Top, double Width, double Height,
    double DpiScale = 1, bool IsMaximized = false)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsValid => double.IsFinite(Left) && double.IsFinite(Top)
        && Math.Abs(Left) <= 1_000_000 && Math.Abs(Top) <= 1_000_000
        && double.IsFinite(Width) && Width is >= 720 and <= 32768
        && double.IsFinite(Height) && Height is >= 480 and <= 32768
        && double.IsFinite(DpiScale) && DpiScale is >= 0.5 and <= 8;
}

public sealed record BrowsingPreferencesData(
    bool ShowInformation = false,
    bool ShowFilmstrip = true,
    BrowseSortMode SortMode = BrowseSortMode.Name,
    bool SortDescending = false,
    int SlideshowSeconds = 5)
{
    public BrowsingPreferencesData Normalize() => this with
    {
        SortMode = Enum.IsDefined(SortMode) ? SortMode : BrowseSortMode.Name,
        SlideshowSeconds = SlideshowSeconds is >= 2 and <= 30 ? SlideshowSeconds : 5,
    };
}

public sealed record UserSettingsSnapshot(
    string? Language = null,
    string? Theme = null,
    WindowPlacementData? WindowPlacement = null,
    BrowsingPreferencesData? Browsing = null)
{
    public UserSettingsSnapshot Normalize() => this with
    {
        WindowPlacement = WindowPlacement is { IsValid: true } ? WindowPlacement : null,
        Browsing = (Browsing ?? new()).Normalize(),
    };
}
