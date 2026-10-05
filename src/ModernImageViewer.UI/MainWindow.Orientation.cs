using System.Windows;
using System.Windows.Controls;

namespace ModernImageViewer.UI;

public partial class MainWindow
{
    private void OnOrientationChanged(object? sender, EventArgs e)
    {
        _viewModel.UpdateViewOrientation(Viewport.Orientation);
        _viewModel.UpdateVisibleRegion(Viewport.VisibleDetailRegion);
    }

    private void OnOrientationClick(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.HasImage || sender is not MenuItem { Tag: string action })
        {
            return;
        }
        switch (action)
        {
            case "Left": Viewport.RotateLeft(); break;
            case "Right": Viewport.RotateRight(); break;
            case "Horizontal": Viewport.FlipHorizontal(); break;
            case "Vertical": Viewport.FlipVertical(); break;
            case "Reset": Viewport.ResetOrientation(); break;
        }
    }
}
