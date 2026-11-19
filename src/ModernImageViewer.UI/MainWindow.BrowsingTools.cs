using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

using ModernImageViewer.Application.Browsing;

namespace ModernImageViewer.UI;

public partial class MainWindow
{
    private async void OnSortModeClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string tag } && Enum.TryParse(tag, out BrowseSortMode mode))
        {
            await _viewModel.ChangeSortAsync(mode, _viewModel.SortDescending);
        }
    }

    private async void OnSortDirectionClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string tag })
        {
            await _viewModel.ChangeSortAsync(_viewModel.SortMode, tag == "Descending");
        }
    }

    private async void OnRevealClick(object sender, RoutedEventArgs e)
    {
        await _viewModel.RevealCurrentFileAsync();
        _messageTimer.Stop();
        _messageTimer.Start();
    }

    private void OnRenameClick(object sender, RoutedEventArgs e) => ShowFileOperation(rename: true);

    private void OnRecycleClick(object sender, RoutedEventArgs e) => ShowFileOperation(rename: false);

    private void ShowFileOperation(bool rename)
    {
        if (!_viewModel.CanChangeFile) { return; }
        _viewModel.IsSlideshowPlaying = false;
        FileOperationWindow dialog = new(_viewModel, _viewModel.CurrentFilePath, rename) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            _messageTimer.Stop();
            _messageTimer.Start();
        }
        Viewport.Focus();
    }

    private void OnShortcutHelpClick(object sender, RoutedEventArgs e) => ShowShortcutHelp();

    private void ShowShortcutHelp()
    {
        _viewModel.IsSlideshowPlaying = false;
        RevealImmersiveControls();
        IInputElement? focus = Keyboard.FocusedElement;
        using ShortcutHelpWindow help = new(_localization ?? _viewModel.Localization) { Owner = this };
        help.ShowDialog();
        if (IsLoaded)
        {
            RevealImmersiveControls();
            if (focus is UIElement { IsVisible: true, IsEnabled: true } element)
            {
                element.Focus();
            }
            else
            {
                Viewport.Focus();
            }
        }
    }
}
