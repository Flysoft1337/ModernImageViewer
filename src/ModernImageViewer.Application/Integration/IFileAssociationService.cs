namespace ModernImageViewer.Application.Integration;

public sealed record FileAssociationStatus(
    bool IsRegistered,
    bool IsOwnedByCurrentExecutable,
    string? ExecutablePath,
    bool CanRegister);

public interface IFileAssociationService
{
    FileAssociationStatus ReadStatus();

    void Register();

    void Unregister();

    void OpenDefaultAppsSettings();
}
