using System.IO;

namespace ModernImageViewer.UI.ViewModels;

public sealed record BrowseItem(string FilePath, int Index, bool IsCurrent = false)
{
    public string FileName => Path.GetFileName(FilePath);
}
