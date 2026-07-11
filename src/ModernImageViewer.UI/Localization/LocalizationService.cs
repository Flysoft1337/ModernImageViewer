using System.Globalization;
using System.Resources;

using ModernImageViewer.Application.Settings;

namespace ModernImageViewer.UI.Localization;

public sealed class LocalizationService : ILocalizationService
{
    private const string EnglishCultureName = "en-US";
    private const string SimplifiedChineseCultureName = "zh-CN";

    private static readonly ResourceManager ResourceManager = new(
        "ModernImageViewer.UI.Resources.Strings",
        typeof(LocalizationService).Assembly);

    private readonly IUserSettingsService _userSettings;

    public LocalizationService(IUserSettingsService userSettings)
    {
        _userSettings = userSettings;
        CurrentCulture = CultureInfo.GetCultureInfo(EnglishCultureName);
        SupportedLanguages =
        [
            new(EnglishCultureName, "English"),
            new(SimplifiedChineseCultureName, "中文"),
        ];
    }

    public event EventHandler? CultureChanged;

    public CultureInfo CurrentCulture { get; private set; }

    public IReadOnlyList<SupportedLanguage> SupportedLanguages { get; }

    public void Initialize()
    {
        string cultureName = NormalizeCultureName(_userSettings.Language ?? CultureInfo.CurrentUICulture.Name);
        ApplyCulture(cultureName, save: false);
    }

    public string GetString(string name)
    {
        return ResourceManager.GetString(name, CurrentCulture) ?? name;
    }

    public void SetCulture(string cultureName)
    {
        ApplyCulture(NormalizeCultureName(cultureName), save: true);
    }

    public static string NormalizeCultureName(string? cultureName)
    {
        if (string.Equals(cultureName, SimplifiedChineseCultureName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(cultureName, "zh-SG", StringComparison.OrdinalIgnoreCase)
            || string.Equals(cultureName, "zh-Hans", StringComparison.OrdinalIgnoreCase))
        {
            return SimplifiedChineseCultureName;
        }

        return EnglishCultureName;
    }

    private void ApplyCulture(string cultureName, bool save)
    {
        if (string.Equals(CurrentCulture.Name, cultureName, StringComparison.OrdinalIgnoreCase) && !save)
        {
            SetThreadCultures(CurrentCulture);
            return;
        }

        CurrentCulture = CultureInfo.GetCultureInfo(cultureName);
        SetThreadCultures(CurrentCulture);

        if (save)
        {
            _userSettings.SaveLanguage(CurrentCulture.Name);
        }

        CultureChanged?.Invoke(this, EventArgs.Empty);
    }

    private static void SetThreadCultures(CultureInfo culture)
    {
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
    }
}
