using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Input;

using ModernImageViewer.Application.Editing;
using ModernImageViewer.Application.Images;
using ModernImageViewer.Imaging;

namespace ModernImageViewer.UI;

public partial class MainWindow
{
    private readonly IImageExportService? _imageExporter;
    private bool _openingEditor;

    private async void OnEditClick(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.CanEditStatic || _imageExporter is null || _openingEditor) { return; }
        _openingEditor = true;
        _viewModel.IsSlideshowPlaying = false;
        _viewModel.CancelRefinement();
        ImageOpenState presentation = _viewModel.Presentation;
        ViewOrientation orientation = Viewport.Orientation;
        IInputElement? focus = Keyboard.FocusedElement;
        using CancellationTokenSource cancellation = new();
        bool IsCurrentSource() => IsLoaded && ReferenceEquals(presentation.Image, _viewModel.CurrentImage)
            && presentation.Source?.Identity == _viewModel.Presentation.Source?.Identity
            && string.Equals(presentation.FilePath, _viewModel.CurrentFilePath, StringComparison.OrdinalIgnoreCase);
        // Memory sources have no path; the VM may expose an empty display path.
        bool IsCurrentMemory() => IsLoaded && ReferenceEquals(presentation.Image, _viewModel.CurrentImage)
            && presentation.Source?.Identity == _viewModel.Presentation.Source?.Identity;
        void OnSourceChanged(object? changedSender, PropertyChangedEventArgs args)
        {
            if (!(presentation.IsMemorySource ? IsCurrentMemory() : IsCurrentSource())) { cancellation.Cancel(); }
        }
        void OnWindowClosed(object? closedSender, EventArgs args) => cancellation.Cancel();
        _viewModel.PropertyChanged += OnSourceChanged;
        Closed += OnWindowClosed;
        try
        {
            if (presentation.IsMemorySource)
            {
                const long byteLimit = 64L * 1024 * 1024;
                if (presentation.Source is not { Identity: var identity, Memory: { } memory } || identity == Guid.Empty
                    || presentation.FilePath is not null || memory.SourceSize != presentation.Image!.SourceSize)
                {
                    throw new ImageExportException(ImageExportError.SourceChanged);
                }
                if (memory.SourceSize.PixelCount * 4 > byteLimit)
                {
                    throw new ImageExportException(ImageExportError.BudgetExceeded);
                }
                ImageExportPixels pixels;
                if (presentation.Image.Size == memory.SourceSize)
                {
                    pixels = new(presentation.Image.Size, presentation.Image.Stride, presentation.Image.Pixels);
                }
                else
                {
                    using PixelBuffer full = await memory.ReadPixelsAsync(memory.SourceSize, byteLimit, cancellation.Token);
                    if (!IsCurrentMemory() || cancellation.IsCancellationRequested) { return; }
                    if (full.Size != memory.SourceSize || full.SourceSize != memory.SourceSize)
                    {
                        throw new ImageExportException(ImageExportError.SourceChanged);
                    }
                    pixels = new(full.Size, full.Stride, full.Pixels);
                }
                if ((long)pixels.Stride * pixels.Size.Height > byteLimit || pixels.Pixels.Length > byteLimit)
                {
                    throw new ImageExportException(ImageExportError.BudgetExceeded);
                }
                if (!IsCurrentMemory() || cancellation.IsCancellationRequested) { return; }
                EditWindow memoryEditor = new(presentation, orientation, _localization ?? _viewModel.Localization,
                    _imageExporter, sourcePixels: pixels, settings: _viewModel.Settings)
                { Owner = this };
                memoryEditor.ShowDialog();
                return;
            }
            if (string.IsNullOrWhiteSpace(presentation.FilePath))
            {
                throw new ImageExportException(ImageExportError.SourceChanged);
            }
            var source = await Task.Run(() =>
            {
                FileInfo file = new(presentation.FilePath!);
                return (file.Length, file.LastWriteTimeUtc);
            });
            if (!IsCurrentSource() || cancellation.IsCancellationRequested) { return; }
            if (presentation.Image!.SourceFileStamp is { } loaded
                && loaded != new ModernImageViewer.Imaging.ImageFileStamp(source.Length, source.LastWriteTimeUtc))
            {
                _viewModel.ShowMessage("Edit_Error_SourceChanged");
                _messageTimer.Start();
                return;
            }
            byte[]? colorProfile = await _imageExporter.GetSourceColorProfileAsync(presentation.FilePath!, cancellation.Token);
            if (!IsCurrentSource() || cancellation.IsCancellationRequested) { return; }
            EditWindow editor = new(presentation, orientation, _localization ?? _viewModel.Localization,
                _imageExporter, source.Length, source.LastWriteTimeUtc, settings: _viewModel.Settings, sourceColorProfile: colorProfile)
            { Owner = this };
            editor.ShowDialog();
        }
        catch (OperationCanceledException) { }
        catch (ImageExportException exception)
        {
            if (!(presentation.IsMemorySource ? IsCurrentMemory() : IsCurrentSource())) { return; }
            _viewModel.ShowMessage($"Edit_Error_{exception.Error}");
            _messageTimer.Start();
        }
        catch (Exception exception) when (exception is ImageDecodeException or ImageSizeLimitExceededException or OutOfMemoryException)
        {
            if (!IsCurrentMemory()) { return; }
            _viewModel.ShowMessage(exception is ImageDecodeException ? "Edit_Error_DecodeFailed" : "Edit_Error_BudgetExceeded");
            _messageTimer.Start();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            if (!(presentation.IsMemorySource ? IsCurrentMemory() : IsCurrentSource())) { return; }
            _viewModel.ShowMessage("Edit_Error_SourceChanged");
            _messageTimer.Start();
        }
        finally
        {
            _viewModel.PropertyChanged -= OnSourceChanged;
            Closed -= OnWindowClosed;
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
