using System.Globalization;
using System.Text.Json;

namespace ModernImageViewer.UI.Observations;

public sealed record AnimationObservationOptions(string OutputPath, IReadOnlyList<string> Inputs,
    int Loops, int LongSeconds)
{
    public static AnimationObservationOptions? Current { get; set; }

    public static AnimationObservationOptions? FromEnvironment()
    {
        string? output = Environment.GetEnvironmentVariable("MIV_ANIMATION_OBSERVATION_OUTPUT");
        if (string.IsNullOrWhiteSpace(output)) { return null; }
        string[] inputs;
        try
        {
            inputs = JsonSerializer.Deserialize<string[]>(Environment.GetEnvironmentVariable("MIV_ANIMATION_OBSERVATION_INPUTS") ?? "[]") ?? [];
        }
        catch (JsonException) { inputs = []; }
        return new(output, inputs, ReadCount("MIV_ANIMATION_LOOPS", 1, 1000, 1),
            ReadCount("MIV_ANIMATION_LONG_SECONDS", 0, 3600, 0));
    }

    private static int ReadCount(string name, int minimum, int maximum, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.None, CultureInfo.InvariantCulture, out int value)
            ? Math.Clamp(value, minimum, maximum) : fallback;
}
