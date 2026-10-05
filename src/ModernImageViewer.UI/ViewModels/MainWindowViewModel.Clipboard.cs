using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;

using ModernImageViewer.Application.Images;
using ModernImageViewer.Application.Integration;
using ModernImageViewer.Imaging;
using ModernImageViewer.UI.Commands;

namespace ModernImageViewer.UI.ViewModels;

public sealed partial class MainWindowViewModel
{
    private readonly IClipboardFileService? _clipboardFiles;
    private readonly IImageClipboardService? _imageClipboard;
    private CancellationTokenSource? _clipboardWriteCancellation;

    public string PasteFilesLabel => Text("Command_PasteFiles");
    public string CopyPreviewLabel => Text("Clipboard_CopyPreview");
    public string CopyPreviewHint => Text("Clipboard_CopyPreviewHint");
    public string CopyOriginalLabel => Text("Clipboard_CopyOriginal");
    public string CopyOriginalHint => Text("Clipboard_CopyOriginalHint");
    public string InformationLocationLabel => Text(Presentation.IsMemorySource ? "Information_Source" : "Information_Folder");
    public string InformationLocation => Presentation.IsMemorySource ? Text("Clipboard_Source") : DirectoryPath;
    public bool CanCopyImage => HasImage && !IsLoading && _imageClipboard is not null;
    public bool CanCopyOriginal => CanCopyImage && CurrentImage is { } image && image.Size == image.SourceSize
        && (long)image.Stride * image.Size.Height <= ClipboardImageLimits.SourceBytes;
    public AsyncRelayCommand PasteFilesCommand { get; }
    public AsyncRelayCommand CopyPreviewCommand { get; }
    public AsyncRelayCommand CopyOriginalCommand { get; }

    private async Task PasteFilesAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ImageClipboardInput input;
        try
        {
            // Read a single STA snapshot before awaiting any background pixel work.
            input = _imageClipboard?.ReadInput() ?? new(_clipboardFiles?.ReadFiles() ?? Array.Empty<string>());
        }
        catch (ImageDecodeException exception)
        {
            ShowMessage($"Error_{exception.Error}");
            return;
        }
        catch (ArgumentException)
        {
            ShowMessage("Input_TooManyFiles");
            return;
        }
        catch (Exception exception) when (exception is ExternalException or IOException
            or SecurityException or InvalidOperationException or NotSupportedException or Win32Exception)
        {
            ShowMessage("Error_ClipboardBusy");
            return;
        }
        if (input.Files.Count > 0)
        {
            await OpenInputsAsync(input.Files);
        }
        else if (input.Image is { } image)
        {
            IsSlideshowPlaying = false;
            CancelFolderWork();
            _refreshCancellation?.Cancel();
            using CancellationTokenSource cancellation = new();
            _folderCancellation = cancellation;
            try { await _coordinator.OpenMemoryAsync(image, _browseSession, cancellation.Token); }
            finally
            {
                if (ReferenceEquals(_folderCancellation, cancellation)) { _folderCancellation = null; }
            }
        }
        else
        {
            ShowMessage("Clipboard_NoFiles");
        }
    }

    private async Task CopyImageAsync(bool originalSize)
    {
        if (_disposed || !CanCopyImage || (originalSize && !CanCopyOriginal)) { return; }
        CancelClipboardWrite();
        using CancellationTokenSource cancellation = new();
        _clipboardWriteCancellation = cancellation;
        long version = _inputVersion;
        PixelBuffer image = CurrentImage!;
        // ReadOnlyMemory keeps this immutable array alive if browsing disposes the PixelBuffer wrapper.
        ImageClipboardPixels snapshot = new(image.Size, image.Stride, image.Pixels, _viewOrientation, originalSize);
        try
        {
            await _imageClipboard!.WriteAsync(snapshot, cancellation.Token);
            if (!_disposed && version == _inputVersion && !cancellation.IsCancellationRequested)
            {
                ShowMessage(originalSize ? "Clipboard_OriginalCopied" : "Clipboard_PreviewCopied");
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception) when (exception is ImageDecodeException or ImageSizeLimitExceededException
            or ExternalException or IOException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            if (!_disposed && version == _inputVersion && !cancellation.IsCancellationRequested)
            {
                ShowMessage(exception is ImageSizeLimitExceededException
                    || exception is ImageDecodeException { Error: ImageOpenError.ImageTooLarge }
                    ? "Error_ImageTooLarge" : "Error_ClipboardBusy");
            }
        }
        finally
        {
            if (ReferenceEquals(_clipboardWriteCancellation, cancellation)) { _clipboardWriteCancellation = null; }
        }
    }

    private void CancelClipboardWrite()
    {
        _clipboardWriteCancellation?.Cancel();
        _clipboardWriteCancellation = null;
    }
}
