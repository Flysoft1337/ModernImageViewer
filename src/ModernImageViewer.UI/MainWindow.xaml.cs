using System.Windows;

using ModernImageViewer.UI.ViewModels;

namespace ModernImageViewer.UI;

public partial class MainWindow : Window
{
    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void OnFitClick(object sender, RoutedEventArgs e)
    {
        Viewport.Fit();
    }

    private void OnActualSizeClick(object sender, RoutedEventArgs e)
    {
        Viewport.ActualSize();
    }
}
