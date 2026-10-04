using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;

using ModernImageViewer.Application.Browsing;
using ModernImageViewer.Application.Images;
using ModernImageViewer.Imaging;
using ModernImageViewer.UI.Commands;
using ModernImageViewer.UI.Localization;

namespace ModernImageViewer.UI.ViewModels;

public sealed class MainWindowViewModel : INotifyPropertyChanged
{
    private readonly ILocalizationService _localization;
    private readonly ImageOpenCoordinator _coordinator;
    private readonly ImageBrowseSession _browseSession;
    private SupportedLanguage _selectedLanguage;
    private double _scale = 1;

    public MainWindowViewModel(
        ILocalizationService localization,
        ImageOpenCoordinator coordinator,
        ImageBrowseSession browseSession)
    {
        _localization = localization;
        _coordinator = coordinator;
        _browseSession = browseSession;
        _selectedLanguage = FindCurrentLanguage();
        OpenCommand = new AsyncRelayCommand(() => _coordinator.PickAndOpenAsync());
        PreviousCommand = new AsyncRelayCommand(MovePreviousAsync, () => CanMovePrevious);
        NextCommand = new AsyncRelayCommand(MoveNextAsync, () => CanMoveNext);
        _localization.CultureChanged += OnCultureChanged;
        _coordinator.PropertyChanged += OnCoordinatorPropertyChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Title => string.IsNullOrEmpty(CurrentFileName)
        ? Text("MainWindow_Title")
        : $"{CurrentFileName} — {Text("MainWindow_Title")}";

    public string StatusText => _coordinator.State.Status switch
    {
        ImageOpenStatus.Loading => Text("Status_Loading"),
        ImageOpenStatus.Error => Text($"Error_{_coordinator.State.Error}"),
        _ => string.Empty,
    };

    public string CurrentFileName => _coordinator.State.FilePath is null
        ? string.Empty
        : Path.GetFileName(_coordinator.State.FilePath);

    public string DimensionsText => CurrentImage is null
        ? string.Empty
        : string.Format(_localization.CurrentCulture, Text("Status_DimensionsFormat"), CurrentImage.Size.Width, CurrentImage.Size.Height);

    public string ZoomText => string.Format(_localization.CurrentCulture, Text("Status_ZoomFormat"), _scale);

    public string PositionText => _browseSession.Count == 0
        ? string.Empty
        : string.Format(_localization.CurrentCulture, Text("Status_PositionFormat"), _browseSession.CurrentIndex + 1, _browseSession.Count);

    public string LanguageLabel => Text("Language_Label");
    public string OpenLabel => Text("Command_Open");
    public string PreviousLabel => Text("Command_Previous");
    public string NextLabel => Text("Command_Next");
    public string FitLabel => Text("Command_Fit");
    public string ActualSizeLabel => Text("Command_ActualSize");
    public string ZoomInLabel => Text("Command_ZoomIn");
    public string ZoomOutLabel => Text("Command_ZoomOut");
    public string EmptyTitle => Text("Empty_Title");
    public string EmptyHint => Text("Empty_Hint");

    public PixelBuffer? CurrentImage => _coordinator.State.Image;
    public bool HasImage => CurrentImage is not null;
    public bool IsLoading => _coordinator.State.Status == ImageOpenStatus.Loading;
    public bool CanMovePrevious => _browseSession.CanMovePrevious;
    public bool CanMoveNext => _browseSession.CanMoveNext;

    public ICommand OpenCommand { get; }
    public AsyncRelayCommand PreviousCommand { get; }
    public AsyncRelayCommand NextCommand { get; }

    public IReadOnlyList<SupportedLanguage> SupportedLanguages => _localization.SupportedLanguages;

    public SupportedLanguage SelectedLanguage
    {
        get => _selectedLanguage;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (_selectedLanguage != value)
            {
                _localization.SetCulture(value.CultureName);
            }
        }
    }

    public async Task<bool> OpenPathAsync(string path)
    {
        bool opened = await _coordinator.OpenAsync(path);
        return opened;
    }

    public void UpdateScale(double scale)
    {
        _scale = scale;
        OnPropertyChanged(nameof(ZoomText));
    }

    private async Task MovePreviousAsync()
    {
        string? path = _browseSession.GetPreviousPath();
        if (path is not null)
        {
            await OpenPathAsync(path);
        }
    }

    private async Task MoveNextAsync()
    {
        string? path = _browseSession.GetNextPath();
        if (path is not null)
        {
            await OpenPathAsync(path);
        }
    }

    private void OnCultureChanged(object? sender, EventArgs e)
    {
        _selectedLanguage = FindCurrentLanguage();
        NotifyAll();
    }

    private void OnCoordinatorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_coordinator.State is { Status: ImageOpenStatus.Loaded, FilePath: not null } state)
        {
            _browseSession.Commit(state.FilePath);
        }

        NotifyAll();
    }

    private void NotifyAll()
    {
        OnPropertyChanged(string.Empty);
        PreviousCommand.RaiseCanExecuteChanged();
        NextCommand.RaiseCanExecuteChanged();
    }

    private string Text(string key) => _localization.GetString(key);

    private SupportedLanguage FindCurrentLanguage() => _localization.SupportedLanguages.Single(
        language => language.CultureName == _localization.CurrentCulture.Name);

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
