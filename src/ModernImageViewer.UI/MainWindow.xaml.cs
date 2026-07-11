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
}
