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

public sealed class MainWindowViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly ILocalizationService _localization;
    private readonly ImageOpenCoordinator _coordinator;
    private readonly ImageBrowseSession _browseSession;
    private CancellationTokenSource? _folderCancellation;
    private CancellationTokenSource? _refreshCancellation;
    private bool _disposed;
    private bool _isIndexingFolder;
    private bool _isSlideshowPlaying;
    private int _slideshowSeconds = 5;
    private List<MetadataItem> _metadataItems = [];
    private SupportedLanguage _selectedLanguage;
    private double _scale = 1;
    private bool _showInformation;
    private bool _isFullScreen;
    private bool _showFilmstrip = true;
    private long? _fileLength;
    private DateTime? _modified;
    private string? _messageKey;
    private IReadOnlyList<BrowseItem> _browseItems = [];

    public MainWindowViewModel(
        ILocalizationService localization,
        ImageOpenCoordinator coordinator,
        ImageBrowseSession browseSession)
    {
        _localization = localization;
        _coordinator = coordinator;
        _browseSession = browseSession;
        _selectedLanguage = FindCurrentLanguage();
        OpenCommand = new AsyncRelayCommand(async () =>
        {
            IsSlideshowPlaying = false;
            CancelFolderWork();
            await _coordinator.PickAndOpenAsync();
        });
        PreviousCommand = new AsyncRelayCommand(MovePreviousAsync, () => CanMovePrevious);
        NextCommand = new AsyncRelayCommand(MoveNextAsync, () => CanMoveNext);
        _localization.CultureChanged += OnCultureChanged;
        _coordinator.PropertyChanged += OnCoordinatorPropertyChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Title => string.IsNullOrEmpty(CurrentFileName)
        ? Text("MainWindow_Title")
        : $"{CurrentFileName} — {Text("MainWindow_Title")}";

    public string StatusText => _isIndexingFolder ? Text("Status_ScanningFolder") : _coordinator.State.Status switch
    {
        ImageOpenStatus.Loading => Text("Status_Loading"),
        ImageOpenStatus.Error => Text($"Error_{_coordinator.State.Error}"),
        _ => _messageKey is null ? string.Empty : Text(_messageKey),
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
    public string MinimizeLabel => Text("Window_Minimize");
    public string MaximizeLabel => Text("Window_Maximize");
    public string CloseLabel => Text("Window_Close");
    public bool HasStatusMessage => !IsLoading && StatusText.Length > 0;
    public bool IsFullScreen
    {
        get => _isFullScreen;
        set { _isFullScreen = value; OnPropertyChanged(); }
    }
    public string AppName => Text("MainWindow_Title");
    public string WelcomeTitle => Text("Welcome_Title");
    public string WelcomeHint => Text("Welcome_Hint");
    public string FullScreenLabel => Text("Command_FullScreen");
    public string InformationLabel => Text("Command_Information");
    public string FilmstripLabel => Text("Command_Filmstrip");
    public string OpenFolderLabel => Text("Command_OpenFolder");
    public string SlideshowLabel => Text(IsSlideshowPlaying ? "Command_PauseSlideshow" : "Command_Slideshow");
    public string ExifLabel => Text("Information_Exif");
    public string Slideshow2Label => Text("Slideshow_2Seconds");
    public string Slideshow5Label => Text("Slideshow_5Seconds");
    public string Slideshow10Label => Text("Slideshow_10Seconds");
    public string SlideshowSpeedLabel => Text("Slideshow_Speed");
    public bool CanPlaySlideshow => HasImage && _browseSession.Count > 1;
    public IReadOnlyList<MetadataItem> MetadataItems => _metadataItems;
    public bool HasMetadata => _metadataItems.Count > 0;
    public bool IsSlideshowPlaying
    {
        get => _isSlideshowPlaying;
        set
        {
            _isSlideshowPlaying = value && CanPlaySlideshow;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SlideshowLabel));
        }
    }
    public int SlideshowSeconds
    {
        get => _slideshowSeconds;
        set { _slideshowSeconds = Math.Clamp(value, 2, 30); OnPropertyChanged(); }
    }
    public string CopyPathLabel => Text("Command_CopyPath");
    public string RefreshLabel => Text("Command_Refresh");
    public string AppearanceLabel => Text("Appearance_Label");
    public string DarkThemeLabel => Text("Theme_Dark");
    public string LightThemeLabel => Text("Theme_Light");
    public string SystemThemeLabel => Text("Theme_System");
    public string FileNameLabel => Text("Information_FileName");
    public string FolderLabel => Text("Information_Folder");
    public string FormatLabel => Text("Information_Format");
    public string DimensionsLabel => Text("Information_Dimensions");
    public string FileSizeLabel => Text("Information_FileSize");
    public string ModifiedLabel => Text("Information_Modified");
    public string KeyboardHint => Text("Navigation_Hint");
    public string CurrentFilePath => _coordinator.State.FilePath ?? string.Empty;
    public string DirectoryPath => Path.GetDirectoryName(CurrentFilePath) ?? string.Empty;
    public string DirectoryName => Path.GetFileName(DirectoryPath) is { Length: > 0 } name ? name : DirectoryPath;
    public string FormatText => HasImage ? Path.GetExtension(CurrentFilePath).TrimStart('.').ToUpperInvariant() : "—";
    public string FileSizeText => _fileLength is { } length ? FormatFileSize(length) : "—";
    public string ModifiedText => _modified?.ToString("g", _localization.CurrentCulture) ?? "—";
    public string FolderImagesText => string.Format(_localization.CurrentCulture, Text("Browsing_CountFormat"), _browseSession.Count);
    public IReadOnlyList<BrowseItem> BrowseItems => _browseItems;
    public bool IsFilmstripVisible => HasImage && _showFilmstrip && _browseSession.Count > 1;

    public bool ShowInformation
    {
        get => _showInformation;
        set
        {
            _showInformation = value;
            OnPropertyChanged();
        }
    }

    public bool ShowFilmstrip
    {
        get => _showFilmstrip;
        set
        {
            _showFilmstrip = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsFilmstripVisible));
        }
    }

    public PixelBuffer? CurrentImage => _coordinator.State.Image;
    public bool HasImage => CurrentImage is not null;
    public bool IsLoading => _isIndexingFolder || _coordinator.State.Status == ImageOpenStatus.Loading;
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
        ObjectDisposedException.ThrowIf(_disposed, this);
        CancelFolderWork();
        _refreshCancellation?.Cancel();
        return await _coordinator.OpenAsync(path);
    }

    public void UpdateScale(double scale)
    {
        _scale = scale;
        OnPropertyChanged(nameof(ZoomText));
    }

    public Task<bool> OpenFirstAsync() => _browseSession.Count == 0
        ? Task.FromResult(false)
        : OpenPathAsync(_browseSession.Items[0]);

    public Task<bool> OpenLastAsync() => _browseSession.Count == 0
        ? Task.FromResult(false)
        : OpenPathAsync(_browseSession.Items[^1]);

    public async Task<bool> OpenInputAsync(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        IsSlideshowPlaying = false;
        return Directory.Exists(path) ? await OpenFolderAsync(path) : await OpenPathAsync(path);
    }

    public async Task<bool> OpenFolderAsync(string directory)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        IsSlideshowPlaying = false;
        CancelFolderWork();
        _refreshCancellation?.Cancel();
        CancellationTokenSource cancellation = new();
        _folderCancellation = cancellation;
        CancellationToken token = cancellation.Token;
        _isIndexingFolder = true;
        NotifyAll();
        try
        {
            string? first = await _browseSession.FindFirstAsync(directory, token);
            if (first is null)
            {
                ShowMessage("Status_EmptyFolder");
                return false;
            }
            _isIndexingFolder = false;
            return await _coordinator.OpenAsync(first, token);
        }
        catch (OperationCanceledException) { return false; }
        finally
        {
            if (ReferenceEquals(_folderCancellation, cancellation))
            {
                _isIndexingFolder = false;
                NotifyAll();
            }
        }
    }

    public async Task RefreshFolderAsync()
    {
        if (!HasImage || _disposed)
        {
            return;
        }
        _refreshCancellation?.Cancel();
        _refreshCancellation?.Dispose();
        _refreshCancellation = new CancellationTokenSource();
        try
        {
            await _browseSession.RefreshAsync(_refreshCancellation.Token);
            ReadFileInformation();
            UpdateBrowseItems();
            _isSlideshowPlaying = _isSlideshowPlaying && CanPlaySlideshow;
            NotifyAll();
        }
        catch (OperationCanceledException) { }
    }

    public Task<bool> AdvanceSlideshowAsync() => !_isSlideshowPlaying || !CanPlaySlideshow || IsLoading
        ? Task.FromResult(false)
        : OpenPathAsync(_browseSession.GetNextPath() ?? _browseSession.Items[0]);

    public void Dispose()
    {
        _disposed = true;
        CancelFolderWork();
        _refreshCancellation?.Cancel();
        _refreshCancellation?.Dispose();
        _refreshCancellation = null;
        _localization.CultureChanged -= OnCultureChanged;
        _coordinator.PropertyChanged -= OnCoordinatorPropertyChanged;
        GC.SuppressFinalize(this);
    }

    private void CancelFolderWork()
    {
        _folderCancellation?.Cancel();
        _folderCancellation?.Dispose();
        _folderCancellation = null;
        if (_isIndexingFolder)
        {
            _isIndexingFolder = false;
            if (!_disposed)
            {
                NotifyAll();
            }
        }
    }

    public void ShowMessage(string? resourceKey)
    {
        _messageKey = resourceKey;
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(HasStatusMessage));
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
        UpdateMetadataItems();
        NotifyAll();
    }

    private void OnCoordinatorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_coordinator.State is { Status: ImageOpenStatus.Loaded, FilePath: not null } state)
        {
            _browseSession.Commit(state.FilePath);
            UpdateBrowseItems();
            ReadFileInformation();
            UpdateMetadataItems();
        }
        if (_coordinator.State.Status == ImageOpenStatus.Error)
        {
            _isSlideshowPlaying = false;
        }

        _messageKey = null;

        NotifyAll();
    }

    private void UpdateBrowseItems()
    {
        // Keep the strip bounded, including at the beginning and end of a directory.
        const int visibleCount = 9;
        int start = Math.Clamp(_browseSession.CurrentIndex - (visibleCount / 2), 0, Math.Max(0, _browseSession.Count - visibleCount));
        _browseItems = _browseSession.Items.Skip(start).Take(visibleCount)
            .Select((path, offset) => new BrowseItem(path, start + offset, start + offset == _browseSession.CurrentIndex))
            .ToArray();
    }

    private void ReadFileInformation()
    {
        _fileLength = null;
        _modified = null;
        try
        {
            FileInfo file = new(CurrentFilePath);
            if (file.Exists)
            {
                _fileLength = file.Length;
                _modified = file.LastWriteTime;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void UpdateMetadataItems()
    {
        ImageMetadata metadata = CurrentImage?.Metadata ?? ImageMetadata.Empty;
        List<MetadataItem> items = [];
        void Add(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                items.Add(new MetadataItem(Text(key), value));
            }
        }
        Add("Exif_Camera", metadata.Camera);
        Add("Exif_Lens", metadata.Lens);
        string? captured = metadata.CapturedAt;
        if (DateTime.TryParseExact(captured, "yyyy:MM:dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out DateTime date))
        {
            captured = date.ToString("g", _localization.CurrentCulture);
        }
        Add("Exif_CapturedAt", captured);
        Add("Exif_Iso", metadata.Iso?.ToString(_localization.CurrentCulture));
        if (metadata.ExposureSeconds is double exposure && exposure > 0)
        {
            Add("Exif_Exposure", exposure < 1 ? $"1/{Math.Round(1 / exposure):0} s" : $"{exposure:0.###} s");
        }
        Add("Exif_Aperture", metadata.Aperture is double aperture && aperture > 0 ? $"f/{aperture:0.#}" : null);
        Add("Exif_FocalLength", metadata.FocalLength is double focal && focal > 0 ? $"{focal:0.#} mm" : null);
        if (metadata.Orientation != 1)
        {
            Add("Exif_Orientation", Text("Exif_OrientationApplied"));
        }
        _metadataItems = items;
    }

    private string FormatFileSize(long length)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double size = length;
        int unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return string.Format(_localization.CurrentCulture, "{0:0.#} {1}", size, units[unit]);
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
