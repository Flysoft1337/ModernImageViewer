using System.ComponentModel;
using System.IO;
using System.Security;

using ModernImageViewer.Application.Integration;
using ModernImageViewer.UI.Localization;

namespace ModernImageViewer.UI.ViewModels;

public sealed class FileAssociationViewModel : INotifyPropertyChanged
{
    private readonly IFileAssociationService _service;
    private readonly ILocalizationService _localization;
    private FileAssociationStatus _status = new(false, false, null, false);
    private bool _isBusy;
    private bool _statusKnown;
    private string? _messageKey;

    public FileAssociationViewModel(IFileAssociationService service, ILocalizationService localization)
    {
        _service = service;
        _localization = localization;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Title => Text("Association_Title");
    public string Intro => Text("Association_Intro");
    public string FormatsLabel => Text("Association_Formats");
    public string LocationLabel => Text("Association_CurrentLocation");
    public string RegisteredPath => _status.ExecutablePath ?? "—";
    public string RegisterLabel => Text(_status.IsRegistered && !_status.IsOwnedByCurrentExecutable
        ? "Association_Reregister" : "Association_Register");
    public string UnregisterLabel => Text("Association_Unregister");
    public string DefaultAppsLabel => Text("Association_DefaultApps");
    public string DefaultHint => Text("Association_DefaultHint");
    public string PortableHint => Text(_status.IsInstalled ? "Association_InstalledHint" : "Association_PortableHint");
    public string CloseLabel => Text("Window_Close");
    public bool CanRegister => !_isBusy && _status.CanRegister;
    public bool CanUnregister => !_isBusy && _status.IsOwnedByCurrentExecutable;
    public bool CanOpenSettings => !_isBusy;
    public bool IsBusy => _isBusy;
    public bool HasMessage => _messageKey is not null || (_statusKnown && !_status.CanRegister);
    public string Message => Text(_messageKey ?? "Association_DevelopmentHint");
    public string StatusLabel => Text(_isBusy || (!_statusKnown && _messageKey is null) ? "Association_Working"
        : !_statusKnown ? "Association_ReadFailed" : !_status.IsRegistered && _status.IsOwnedByCurrentExecutable
        ? "Association_Partial" : _status.IsRegistered
        ? _status.IsOwnedByCurrentExecutable ? "Association_Registered" : "Association_OtherLocation"
        : "Association_NotRegistered");

    public Task RegisterAsync() => ChangeRegistrationAsync(_service.Register, "Association_Success");
    public Task UnregisterAsync() => ChangeRegistrationAsync(_service.Unregister, "Association_Removed");

    public async Task InitializeAsync()
    {
        _isBusy = true;
        NotifyAll();
        await ReadStatusAsync();
        _isBusy = false;
        NotifyAll();
    }

    public void OpenSettings()
    {
        try
        {
            _service.OpenDefaultAppsSettings();
        }
        catch (Exception exception) when (IsIntegrationError(exception))
        {
            _messageKey = "Association_SettingsFailed";
            NotifyAll();
        }
    }

    private async Task ChangeRegistrationAsync(Action change, string successMessage)
    {
        if (_isBusy)
        {
            return;
        }
        _isBusy = true;
        _messageKey = null;
        NotifyAll();
        try
        {
            await Task.Run(change);
            _messageKey = successMessage;
        }
        catch (Exception exception) when (IsIntegrationError(exception))
        {
            _messageKey = "Association_Failed";
        }
        finally
        {
            await ReadStatusAsync();
            _isBusy = false;
            NotifyAll();
        }
    }

    private async Task ReadStatusAsync()
    {
        try
        {
            _status = await Task.Run(_service.ReadStatus);
            _statusKnown = true;
        }
        catch (Exception exception) when (IsIntegrationError(exception))
        {
            _messageKey = "Association_ReadFailed";
        }
    }

    private static bool IsIntegrationError(Exception exception) => exception is IOException
        or UnauthorizedAccessException or SecurityException or InvalidOperationException
        or Win32Exception or ArgumentException or NotSupportedException;

    private string Text(string key) => _localization.GetString(key);

    private void NotifyAll() =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}
