namespace ModernImageViewer.Application.Integration;

/// <summary>Reads explicit clipboard file paths on the calling UI STA thread, without requesting image data.</summary>
public interface IClipboardFileService
{
    IReadOnlyList<string> ReadFiles();
}
