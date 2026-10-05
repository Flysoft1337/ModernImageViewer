using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Shell;
using System.Windows.Threading;

using Microsoft.Win32;

using ModernImageViewer.Application.Browsing;
using ModernImageViewer.Application.Images;
using ModernImageViewer.Application.Integration;
using ModernImageViewer.UI.Controls;
using ModernImageViewer.UI.Localization;
using ModernImageViewer.UI.Themes;
using ModernImageViewer.UI.ViewModels;

namespace ModernImageViewer.UI;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private readonly ThemeService _themes;
    private readonly Func<IFileAssociationService>? _fileAssociations;
    private readonly ILocalizationService? _localization;
    private readonly DispatcherTimer _messageTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer _slideshowTimer = new();
    private Rect _savedBounds;
    private WindowState _savedState;
    private double _savedMinWidth;
    private double _savedMinHeight;

    public MainWindow(MainWindowViewModel viewModel, ThemeService themes,
        Func<IFileAssociationService>? fileAssociations = null, ILocalizationService? localization = null,
        IThumbnailDecoder? thumbnailDecoder = null)
    {
        _viewModel = viewModel;
        _themes = themes;
        _fileAssociations = fileAssociations;
        _localization = localization;
        ThumbnailDecoder = thumbnailDecoder;
        _themes.Initialize();
        InitializeComponent();
        DataContext = viewModel;
        Viewport.ScaleChanged += (_, scale) => _viewModel.UpdateScale(scale);
        Viewport.DetailRequested += OnDetailRequested;
        InitializeRegionDetail();
        InitializeImmersiveControls();
        InitializeWindowPreferences();
        Viewport.OrientationChanged += OnOrientationChanged;
        _messageTimer.Tick += (_, _) => { _messageTimer.Stop(); _viewModel.ShowMessage(null); };
        StateChanged += (_, _) =>
        {
            WindowLayout.Margin = !_viewModel.IsFullScreen && WindowState == WindowState.Maximized ? new Thickness(6) : new Thickness(0);
            UpdateSlideshowTimer();
        };
        _slideshowTimer.Tick += OnSlideshowTick;
        _viewModel.PropertyChanged += OnViewModelChanged;
        Closed += (_, _) =>
        {
            SaveWindowPreferences();
            _viewModel.FlushPreferences();
            Viewport.OrientationChanged -= OnOrientationChanged;
            _viewModel.IsSlideshowPlaying = false;
            _viewModel.PropertyChanged -= OnViewModelChanged;
            _messageTimer.Stop();
            _slideshowTimer.Stop();
            DisposeImmersiveControls();
            DisposeRegionDetail();
            Viewport.DetailRequested -= OnDetailRequested;
            Viewport.Dispose();
        };
    }

    public IThumbnailDecoder? ThumbnailDecoder { get; }

    private void OnDetailRequested(object? sender, EventArgs e)
    {
        // Queue after the pixel/path binding has completed; do not reenter a WPF binding update.
        ModernImageViewer.Imaging.PixelBuffer? requestedImage = _viewModel.CurrentImage;
        _ = Dispatcher.InvokeAsync(async () =>
        {
            if (IsLoaded && ReferenceEquals(requestedImage, _viewModel.CurrentImage))
            {
                await _viewModel.RefineImageAsync();
            }
        }, DispatcherPriority.Background);
    }

    private async void OnRefineClick(object sender, RoutedEventArgs e) => await _viewModel.RefineImageAsync();
    private void OnCancelRefinementClick(object sender, RoutedEventArgs e) => _viewModel.CancelRefinement();

    private void OnFitClick(object sender, RoutedEventArgs e) => Viewport.Fit();
    private void OnActualSizeClick(object sender, RoutedEventArgs e) => Viewport.ActualSize();
    private void OnZoomInClick(object sender, RoutedEventArgs e) => Viewport.ZoomIn();
    private void OnZoomOutClick(object sender, RoutedEventArgs e) => Viewport.ZoomOut();
    private void OnInformationClick(object sender, RoutedEventArgs e) => _viewModel.ShowInformation = !_viewModel.ShowInformation;
    private void OnFilmstripClick(object sender, RoutedEventArgs e) => _viewModel.ShowFilmstrip = !_viewModel.ShowFilmstrip;
    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnMaximizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnFileAssociationsClick(object sender, RoutedEventArgs e)
    {
        if (_fileAssociations is null || _localization is null)
        {
            return;
        }
        try
        {
            FileAssociationWindow dialog = new(_fileAssociations(), _localization) { Owner = this };
            dialog.ShowDialog();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or System.Security.SecurityException or InvalidOperationException or Win32Exception)
        {
            _viewModel.ShowMessage("Association_Failed");
            _messageTimer.Start();
        }
    }

    public void ActivateForExternalOpen()
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
        if (!Activate())
        {
            FlashInfo flash = new()
            {
                Size = (uint)Marshal.SizeOf<FlashInfo>(),
                Window = new WindowInteropHelper(this).Handle,
                Flags = 2,
                Count = 3,
            };
            FlashWindowEx(ref flash);
        }
    }

    private async void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        _viewModel.IsSlideshowPlaying = false;
        OpenFolderDialog dialog = new() { Title = _viewModel.OpenFolderLabel, Multiselect = false };
        if (dialog.ShowDialog(this) == true)
        {
            await _viewModel.OpenFolderAsync(dialog.FolderName);
        }
    }

    private void OnSlideshowClick(object sender, RoutedEventArgs e) => _viewModel.IsSlideshowPlaying = !_viewModel.IsSlideshowPlaying;

    private void OnSlideshowSpeedClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string value } && int.TryParse(value, out int seconds))
        {
            _viewModel.SlideshowSeconds = seconds;
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        UpdateSlideshowTimer();
        if (e.PropertyName == nameof(MainWindowViewModel.IsFullScreen))
        {
            UpdateImmersiveMode();
        }
    }

    private void UpdateSlideshowTimer()
    {
        _slideshowTimer.Stop();
        if (_viewModel.IsSlideshowPlaying && _viewModel.CanPlaySlideshow && !_viewModel.IsLoading && IsLoaded && WindowState != WindowState.Minimized)
        {
            _slideshowTimer.Interval = TimeSpan.FromSeconds(_viewModel.SlideshowSeconds);
            _slideshowTimer.Start();
        }
    }

    private async void OnSlideshowTick(object? sender, EventArgs e)
    {
        _slideshowTimer.Stop();
        if (_viewModel.IsSlideshowPlaying && !_viewModel.IsLoading)
        {
            await _viewModel.AdvanceSlideshowAsync();
        }
        UpdateSlideshowTimer();
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && SettingsButton.ContextMenu is { } menu)
        {
            RevealImmersiveControls();
            menu.PlacementTarget = button;
            menu.Placement = ReferenceEquals(button, SettingsButton) ? PlacementMode.Bottom : PlacementMode.Top;
            menu.IsOpen = true;
        }
    }

    private void OnSettingsOpened(object sender, RoutedEventArgs e)
    {
        Slideshow2Item.IsChecked = _viewModel.SlideshowSeconds == 2;
        Slideshow5Item.IsChecked = _viewModel.SlideshowSeconds == 5;
        Slideshow10Item.IsChecked = _viewModel.SlideshowSeconds == 10;
        DarkThemeItem.IsChecked = _themes.CurrentTheme == AppTheme.Dark;
        LightThemeItem.IsChecked = _themes.CurrentTheme == AppTheme.Light;
        SystemThemeItem.IsChecked = _themes.CurrentTheme == AppTheme.System;
    }

    private void OnThemeClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string name } && Enum.TryParse(name, out AppTheme theme))
        {
            _themes.Apply(theme);
        }
    }

    private void OnLanguageClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string name })
        {
            _viewModel.SelectedLanguage = _viewModel.SupportedLanguages.Single(language => language.CultureName == name);
        }
    }

    private async void OnThumbnailClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: BrowseItem item })
        {
            _viewModel.IsSlideshowPlaying = false;
            await _viewModel.OpenPathAsync(item.FilePath);
        }
    }

    private void OnThumbnailLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: BrowseItem { IsCurrent: true } } button)
        {
            // Let the complete strip measure before bringing its selected tile into view.
            Dispatcher.InvokeAsync(() => button.BringIntoView(), DispatcherPriority.Loaded);
        }
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        ThumbnailImage.ClearCache();
        await _viewModel.RefreshFolderAsync();
    }

    private void OnCopyPathClick(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.HasImage)
        {
            return;
        }

        try
        {
            Clipboard.SetText(_viewModel.CurrentFilePath);
            _viewModel.ShowMessage("Status_PathCopied");
        }
        catch (ExternalException)
        {
            _viewModel.ShowMessage("Error_ClipboardBusy");
        }

        _messageTimer.Stop();
        _messageTimer.Start();
    }

    private void OnFullScreenClick(object sender, RoutedEventArgs e) => ToggleFullScreen();

    private void ToggleFullScreen()
    {
        _changingFullScreen = true;
        try { ChangeFullScreen(); }
        finally { _changingFullScreen = false; }
    }

    private void ChangeFullScreen()
    {
        WindowChrome chrome = WindowChrome.GetWindowChrome(this);
        if (_viewModel.IsFullScreen)
        {
            _viewModel.IsFullScreen = false;
            MinWidth = _savedMinWidth;
            MinHeight = _savedMinHeight;
            ResizeMode = ResizeMode.CanResize;
            chrome.CaptionHeight = 54;
            chrome.ResizeBorderThickness = new Thickness(6);
            Left = _savedBounds.Left;
            Top = _savedBounds.Top;
            Width = _savedBounds.Width;
            Height = _savedBounds.Height;
            WindowState = _savedState;
            return;
        }

        IntPtr monitor = MonitorFromWindow(new WindowInteropHelper(this).Handle, 2);
        MonitorInfo info = new() { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            return;
        }

        _savedState = WindowState;
        _savedMinWidth = MinWidth;
        _savedMinHeight = MinHeight;
        _savedBounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        var source = PresentationSource.FromVisual(this);
        var transform = source?.CompositionTarget?.TransformFromDevice ?? System.Windows.Media.Matrix.Identity;
        Point origin = transform.Transform(new Point(info.Monitor.Left, info.Monitor.Top));
        Point corner = transform.Transform(new Point(info.Monitor.Right, info.Monitor.Bottom));
        WindowState = WindowState.Normal;
        _viewModel.IsFullScreen = true;
        ResizeMode = ResizeMode.NoResize;
        MinWidth = 0;
        MinHeight = 0;
        chrome.CaptionHeight = 0;
        chrome.ResizeBorderThickness = new Thickness(0);
        Left = origin.X;
        Top = origin.Y;
        Width = corner.X - origin.X;
        Height = corner.Y - origin.Y;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = GetSupportedDroppedPath(e.Data) is null ? DragDropEffects.None : DragDropEffects.Copy;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths && GetSupportedDroppedPath(e.Data) is not null)
        {
            await _viewModel.OpenInputsAsync(paths);
        }
        e.Handled = true;
    }

    private static string? GetSupportedDroppedPath(IDataObject data)
    {
        if (!data.GetDataPresent(DataFormats.FileDrop) || data.GetData(DataFormats.FileDrop) is not string[] paths)
        {
            return null;
        }

        return paths.FirstOrDefault(path => Directory.Exists(path) || ImageBrowseSession.IsSupported(path));
    }

    private async void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        RevealImmersiveControls();
        ViewerAction? action = ShortcutCatalog.Match(e.Key, Keyboard.Modifiers);
        if (action == ViewerAction.ToggleFullScreen)
        {
            ToggleFullScreen();
            e.Handled = true;
            return;
        }
        if (action == ViewerAction.ShowShortcutHelp)
        {
            e.Handled = true;
            ShowShortcutHelp();
            return;
        }
        if (action == ViewerAction.OpenImage)
        {
            e.Handled = true;
            await ((Commands.AsyncRelayCommand)_viewModel.OpenCommand).ExecuteAsync();
            return;
        }
        if (e.Key == Key.Escape && (_viewModel.IsFullScreen || _viewModel.IsSlideshowPlaying))
        {
            _viewModel.IsSlideshowPlaying = false;
            if (_viewModel.IsFullScreen)
            {
                ToggleFullScreen();
            }
            e.Handled = true;
            return;
        }
        if (Keyboard.FocusedElement is TextBoxBase or ComboBox or MenuItem || action is null
            || (e.Key == Key.Space && Keyboard.FocusedElement is ButtonBase))
        {
            return;
        }
        e.Handled = true;
        switch (action)
        {
            case ViewerAction.OpenFolder: OnOpenFolderClick(this, e); break;
            case ViewerAction.PreviousImage: await _viewModel.PreviousCommand.ExecuteAsync(); break;
            case ViewerAction.NextImage: await _viewModel.NextCommand.ExecuteAsync(); break;
            case ViewerAction.FirstImage: await _viewModel.OpenFirstAsync(); break;
            case ViewerAction.LastImage: await _viewModel.OpenLastAsync(); break;
            case ViewerAction.FitImage: Viewport.Fit(); break;
            case ViewerAction.ActualSize: Viewport.ActualSize(); break;
            case ViewerAction.ZoomIn: Viewport.ZoomIn(); break;
            case ViewerAction.ZoomOut: Viewport.ZoomOut(); break;
            case ViewerAction.ToggleSlideshow: OnSlideshowClick(this, e); break;
            case ViewerAction.ToggleInformation: OnInformationClick(this, e); break;
            case ViewerAction.ToggleFilmstrip: OnFilmstripClick(this, e); break;
            case ViewerAction.RefreshFolder: OnRefreshClick(this, e); break;
            case ViewerAction.RevealInExplorer: OnRevealClick(this, e); break;
            case ViewerAction.RotateRight: Viewport.RotateRight(); break;
            case ViewerAction.RotateLeft: Viewport.RotateLeft(); break;
            case ViewerAction.DismissOverlay: _viewModel.ShowInformation = false; break;
        }
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FlashInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo
    {
        public uint Size;
        public IntPtr Window;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", ExactSpelling = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
