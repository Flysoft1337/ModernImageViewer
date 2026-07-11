using System.Globalization;

namespace ModernImageViewer.UI.Localization;

public interface ILocalizationService
{
    event EventHandler? CultureChanged;

    CultureInfo CurrentCulture { get; }

    IReadOnlyList<SupportedLanguage> SupportedLanguages { get; }

    void Initialize();

    string GetString(string name);

    void SetCulture(string cultureName);
}
