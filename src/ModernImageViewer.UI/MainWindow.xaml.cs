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
using ModernImageViewer.UI.Controls;
using ModernImageViewer.UI.Themes;
using ModernImageViewer.UI.ViewModels;

namespace ModernImageViewer.UI;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private readonly ThemeService _themes;
    private readonly DispatcherTimer _messageTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer _slideshowTimer = new();
    private Rect _savedBounds;
    private WindowState _savedState;
    private double _savedMinWidth;
    private double _savedMinHeight;

    public MainWindow(MainWindowViewModel viewModel, ThemeService themes)
    {
        _viewModel = viewModel;
        _themes = themes;
        _themes.Initialize();
        InitializeComponent();
        DataContext = viewModel;
        Viewport.ScaleChanged += (_, scale) => _viewModel.UpdateScale(scale);
        _messageTimer.Tick += (_, _) => { _messageTimer.Stop(); _viewModel.ShowMessage(null); };
        StateChanged += (_, _) =>
        {
            WindowLayout.Margin = WindowState == WindowState.Maximized ? new Thickness(6) : new Thickness(0);
            UpdateSlideshowTimer();
        };
        _slideshowTimer.Tick += OnSlideshowTick;
        _viewModel.PropertyChanged += OnViewModelChanged;
        Closed += (_, _) =>
        {
            _viewModel.IsSlideshowPlaying = false;
            _viewModel.PropertyChanged -= OnViewModelChanged;
            _messageTimer.Stop();
            _slideshowTimer.Stop();
            Viewport.Dispose();
        };
    }

    private void OnFitClick(object sender, RoutedEventArgs e) => Viewport.Fit();
    private void OnActualSizeClick(object sender, RoutedEventArgs e) => Viewport.ActualSize();
    private void OnZoomInClick(object sender, RoutedEventArgs e) => Viewport.ZoomIn();
    private void OnZoomOutClick(object sender, RoutedEventArgs e) => Viewport.ZoomOut();
    private void OnInformationClick(object sender, RoutedEventArgs e) => _viewModel.ShowInformation = !_viewModel.ShowInformation;
    private void OnFilmstripClick(object sender, RoutedEventArgs e) => _viewModel.ShowFilmstrip = !_viewModel.ShowFilmstrip;
    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnMaximizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

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

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e) => UpdateSlideshowTimer();

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
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
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
        string? path = GetSupportedDroppedPath(e.Data);
        if (path is not null)
        {
            await _viewModel.OpenInputAsync(path);
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
        if (e.Key == Key.F11 && Keyboard.Modifiers == ModifierKeys.None)
        {
            ToggleFullScreen();
            e.Handled = true;
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

        if (Keyboard.FocusedElement is TextBoxBase or ComboBox or MenuItem)
        {
            return;
        }

        if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.O)
        {
            OnOpenFolderClick(this, e);
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (e.Key == Key.I)
            {
                _viewModel.ShowInformation = !_viewModel.ShowInformation;
                e.Handled = true;
            }
            else if (e.Key == Key.T)
            {
                _viewModel.ShowFilmstrip = !_viewModel.ShowFilmstrip;
                e.Handled = true;
            }
            return;
        }

        if (Keyboard.Modifiers != ModifierKeys.None && !(Keyboard.Modifiers == ModifierKeys.Shift && e.Key == Key.OemPlus))
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Left: e.Handled = true; await _viewModel.PreviousCommand.ExecuteAsync(); break;
            case Key.Right: e.Handled = true; await _viewModel.NextCommand.ExecuteAsync(); break;
            case Key.Home: e.Handled = true; await _viewModel.OpenFirstAsync(); break;
            case Key.End: e.Handled = true; await _viewModel.OpenLastAsync(); break;
            case Key.D0:
            case Key.NumPad0: Viewport.Fit(); e.Handled = true; break;
            case Key.D1:
            case Key.NumPad1: Viewport.ActualSize(); e.Handled = true; break;
            case Key.Add:
            case Key.OemPlus: Viewport.ZoomIn(); e.Handled = true; break;
            case Key.Subtract:
            case Key.OemMinus: Viewport.ZoomOut(); e.Handled = true; break;
            case Key.F6: OnSlideshowClick(this, e); e.Handled = true; break;
            case Key.Space when Keyboard.FocusedElement is not ButtonBase:
                OnSlideshowClick(this, e); e.Handled = true; break;
            case Key.F5: OnRefreshClick(this, e); e.Handled = true; break;
            case Key.Escape: _viewModel.ShowInformation = false; e.Handled = true; break;
        }
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

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
