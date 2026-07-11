using System.ComponentModel;
using System.Runtime.CompilerServices;

using ModernImageViewer.UI.Localization;

namespace ModernImageViewer.UI.ViewModels;

public sealed class MainWindowViewModel : INotifyPropertyChanged
{
    private readonly ILocalizationService _localizationService;
    private SupportedLanguage _selectedLanguage;

    public MainWindowViewModel(ILocalizationService localizationService)
    {
        _localizationService = localizationService;
        _selectedLanguage = FindCurrentLanguage();
        _localizationService.CultureChanged += OnCultureChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Title => _localizationService.GetString("MainWindow_Title");

    public string StatusText => _localizationService.GetString("MainWindow_StatusSkeletonRunning");

    public string LanguageLabel => _localizationService.GetString("Language_Label");

    public IReadOnlyList<SupportedLanguage> SupportedLanguages => _localizationService.SupportedLanguages;

    public SupportedLanguage SelectedLanguage
    {
        get => _selectedLanguage;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (_selectedLanguage == value)
            {
                return;
            }

            _localizationService.SetCulture(value.CultureName);
        }
    }

    private void OnCultureChanged(object? sender, EventArgs e)
    {
        _selectedLanguage = FindCurrentLanguage();
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(LanguageLabel));
        OnPropertyChanged(nameof(SelectedLanguage));
    }

    private SupportedLanguage FindCurrentLanguage()
    {
        return _localizationService.SupportedLanguages.Single(
            language => language.CultureName == _localizationService.CurrentCulture.Name);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
