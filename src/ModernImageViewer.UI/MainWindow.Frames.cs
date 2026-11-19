using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

using ModernImageViewer.Application.Images;
using ModernImageViewer.Imaging;
using ModernImageViewer.UI.ViewModels;

namespace ModernImageViewer.UI;

public partial class MainWindow
{
    private DispatcherTimer? _frameTimer;
    private bool _frameTickRunning;

    private void InitializeFrameControls()
    {
        _viewModel.PropertyChanged += OnFrameStateChanged;
        Loaded += OnFrameAvailabilityChanged;
        Unloaded += OnFrameAvailabilityChanged;
        IsVisibleChanged += OnFrameVisibilityChanged;
        Activated += OnFrameActivationChanged;
        Deactivated += OnFrameActivationChanged;
        StateChanged += OnFrameActivationChanged;
        Viewport.Loaded += OnFrameAvailabilityChanged;
        Viewport.Unloaded += OnFrameAvailabilityChanged;
        UpdateFrameAvailability();
    }

    private void OnFrameAvailabilityChanged(object sender, RoutedEventArgs e) => UpdateFrameAvailability();
    private void OnFrameVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) => UpdateFrameAvailability();
    private void OnFrameActivationChanged(object? sender, EventArgs e) => UpdateFrameAvailability();

    private void UpdateFrameAvailability()
    {
        _viewModel.SetFramePresentationAvailable(IsLoaded && Viewport.IsLoaded && IsVisible && IsActive && WindowState != WindowState.Minimized);
        UpdateFrameTimer();
    }

    private void OnFrameStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null or "" or nameof(MainWindowViewModel.IsAnimationPlaying)
            or nameof(MainWindowViewModel.HasFrameSequence)) { UpdateFrameTimer(); }
    }

    private void UpdateFrameTimer()
    {
        bool play = _viewModel.IsAnimationPlaying && _viewModel.IsAnimation && IsLoaded && IsVisible
            && Viewport.IsLoaded && IsActive && WindowState != WindowState.Minimized;
        if (!play)
        {
            _frameTimer?.Stop();
            if (!_viewModel.HasFrameSequence) { ReleaseFrameTimer(); }
            return;
        }
        if (_frameTimer is null)
        {
            _frameTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(20) };
            _frameTimer.Tick += OnFrameTick;
        }
        if (!_frameTimer.IsEnabled && !_frameTickRunning)
        {
            _frameTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(_viewModel.NextFrameDueMilliseconds, 1, 1000));
            _frameTimer.Start();
        }
    }

    private async void OnFrameTick(object? sender, EventArgs e)
    {
        _frameTimer?.Stop();
        _frameTickRunning = true;
        try { await _viewModel.PulseAnimationAsync(); }
        finally { _frameTickRunning = false; UpdateFrameTimer(); }
    }

    private async void OnAnimationToggleClick(object sender, RoutedEventArgs e) => await _viewModel.ToggleAnimationAsync();
    private async void OnAnimationRestartClick(object sender, RoutedEventArgs e) => await _viewModel.RestartAnimationAsync();
    private async void OnPreviousFrameClick(object sender, RoutedEventArgs e) => await _viewModel.MoveFrameAsync(-1);
    private async void OnNextFrameClick(object sender, RoutedEventArgs e) => await _viewModel.MoveFrameAsync(1);

    private async void OnFrameIndexKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox box) { return; }
        e.Handled = true;
        if (int.TryParse(box.Text, out int page) && page > 0) { await _viewModel.SeekFrameAsync(page - 1); }
        box.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
        Viewport.Focus();
    }

    private void OnFrameIndexFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_viewModel.IsAnimationPlaying) { _viewModel.ToggleAnimation(); }
        if (sender is TextBox box) { box.SelectAll(); }
    }

    private async void OnFrameIndexLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox box)
        {
            if (int.TryParse(box.Text, out int page) && page > 0 && page - 1 != _viewModel.Presentation.FrameIndex)
            {
                await _viewModel.SeekFrameAsync(page - 1);
            }
            box.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
        }
    }

    private void OnIconSizeClick(object sender, RoutedEventArgs e)
    {
        ImageOpenState state = _viewModel.Presentation;
        if (state is not { Status: ImageOpenStatus.Loaded, Sequence.Kind: ImageSequenceKind.IconSizes }) { return; }
        RevealImmersiveControls();
        ContextMenu menu = new() { PlacementTarget = IconSizeButton, Placement = PlacementMode.Top };
        menu.Items.Add(CreateOption(_viewModel.IconSizeAutomaticLabel, null, state.IsIconSizeAutomatic));
        menu.Items.Add(new Separator());
        foreach (var sizes in state.Sequence.Frames.GroupBy(frame => frame.CanvasSize).OrderBy(group => group.Key.PixelCount))
        {
            menu.Items.Add(CreateOption(_viewModel.FormatIconSize(sizes.Key), sizes.First().Index,
                !state.IsIconSizeAutomatic && sizes.Any(frame => frame.Index == state.FrameIndex)));
        }
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(IconSizeButton.ContextMenu, menu)) { IconSizeButton.ContextMenu = null; }
            RevealImmersiveControls();
        };
        IconSizeButton.ContextMenu = menu;
        UpdateMenuBounds(menu);
        menu.IsOpen = true;

        MenuItem CreateOption(string label, int? index, bool selected)
        {
            MenuItem item = new() { Header = label, IsCheckable = true, IsChecked = selected };
            item.Click += async (_, _) =>
            {
                if (state.Source?.Identity == _viewModel.Presentation.Source?.Identity)
                {
                    await _viewModel.SelectIconSizeAsync(index, Viewport.Orientation);
                }
            };
            return item;
        }
    }

    private void ReleaseFrameTimer()
    {
        if (_frameTimer is null) { return; }
        _frameTimer.Stop();
        _frameTimer.Tick -= OnFrameTick;
        _frameTimer = null;
    }

    private void DisposeFrameControls()
    {
        _viewModel.SetFramePresentationAvailable(false);
        _viewModel.StopFramePresentation();
        _viewModel.PropertyChanged -= OnFrameStateChanged;
        Loaded -= OnFrameAvailabilityChanged;
        Unloaded -= OnFrameAvailabilityChanged;
        IsVisibleChanged -= OnFrameVisibilityChanged;
        Activated -= OnFrameActivationChanged;
        Deactivated -= OnFrameActivationChanged;
        StateChanged -= OnFrameActivationChanged;
        Viewport.Loaded -= OnFrameAvailabilityChanged;
        Viewport.Unloaded -= OnFrameAvailabilityChanged;
        ReleaseFrameTimer();
    }
}
