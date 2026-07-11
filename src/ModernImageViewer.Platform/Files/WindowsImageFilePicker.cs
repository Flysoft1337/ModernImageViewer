using Microsoft.Win32;

using ModernImageViewer.Application.Images;

namespace ModernImageViewer.Platform.Files;

public sealed class WindowsImageFilePicker : IImageFilePicker
{
    public Task<string?> PickImageAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        OpenFileDialog dialog = new()
        {
            CheckFileExists = true,
            Filter = "Images (*.jpg;*.jpeg;*.png)|*.jpg;*.jpeg;*.png|All files (*.*)|*.*",
            Multiselect = false,
            Title = "Open image",
        };

        return Task.FromResult(dialog.ShowDialog() == true ? dialog.FileName : null);
    }
}
