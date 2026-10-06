using ModernImageViewer.Imaging;

namespace ModernImageViewer.Tests;

public sealed class ImageEditAdjustmentsTests
{
    [Fact]
    public void DefaultsAreIdentityAndWithCopiesRemainImmutable()
    {
        ImageEditAdjustments original = new();
        Assert.True(original.IsIdentity);
        Assert.Equal(1, original.Gamma);
        ImageEditAdjustments changed = original with { Exposure = 2, Blur = 3 };
        Assert.False(changed.IsIdentity);
        Assert.True(original.IsIdentity);
        Assert.Equal(original, changed with { Exposure = 0, Blur = 0 });
        Assert.Equal(changed, new ImageEditAdjustments(Exposure: 2, Blur: 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ImageEditAdjustments(Gamma: 0));
    }

    [Theory]
    [InlineData("Exposure", -5, 5)]
    [InlineData("Brightness", -100, 100)]
    [InlineData("Contrast", -100, 100)]
    [InlineData("Gamma", .1, 5)]
    [InlineData("Saturation", -100, 100)]
    [InlineData("Temperature", -100, 100)]
    [InlineData("Sharpen", 0, 100)]
    [InlineData("Blur", 0, 20)]
    public void EveryParameterRejectsOutOfRangeAndNonFiniteValues(string name, double minimum, double maximum)
    {
        ImageEditAdjustments identity = new();
        Assert.NotNull(Change(identity, name, minimum));
        Assert.NotNull(Change(identity, name, maximum));
        foreach (double value in new[] { minimum - .01, maximum + .01, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(() => Change(identity, name, value));
            Assert.Equal(name, exception.ParamName);
        }
        Assert.True(identity.IsIdentity);
    }

    private static ImageEditAdjustments Change(ImageEditAdjustments original, string name, double value) => name switch
    {
        "Exposure" => original with { Exposure = value },
        "Brightness" => original with { Brightness = value },
        "Contrast" => original with { Contrast = value },
        "Gamma" => original with { Gamma = value },
        "Saturation" => original with { Saturation = value },
        "Temperature" => original with { Temperature = value },
        "Sharpen" => original with { Sharpen = value },
        "Blur" => original with { Blur = value },
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };
}
