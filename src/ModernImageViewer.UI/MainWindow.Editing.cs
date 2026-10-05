using System.IO;
using System.Windows;
using System.Windows.Input;

using ModernImageViewer.Application.Editing;
using ModernImageViewer.Application.Images;

namespace ModernImageViewer.UI;

public partial class MainWindow
{
    private readonly IImageExportService? _imageExporter;
    private bool _openingEditor;

    private async void OnEditClick(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.HasImage || _imageExporter is null || _openingEditor) { return; }
        _openingEditor = true;
        _viewModel.IsSlideshowPlaying = false;
        _viewModel.CancelRefinement();
        ImageOpenState presentation = _viewModel.Presentation;
        IInputElement? focus = Keyboard.FocusedElement;
        try
        {
            var source = await Task.Run(() =>
            {
                FileInfo file = new(presentation.FilePath!);
                return (file.Length, file.LastWriteTimeUtc);
            });
            if (!IsLoaded || !ReferenceEquals(presentation.Image, _viewModel.CurrentImage)
                || !string.Equals(presentation.FilePath, _viewModel.CurrentFilePath, StringComparison.OrdinalIgnoreCase)) { return; }
            if (presentation.Image!.SourceFileStamp is { } loaded
                && loaded != new ModernImageViewer.Imaging.ImageFileStamp(source.Length, source.LastWriteTimeUtc))
            {
                _viewModel.ShowMessage("Edit_Error_SourceChanged");
                _messageTimer.Start();
                return;
            }
            EditWindow editor = new(presentation, Viewport.Orientation, _localization ?? _viewModel.Localization,
                _imageExporter, source.Length, source.LastWriteTimeUtc)
            { Owner = this };
            editor.ShowDialog();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _viewModel.ShowMessage("Edit_Error_SourceChanged");
            _messageTimer.Start();
        }
        finally
        {
            _openingEditor = false;
            if (IsLoaded)
            {
                RevealImmersiveControls();
                if (focus is UIElement { IsVisible: true, IsEnabled: true } element) { element.Focus(); }
                else { Viewport.Focus(); }
            }
        }
    }
}
