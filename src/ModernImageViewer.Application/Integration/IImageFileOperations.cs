namespace ModernImageViewer.Application.Integration;

public enum ImageFileOperationResult
{
    Success,
    MissingFile,
    NameConflict,
    AccessDenied,
    FileInUse,
    Unavailable,
}

public interface IImageFileOperations
{
    Task<ImageFileOperationResult> RenameAsync(string sourcePath, string destinationPath);
    Task<ImageFileOperationResult> RecycleAsync(string path);
}
