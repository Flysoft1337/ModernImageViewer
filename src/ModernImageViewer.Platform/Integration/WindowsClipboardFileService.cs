using System.Windows;

using ModernImageViewer.Application.Images;
using ModernImageViewer.Application.Integration;

namespace ModernImageViewer.Platform.Integration;

public sealed class WindowsClipboardFileService : IClipboardFileService
{
    private readonly Func<IDataObject?> _readData;

    public WindowsClipboardFileService() : this(Clipboard.GetDataObject) { }

    public WindowsClipboardFileService(Func<IDataObject?> readData)
    {
        ArgumentNullException.ThrowIfNull(readData);
        _readData = readData;
    }

    public IReadOnlyList<string> ReadFiles()
    {
        // Read one snapshot and only the exact file-drop format. Never convert a bitmap or read pixels.
        IDataObject? data = _readData();
        if (data?.GetData(DataFormats.FileDrop, autoConvert: false) is not string[] paths)
        {
            return Array.Empty<string>();
        }
        if (paths.Length > OpenRequest.MaximumPaths)
        {
            throw new ArgumentException($"At most {OpenRequest.MaximumPaths} paths can be opened at once.");
        }
        return paths;
    }
}
