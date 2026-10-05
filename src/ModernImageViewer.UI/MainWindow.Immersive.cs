using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace ModernImageViewer.UI;

public partial class MainWindow
{
    private static readonly DependencyPropertyKey IsChromeVisiblePropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(IsChromeVisible), typeof(bool), typeof(MainWindow), new PropertyMetadata(true));

    public static readonly DependencyProperty IsChromeVisibleProperty = IsChromeVisiblePropertyKey.DependencyProperty;

    private static readonly DependencyPropertyKey IsCompactLayoutPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(IsCompactLayout), typeof(bool), typeof(MainWindow), new PropertyMetadata(false));

    public static readonly DependencyProperty IsCompactLayoutProperty = IsCompactLayoutPropertyKey.DependencyProperty;

    private readonly DispatcherTimer _immersiveTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };
    private Point? _lastPointerPosition;

    public bool IsChromeVisible => (bool)GetValue(IsChromeVisibleProperty);
    public bool IsCompactLayout => (bool)GetValue(IsCompactLayoutProperty);

    private void InitializeImmersiveControls()
    {
        _immersiveTimer.Tick += OnImmersiveIdle;
        SizeChanged += OnImmersiveSizeChanged;
        PreviewMouseMove += OnImmersiveMouseMove;
        PreviewMouseDown += OnImmersiveMouseActivity;
        PreviewMouseWheel += OnImmersiveMouseActivity;
        PreviewMouseUp += OnImmersivePointerReleased;
        Activated += OnImmersiveActivated;
        Deactivated += OnImmersiveDeactivated;
        Loaded += OnImmersiveLoaded;
        if (SettingsButton.ContextMenu is { } menu)
        {
            menu.Closed += OnImmersiveMenuClosed;
        }
    }

    private void DisposeImmersiveControls()
    {
        _immersiveTimer.Stop();
        _immersiveTimer.Tick -= OnImmersiveIdle;
        SizeChanged -= OnImmersiveSizeChanged;
        PreviewMouseMove -= OnImmersiveMouseMove;
        PreviewMouseDown -= OnImmersiveMouseActivity;
        PreviewMouseWheel -= OnImmersiveMouseActivity;
        PreviewMouseUp -= OnImmersivePointerReleased;
        Activated -= OnImmersiveActivated;
        Deactivated -= OnImmersiveDeactivated;
        Loaded -= OnImmersiveLoaded;
        if (SettingsButton.ContextMenu is { } menu)
        {
            menu.Closed -= OnImmersiveMenuClosed;
        }
        ClearValue(CursorProperty);
        ClearValue(ForceCursorProperty);
    }

    private void OnImmersiveLoaded(object sender, RoutedEventArgs e) => UpdateImmersiveMode();
    private void OnImmersiveActivated(object? sender, EventArgs e) => RevealImmersiveControls();
    private void OnImmersiveDeactivated(object? sender, EventArgs e)
    {
        _immersiveTimer.Stop();
        SetChromeVisible(true);
    }

    private void OnImmersiveSizeChanged(object sender, SizeChangedEventArgs e)
    {
        SetValue(IsCompactLayoutPropertyKey, ActualWidth < 960 || ActualHeight < 640);
    }

    private void UpdateImmersiveMode()
    {
        WindowLayout.Margin = !_viewModel.IsFullScreen && WindowState == WindowState.Maximized
            ? new Thickness(6) : new Thickness(0);
        _lastPointerPosition = null;
        RevealImmersiveControls();
    }

    private void OnImmersiveMouseMove(object sender, MouseEventArgs e)
    {
        Point point = e.GetPosition(this);
        if (_lastPointerPosition is not { } previous || (point - previous).LengthSquared > 1)
        {
            _lastPointerPosition = point;
            RevealImmersiveControls();
        }
    }

    private void OnImmersiveMouseActivity(object sender, MouseEventArgs e) => RevealImmersiveControls();

    private void OnImmersivePointerReleased(object sender, MouseButtonEventArgs e)
    {
        // Pointer clicks must not leave a button focused indefinitely and block immersive idle.
        // Keyboard focus stays in controls when the user tabs through the toolbar or information.
        if (_viewModel.IsFullScreen && Keyboard.FocusedElement is Button button)
        {
            _ = Dispatcher.InvokeAsync(() =>
            {
                if (ReferenceEquals(Keyboard.FocusedElement, button) && Mouse.Captured is null
                    && SettingsButton.ContextMenu?.IsOpen != true)
                {
                    Viewport.Focus();
                }
            }, DispatcherPriority.Input);
        }
    }

    private void OnImmersiveMenuClosed(object sender, RoutedEventArgs e) => RevealImmersiveControls();

    private void RevealImmersiveControls()
    {
        _immersiveTimer.Stop();
        SetChromeVisible(true);
        if (_viewModel.IsFullScreen && IsLoaded && IsActive)
        {
            _immersiveTimer.Start();
        }
    }

    private void OnImmersiveIdle(object? sender, EventArgs e)
    {
        if (!_viewModel.IsFullScreen || !IsActive)
        {
            _immersiveTimer.Stop();
            return;
        }

        if (Mouse.Captured is not null || Mouse.LeftButton == MouseButtonState.Pressed
            || Mouse.RightButton == MouseButtonState.Pressed || SettingsButton.ContextMenu?.IsOpen == true
            || Toolbar.IsMouseOver || InformationPanel.IsMouseOver || Filmstrip.IsMouseOver
            || PreviewStatusOverlay.IsMouseOver || StatusBar.IsMouseOver
            || LoadingOverlay.IsMouseOver || MessageOverlay.IsMouseOver
            || Toolbar.IsKeyboardFocusWithin || InformationPanel.IsKeyboardFocusWithin
            || Filmstrip.IsKeyboardFocusWithin || PreviewStatusOverlay.IsKeyboardFocusWithin)
        {
            return;
        }

        _immersiveTimer.Stop();
        SetChromeVisible(false);
    }

    private void SetChromeVisible(bool visible)
    {
        SetValue(IsChromeVisiblePropertyKey, visible);
        if (visible)
        {
            ClearValue(CursorProperty);
            ClearValue(ForceCursorProperty);
        }
        else
        {
            // ForceCursor also suppresses the viewport's hand cursor during a fullscreen pause.
            Cursor = Cursors.None;
            ForceCursor = true;
        }
    }
}
