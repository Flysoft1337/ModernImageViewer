using System.ComponentModel;
using System.Windows.Threading;

using ModernImageViewer.Imaging;
using ModernImageViewer.UI.ViewModels;

namespace ModernImageViewer.UI;

public partial class MainWindow
{
    private readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };

    private void InitializeAdaptivePreview()
    {
        Viewport.PreviewTargetChanged += OnPreviewTargetChanged;
        _viewModel.PropertyChanged += OnPreviewPresentationChanged;
        _viewModel.BrowsingCacheInvalidated += OnBrowsingCacheInvalidated;
        _previewTimer.Tick += OnPreviewTimer;
    }

    private void OnBrowsingCacheInvalidated(object? sender, EventArgs e) => Controls.ThumbnailImage.ClearCache();

    private void OnPreviewTargetChanged(object? sender, PixelSize target)
    {
        // New opens see the current target immediately; replacements wait for stable layout.
        _viewModel.SetPreviewTarget(target);
        QueuePreviewUpgrade();
    }

    private void OnPreviewPresentationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null or "" or nameof(MainWindowViewModel.Presentation)) { QueuePreviewUpgrade(); }
    }

    private void QueuePreviewUpgrade()
    {
        _previewTimer.Stop();
        if (IsLoaded && WindowState != System.Windows.WindowState.Minimized) { _previewTimer.Start(); }
    }

    private async void OnPreviewTimer(object? sender, EventArgs e)
    {
        _previewTimer.Stop();
        if (IsLoaded && WindowState != System.Windows.WindowState.Minimized)
        {
            await _viewModel.UpgradePreviewAsync(Viewport.CurrentPreviewTarget, Viewport.Orientation);
        }
    }

    private void DisposeAdaptivePreview()
    {
        _previewTimer.Stop();
        _previewTimer.Tick -= OnPreviewTimer;
        Viewport.PreviewTargetChanged -= OnPreviewTargetChanged;
        _viewModel.PropertyChanged -= OnPreviewPresentationChanged;
        _viewModel.BrowsingCacheInvalidated -= OnBrowsingCacheInvalidated;
    }
}
