using System.Windows;
using System.Windows.Input;

using ModernImageViewer.UI.Localization;
using ModernImageViewer.UI.ViewModels;

namespace ModernImageViewer.UI;

public sealed partial class ShortcutHelpWindow : Window, IDisposable
{
    private readonly ShortcutHelpViewModel _viewModel;

    public ShortcutHelpWindow(ILocalizationService localization)
    {
        InitializeComponent();
        _viewModel = new(localization);
        DataContext = _viewModel;
        MaxHeight = Math.Max(MinHeight, SystemParameters.WorkArea.Height);
        MaxWidth = Math.Max(MinWidth, SystemParameters.WorkArea.Width);
        Closed += (_, _) => Dispose();
    }

    public void Dispose()
    {
        _viewModel.Dispose();
        GC.SuppressFinalize(this);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (ShortcutCatalog.Match(e.Key, Keyboard.Modifiers) == ViewerAction.DismissOverlay)
        {
            Close();
            e.Handled = true;
        }
    }
}
