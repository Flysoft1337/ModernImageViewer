namespace ModernImageViewer.Application.Images;

public interface IImageFilePicker
{
    Task<string?> PickImageAsync(CancellationToken cancellationToken);
}
