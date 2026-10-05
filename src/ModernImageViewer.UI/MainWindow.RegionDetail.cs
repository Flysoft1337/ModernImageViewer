using ModernImageViewer.Imaging;

namespace ModernImageViewer.UI;

public partial class MainWindow
{
    private void InitializeRegionDetail()
    {
        Viewport.ScaleChanged += OnRegionScaleChanged;
        Viewport.RegionDetailRequested += OnRegionDetailRequested;
    }

    private void OnRegionScaleChanged(object? sender, double scale) =>
        _viewModel.UpdateVisibleRegion(Viewport.VisibleDetailRegion);

    private async void OnRegionDetailRequested(object? sender, PixelRect bounds) =>
        await _viewModel.RequestRegionDetailAsync(bounds);

    private void DisposeRegionDetail()
    {
        Viewport.ScaleChanged -= OnRegionScaleChanged;
        Viewport.RegionDetailRequested -= OnRegionDetailRequested;
    }
}
