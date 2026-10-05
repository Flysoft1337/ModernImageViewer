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
    private static readonly SemaphoreSlim s_fileInformationGate = new(1, 1);
    private readonly ILocalizationService _localization;
    private readonly ImageOpenCoordinator _coordinator;
    private readonly ImageBrowseSession _browseSession;
    private CancellationTokenSource? _refinementCancellation;
    private CancellationTokenSource? _folderCancellation;
    private CancellationTokenSource? _refreshCancellation;
    private CancellationTokenSource? _fileInformationCancellation;
    private long _fileInformationVersion;
    private long _inputVersion;
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
        OpenCommand = new AsyncRelayCommand(PickInputAsync);
        PreviousCommand = new AsyncRelayCommand(MovePreviousAsync, () => CanMovePrevious);
        NextCommand = new AsyncRelayCommand(MoveNextAsync, () => CanMoveNext);
        _localization.CultureChanged += OnCultureChanged;
        _coordinator.PropertyChanged += OnCoordinatorPropertyChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Title => string.IsNullOrEmpty(CurrentFileName)
        ? Text("MainWindow_Title")
        : $"{CurrentFileName} — {Text("MainWindow_Title")}";

    public string StatusText => _isIndexingFolder || _browseSession.IsIndexing ? Text("Status_ScanningFolder")
        : _messageKey is not null ? Text(_messageKey) : _coordinator.State.Status switch
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
        : string.Format(_localization.CurrentCulture, Text("Status_DimensionsFormat"), CurrentImage.SourceSize.Width, CurrentImage.SourceSize.Height);

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
    public bool CanPlaySlideshow => HasImage && !_browseSession.IsIndexing && _browseSession.Count > 1;
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
    public string FileAssociationLabel => Text("Association_Title");
    public string SettingsLabel => Text("Settings_Title");
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
    public string DirectoryName => _browseSession.IsSelection ? Text("Browsing_Selection")
        : Path.GetFileName(DirectoryPath) is { Length: > 0 } name ? name : DirectoryPath;
    public string FormatText => HasImage ? Path.GetExtension(CurrentFilePath).TrimStart('.').ToUpperInvariant() : "—";
    public string FileSizeText => _fileLength is { } length ? FormatFileSize(length) : "—";
    public string ModifiedText => _modified?.ToString("g", _localization.CurrentCulture) ?? "—";
    public string FolderImagesText => string.Format(_localization.CurrentCulture, Text(_browseSession.IsSelection ? "Browsing_SelectionCountFormat" : "Browsing_CountFormat"), _browseSession.Count);
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

    public ImageOpenState Presentation => _coordinator.State;
    public bool ShowPreviewStatus => HasImage && Presentation.IsPreview && Presentation.Status != ImageOpenStatus.Loading;
    public bool IsRefining => Presentation.IsRefining;
    public bool CanRefine => ShowPreviewStatus && !IsRefining;
    public string RefineLabel => Text("Preview_Refine");
    public string CancelLabel => Text("Preview_Cancel");
    public string PreviewStatusText => Text(IsRefining ? "Preview_Refining"
        : Presentation.RefinementError == ImageOpenError.ImageTooLarge ? "Preview_BudgetLimit"
        : Presentation.RefinementError != ImageOpenError.None ? "Preview_DetailFailed" : "Preview_Ready");

    public async Task RefineImageAsync()
    {
        if (_disposed || !CanRefine)
        {
            return;
        }
        using CancellationTokenSource cancellation = new();
        _refinementCancellation = cancellation;
        try
        {
            await _coordinator.RefineAsync(cancellation.Token);
        }
        finally
        {
            if (ReferenceEquals(_refinementCancellation, cancellation))
            {
                _refinementCancellation = null;
            }
        }
    }

    public void CancelRefinement() => _refinementCancellation?.Cancel();

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
        return await _coordinator.OpenCandidatesAsync([path], _browseSession);
    }

    public void UpdateScale(double scale)
    {
        _scale = scale;
        OnPropertyChanged(nameof(ZoomText));
    }

    public Task<bool> OpenFirstAsync() => _browseSession.IsIndexing || _browseSession.Count == 0
        ? Task.FromResult(false)
        : OpenPathAsync(_browseSession.Items[0]);

    public Task<bool> OpenLastAsync() => _browseSession.IsIndexing || _browseSession.Count == 0
        ? Task.FromResult(false)
        : OpenPathAsync(_browseSession.Items[^1]);

    public Task<bool> OpenInputAsync(string path) => OpenInputsAsync([path]);

    public async Task<bool> OpenInputsAsync(IReadOnlyList<string> paths)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count == 0)
        {
            return false;
        }
        IsSlideshowPlaying = false;
        CancelFolderWork();
        _refreshCancellation?.Cancel();
        _coordinator.CancelPendingOpen();
        long version = _inputVersion;
        CancellationTokenSource cancellation = new();
        _folderCancellation = cancellation;
        CancellationToken token = cancellation.Token;
        try
        {
            OpenRequest request;
            try
            {
                request = OpenRequest.Create(paths);
            }
            catch (ArgumentException)
            {
                ShowMessage("Input_TooManyFiles");
                return false;
            }
            if (paths.Count == 1 && request.Paths.Count == 1 && await Task.Run(() => Directory.Exists(request.Paths[0]), token))
            {
                return await OpenFolderCoreAsync(request.Paths[0], version, token);
            }
            // Mixed requests use only explicit image files; directories are never expanded recursively.
            string[] candidates = await Task.Run(() => request.Paths.Where(path =>
            {
                token.ThrowIfCancellationRequested();
                return ImageBrowseSession.IsSupported(path) && File.Exists(path);
            }).ToArray(), token);
            if (version != _inputVersion || token.IsCancellationRequested)
            {
                return false;
            }
            if (candidates.Length == 0)
            {
                ShowMessage("Input_NoSupportedFiles");
                return false;
            }
            bool opened = await _coordinator.OpenCandidatesAsync(candidates, _browseSession,
                selection: paths.Count > 1, resetSelection: true, cancellationToken: token);
            if (opened && version == _inputVersion
                && (request.RejectedCount > 0 || candidates.Length < request.Paths.Count || _coordinator.SkippedCandidateCount > 0))
            {
                ShowMessage("Input_PartiallySkipped");
            }
            return opened;
        }
        catch (OperationCanceledException) { return false; }
        finally
        {
            if (ReferenceEquals(_folderCancellation, cancellation))
            {
                _folderCancellation = null;
            }
            cancellation.Dispose();
        }
    }

    public Task<bool> OpenFolderAsync(string directory) => OpenInputAsync(directory);

    private async Task<bool> OpenFolderCoreAsync(string directory, long version, CancellationToken token)
    {
        _isIndexingFolder = true;
        NotifyAll();
        try
        {
            string? first = await _browseSession.FindFirstAsync(directory, token);
            if (version != _inputVersion || token.IsCancellationRequested)
            {
                return false;
            }
            if (first is null)
            {
                ShowMessage("Status_EmptyFolder");
                return false;
            }
            _isIndexingFolder = false;
            return await _coordinator.OpenCandidatesAsync([first], _browseSession,
                resetSelection: true, cancellationToken: token);
        }
        finally
        {
            if (version == _inputVersion)
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
        _coordinator.CancelPendingIndexing();
        try
        {
            Task refresh = _browseSession.RefreshAsync(_refreshCancellation.Token);
            NotifyAll();
            await refresh;
            await ReadFileInformationAsync();
            UpdateBrowseItems();
            _isSlideshowPlaying = _isSlideshowPlaying && CanPlaySlideshow;
            NotifyAll();
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (!_disposed)
            {
                NotifyAll();
            }
        }
    }

    public Task<bool> AdvanceSlideshowAsync() => !_isSlideshowPlaying || !CanPlaySlideshow || IsLoading
        ? Task.FromResult(false)
        : OpenPathAsync(_browseSession.GetNextPath() ?? _browseSession.Items[0]);

    public void Dispose()
    {
        _disposed = true;
        _refinementCancellation?.Cancel();
        CancelFolderWork();
        _refreshCancellation?.Cancel();
        _refreshCancellation?.Dispose();
        _refreshCancellation = null;
        _fileInformationCancellation?.Cancel();
        _fileInformationCancellation?.Dispose();
        _fileInformationCancellation = null;
        _localization.CultureChanged -= OnCultureChanged;
        _coordinator.PropertyChanged -= OnCoordinatorPropertyChanged;
        GC.SuppressFinalize(this);
    }

    private void CancelFolderWork()
    {
        _inputVersion++;
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

    private async Task PickInputAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        IsSlideshowPlaying = false;
        CancelFolderWork();
        _refreshCancellation?.Cancel();
        _coordinator.CancelPendingOpen();
        using CancellationTokenSource cancellation = new();
        _folderCancellation = cancellation;
        try
        {
            await _coordinator.PickAndOpenAsync(_browseSession, cancellation.Token);
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(_folderCancellation, cancellation))
            {
                _folderCancellation = null;
            }
        }
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
            UpdateBrowseItems();
            if (e.PropertyName == nameof(ImageOpenCoordinator.State))
            {
                _ = ReadFileInformationAsync();
                UpdateMetadataItems();
            }
        }
        if (_coordinator.State.Status == ImageOpenStatus.Error)
        {
            _isSlideshowPlaying = false;
        }

        if (e.PropertyName == nameof(ImageOpenCoordinator.State))
        {
            _messageKey = null;
        }

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

    private async Task ReadFileInformationAsync()
    {
        _fileInformationCancellation?.Cancel();
        _fileInformationCancellation?.Dispose();
        CancellationTokenSource cancellation = new();
        _fileInformationCancellation = cancellation;
        CancellationToken token = cancellation.Token;
        long version = ++_fileInformationVersion;
        string path = CurrentFilePath;
        _fileLength = null;
        _modified = null;
        try
        {
            await s_fileInformationGate.WaitAsync(token);
            (long? length, DateTime? modified) information;
            try
            {
                information = await Task.Run(() =>
                {
                    FileInfo file = new(path);
                    return file.Exists ? ((long?)file.Length, (DateTime?)file.LastWriteTime) : (null, null);
                }, token);
            }
            finally
            {
                s_fileInformationGate.Release();
            }
            if (!_disposed && version == _fileInformationVersion && !token.IsCancellationRequested)
            {
                (_fileLength, _modified) = information;
                OnPropertyChanged(nameof(FileSizeText));
                OnPropertyChanged(nameof(ModifiedText));
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        finally
        {
            if (ReferenceEquals(_fileInformationCancellation, cancellation))
            {
                _fileInformationCancellation = null;
            }
            cancellation.Dispose();
        }
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
