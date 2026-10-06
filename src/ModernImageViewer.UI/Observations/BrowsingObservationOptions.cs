using System.Globalization;


namespace ModernImageViewer.UI.Observations;

/// <summary>Configured by App only when a developer explicitly supplies an output destination.</summary>
public sealed record BrowsingObservationOptions(string OutputPath, int NeighborSwitches, int RapidBurst, int RefreshEvery, int DetailEvery)
{
    public static BrowsingObservationOptions? Current { get; set; }

    public static BrowsingObservationOptions? FromEnvironment()
    {
        string? output = Environment.GetEnvironmentVariable("MIV_BROWSING_OBSERVATION_OUTPUT");
        if (string.IsNullOrWhiteSpace(output)) { return null; }
        return new(output, ReadCount("MIV_BROWSING_SWITCHES", 0, 1000),
            ReadCount("MIV_BROWSING_RAPID_BURST", 0, 20), ReadCount("MIV_BROWSING_REFRESH_EVERY", 0, 1000),
            ReadCount("MIV_BROWSING_DETAIL_EVERY", 0, 1000));
    }

    private static int ReadCount(string name, int minimum, int maximum) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.None, CultureInfo.InvariantCulture, out int value)
            ? Math.Clamp(value, minimum, maximum) : minimum;
}
