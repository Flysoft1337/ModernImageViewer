namespace ModernImageViewer.Application.Integration;

public enum FileRevealResult
{
    Success,
    MissingFile,
    Unavailable,
    Canceled,
}

public interface IFileRevealService
{
    Task<FileRevealResult> RevealAsync(string path, CancellationToken cancellationToken = default);
}
