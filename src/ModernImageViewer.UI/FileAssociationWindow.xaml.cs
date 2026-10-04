using System.Windows;
using System.Windows.Input;

using ModernImageViewer.Application.Integration;
using ModernImageViewer.UI.Localization;
using ModernImageViewer.UI.ViewModels;

namespace ModernImageViewer.UI;

public partial class FileAssociationWindow : Window
{
    private readonly FileAssociationViewModel _viewModel;

    public FileAssociationWindow(IFileAssociationService service, ILocalizationService localization)
    {
        _viewModel = new(service, localization);
        InitializeComponent();
        DataContext = _viewModel;
        Loaded += async (_, _) => await _viewModel.InitializeAsync();
    }

    private async void OnRegisterClick(object sender, RoutedEventArgs e) => await _viewModel.RegisterAsync();
    private async void OnUnregisterClick(object sender, RoutedEventArgs e) => await _viewModel.UnregisterAsync();
    private void OnDefaultAppsClick(object sender, RoutedEventArgs e) => _viewModel.OpenSettings();
    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }
}
