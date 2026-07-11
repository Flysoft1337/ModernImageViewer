using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

using ModernImageViewer.Application.Images;
using ModernImageViewer.Imaging;
using ModernImageViewer.UI.Commands;
using ModernImageViewer.UI.Localization;

namespace ModernImageViewer.UI.ViewModels;

public sealed class MainWindowViewModel : INotifyPropertyChanged
{
    private readonly ILocalizationService _localizationService;
    private readonly ImageOpenCoordinator _coordinator;
    private SupportedLanguage _selectedLanguage;

    public MainWindowViewModel(ILocalizationService localizationService, ImageOpenCoordinator coordinator)
    {
        _localizationService = localizationService;
        _coordinator = coordinator;
        _selectedLanguage = FindCurrentLanguage();
        OpenCommand = new AsyncRelayCommand(() => _coordinator.PickAndOpenAsync());
        _localizationService.CultureChanged += OnCultureChanged;
        _coordinator.PropertyChanged += OnCoordinatorPropertyChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Title => _coordinator.State.FileName is null
        ? _localizationService.GetString("MainWindow_Title")
        : $"{_coordinator.State.FileName} — {_localizationService.GetString("MainWindow_Title")}";

    public string StatusText => _coordinator.State.Status switch
    {
        ImageOpenStatus.Empty => _localizationService.GetString("Status_Empty"),
        ImageOpenStatus.Loading => _localizationService.GetString("Status_Loading"),
        ImageOpenStatus.Loaded => _coordinator.State.FileName ?? string.Empty,
        ImageOpenStatus.Error => _localizationService.GetString($"Error_{_coordinator.State.Error}"),
        _ => string.Empty,
    };

    public string LanguageLabel => _localizationService.GetString("Language_Label");

    public string OpenLabel => _localizationService.GetString("Command_Open");

    public string FitLabel => _localizationService.GetString("Command_Fit");

    public string ActualSizeLabel => _localizationService.GetString("Command_ActualSize");

    public PixelBuffer? CurrentImage => _coordinator.State.Image;

    public bool IsLoading => _coordinator.State.Status == ImageOpenStatus.Loading;

    public ICommand OpenCommand { get; }

    public IReadOnlyList<SupportedLanguage> SupportedLanguages => _localizationService.SupportedLanguages;

    public SupportedLanguage SelectedLanguage
    {
        get => _selectedLanguage;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (_selectedLanguage != value)
            {
                _localizationService.SetCulture(value.CultureName);
            }
        }
    }

    private void OnCultureChanged(object? sender, EventArgs e)
    {
        _selectedLanguage = FindCurrentLanguage();
        NotifyAll();
    }

    private void OnCoordinatorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        NotifyAll();
    }

    private void NotifyAll()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(LanguageLabel));
        OnPropertyChanged(nameof(OpenLabel));
        OnPropertyChanged(nameof(FitLabel));
        OnPropertyChanged(nameof(ActualSizeLabel));
        OnPropertyChanged(nameof(CurrentImage));
        OnPropertyChanged(nameof(IsLoading));
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
