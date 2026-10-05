using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;

using ModernImageViewer.Application.Integration;
using ModernImageViewer.UI.Commands;

namespace ModernImageViewer.UI.ViewModels;

public sealed partial class MainWindowViewModel
{
    private readonly IClipboardFileService? _clipboardFiles;

    public string PasteFilesLabel => Text("Command_PasteFiles");
    public AsyncRelayCommand PasteFilesCommand { get; }

    private async Task PasteFilesAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        IReadOnlyList<string> paths;
        try
        {
            // WPF calls this before the first await, on its UI STA thread.
            paths = _clipboardFiles?.ReadFiles() ?? Array.Empty<string>();
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
        if (paths.Count == 0)
        {
            ShowMessage("Clipboard_NoFiles");
            return;
        }
        await OpenInputsAsync(paths);
    }
}
