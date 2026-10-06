namespace ModernImageViewer.Imaging;

/// <summary>Validated, immutable effect parameters; stores no pixel data.</summary>
public sealed record ImageEditAdjustments
{
    public ImageEditAdjustments(double Exposure = 0, double Brightness = 0, double Contrast = 0, double Gamma = 1,
        double Saturation = 0, double Temperature = 0, double Sharpen = 0, double Blur = 0)
    {
        this.Exposure = Exposure;
        this.Brightness = Brightness;
        this.Contrast = Contrast;
        this.Gamma = Gamma;
        this.Saturation = Saturation;
        this.Temperature = Temperature;
        this.Sharpen = Sharpen;
        this.Blur = Blur;
    }

    public double Exposure { get; init => field = Validate(value, -5, 5, nameof(Exposure)); }
    public double Brightness { get; init => field = Validate(value, -100, 100, nameof(Brightness)); }
    public double Contrast { get; init => field = Validate(value, -100, 100, nameof(Contrast)); }
    public double Gamma { get; init => field = Validate(value, .1, 5, nameof(Gamma)); } = 1;
    public double Saturation { get; init => field = Validate(value, -100, 100, nameof(Saturation)); }
    public double Temperature { get; init => field = Validate(value, -100, 100, nameof(Temperature)); }
    public double Sharpen { get; init => field = Validate(value, 0, 100, nameof(Sharpen)); }
    public double Blur { get; init => field = Validate(value, 0, 20, nameof(Blur)); }

    public bool IsNeutral => Exposure == 0 && Brightness == 0 && Contrast == 0 && Gamma == 1
        && Saturation == 0 && Temperature == 0 && Sharpen == 0 && Blur == 0;
    public bool IsIdentity => IsNeutral;

    private static double Validate(double value, double minimum, double maximum, string name)
    {
        if (!double.IsFinite(value) || value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(name);
        }
        return value;
    }
}
