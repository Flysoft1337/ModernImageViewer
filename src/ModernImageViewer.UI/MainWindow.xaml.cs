using System.Windows;
using System.Windows.Input;

using ModernImageViewer.Application.Browsing;
using ModernImageViewer.UI.ViewModels;

namespace ModernImageViewer.UI;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;

    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        Viewport.ScaleChanged += (_, scale) => _viewModel.UpdateScale(scale);
    }

    private void OnFitClick(object sender, RoutedEventArgs e) => Viewport.Fit();

    private void OnActualSizeClick(object sender, RoutedEventArgs e) => Viewport.ActualSize();

    private void OnZoomInClick(object sender, RoutedEventArgs e) => Viewport.ZoomIn();

    private void OnZoomOutClick(object sender, RoutedEventArgs e) => Viewport.ZoomOut();

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
            await _viewModel.OpenPathAsync(path);
        }
    }

    private static string? GetSupportedDroppedPath(IDataObject data)
    {
        if (!data.GetDataPresent(DataFormats.FileDrop) || data.GetData(DataFormats.FileDrop) is not string[] paths)
        {
            return null;
        }

        return paths.FirstOrDefault(ImageBrowseSession.IsSupported);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.D0:
            case Key.NumPad0:
                Viewport.Fit();
                e.Handled = true;
                break;
            case Key.D1:
            case Key.NumPad1:
                Viewport.ActualSize();
                e.Handled = true;
                break;
            case Key.Add:
            case Key.OemPlus:
                Viewport.ZoomIn();
                e.Handled = true;
                break;
            case Key.Subtract:
            case Key.OemMinus:
                Viewport.ZoomOut();
                e.Handled = true;
                break;
        }
    }
}
