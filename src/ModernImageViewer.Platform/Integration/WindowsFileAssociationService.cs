using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

using Microsoft.Win32;

using ModernImageViewer.Application.Integration;

namespace ModernImageViewer.Platform.Integration;

/// <summary>Opt-in, per-user association candidates for installed and portable distributions.</summary>
public sealed class WindowsFileAssociationService : IFileAssociationService
{
    private readonly RegistrationProfile _profile;
    private string ProgId => _profile.ProgId;
    private string ApplicationId => _profile.ApplicationId;
    private string ApplicationName => _profile.ApplicationName;
    private string Owner => _profile.Owner;
    private string ApplicationKey => _profile.ApplicationKey;
    private string CapabilitiesKey => ApplicationKey + @"\Capabilities";
    private string ProgIdKey => @"Software\Classes\" + ProgId;
    private const string RegisteredApplicationsKey = @"Software\RegisteredApplications";
    private static readonly string[] s_extensions = [".jpg", ".jpeg", ".png"];
    private static readonly object s_registrationGate = new();

    private readonly RegistryKey _registryRoot;
    private readonly string _executablePath;
    private readonly bool _canRegister;
    private readonly bool _isSystemRoot;
    private readonly string _registrationGateName;

    public WindowsFileAssociationService()
        : this(Registry.CurrentUser, Environment.ProcessPath ?? string.Empty, IsPublishedExecutable(), true, false)
    {
    }

    /// <summary>Uses an isolated HKCU root for registration verification without touching Shell associations.</summary>
    public WindowsFileAssociationService(RegistryKey registryRoot, string executablePath, bool canRegister, bool isInstalled = false)
        : this(registryRoot, executablePath, canRegister, false, isInstalled)
    {
    }

    private WindowsFileAssociationService(RegistryKey registryRoot, string executablePath, bool canRegister, bool isSystemRoot, bool isInstalled)
    {
        ArgumentNullException.ThrowIfNull(registryRoot);
        ArgumentNullException.ThrowIfNull(executablePath);
        if (!registryRoot.Name.StartsWith(@"HKEY_CURRENT_USER\", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(registryRoot.Name, "HKEY_CURRENT_USER", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("File associations must use the current user's registry.", nameof(registryRoot));
        }

        _registryRoot = registryRoot;
        _executablePath = executablePath;
        _canRegister = canRegister && IsValidExecutablePath(executablePath);
        _profile = isInstalled || isSystemRoot && _canRegister && HasInstallationMarker(executablePath)
            ? RegistrationProfile.Installed : RegistrationProfile.Portable;
        _isSystemRoot = isSystemRoot;
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        string gateIdentity = identity.User?.Value + ":" + registryRoot.Name;
        _registrationGateName = @"Local\" + ApplicationId + ".Associations."
            + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(gateIdentity)));
    }

    public FileAssociationStatus ReadStatus()
    {
        lock (s_registrationGate)
        {
            using RegistrationLease lease = new(_registrationGateName);
            string? path = ReadString(ApplicationKey, "ExecutablePath");
            bool registered = path is not null && HasCompleteRegistration(path);
            bool owned = path is not null && HasCoreOwnership(path) && PathsEqual(path, _executablePath);
            return new FileAssociationStatus(registered, owned, path, _canRegister, _profile.IsInstalled);
        }
    }

    public void Register()
    {
        if (!_canRegister)
        {
            throw new InvalidOperationException("File associations require a published, self-contained ModernImageViewer.App.exe.");
        }

        lock (s_registrationGate)
        {
            using RegistrationLease lease = new(_registrationGateName);
            EnsurePrivateKeyCanBeUsed(ApplicationKey);
            EnsurePrivateKeyCanBeUsed(ProgIdKey);
            string? registeredApplication = ReadString(RegisteredApplicationsKey, ApplicationId);
            if (registeredApplication is not null && !string.Equals(registeredApplication, CapabilitiesKey, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The application identity is already used by another registration.");
            }

            SetString(ApplicationKey, "Owner", Owner);
            SetString(ApplicationKey, "ExecutablePath", _executablePath);
            SetString(ProgIdKey, "Owner", Owner);
            SetString(ProgIdKey, "", "Modern Image Viewer image");
            SetString(ProgIdKey + @"\DefaultIcon", "", Icon(_executablePath));
            SetString(ProgIdKey + @"\shell\open\command", "", Command(_executablePath));
            SetString(CapabilitiesKey, "ApplicationName", ApplicationName);
            SetString(CapabilitiesKey, "ApplicationDescription", "Browse JPEG and PNG images with Modern Image Viewer.");
            SetString(CapabilitiesKey, "ApplicationIcon", Icon(_executablePath));
            foreach (string extension in s_extensions)
            {
                SetString(CapabilitiesKey + @"\FileAssociations", extension, ProgId);
                using RegistryKey candidates = _registryRoot.CreateSubKey(@"Software\Classes\" + extension + @"\OpenWithProgids");
                candidates.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
            }

            SetString(RegisteredApplicationsKey, ApplicationId, CapabilitiesKey);
            NotifyShell();
        }
    }

    public void Unregister()
    {
        lock (s_registrationGate)
        {
            using RegistrationLease lease = new(_registrationGateName);
            // A moved or newer copy may own this distribution's stable identity now.
            FileAssociationStatus status = ReadStatus();
            if (!status.IsOwnedByCurrentExecutable)
            {
                return;
            }

            string registeredPath = status.ExecutablePath!;

            foreach (string extension in s_extensions)
            {
                string candidatesPath = @"Software\Classes\" + extension + @"\OpenWithProgids";
                using (RegistryKey? candidates = _registryRoot.OpenSubKey(candidatesPath, writable: true))
                {
                    if (candidates?.GetValue(ProgId) is byte[] bytes && bytes.Length == 0
                        && candidates.GetValueKind(ProgId) == RegistryValueKind.None)
                    {
                        candidates.DeleteValue(ProgId, throwOnMissingValue: false);
                    }
                }

                DeleteMatchingValue(CapabilitiesKey + @"\FileAssociations", extension, ProgId);
            }

            DeleteMatchingValue(RegisteredApplicationsKey, ApplicationId, CapabilitiesKey, pruneKey: false);
            DeleteMatchingValue(ProgIdKey + @"\shell\open\command", "", Command(registeredPath));
            DeleteMatchingValue(ProgIdKey + @"\DefaultIcon", "", Icon(registeredPath));
            DeleteEmptyKey(ProgIdKey + @"\shell\open");
            DeleteEmptyKey(ProgIdKey + @"\shell");
            DeleteMatchingValue(ProgIdKey, "", "Modern Image Viewer image");
            RemovePrivateOwnerIfEmpty(ProgIdKey);
            DeleteMatchingValue(CapabilitiesKey, "ApplicationName", ApplicationName);
            DeleteMatchingValue(CapabilitiesKey, "ApplicationDescription", "Browse JPEG and PNG images with Modern Image Viewer.");
            DeleteMatchingValue(CapabilitiesKey, "ApplicationIcon", Icon(registeredPath));
            DeleteMatchingValue(ApplicationKey, "ExecutablePath", registeredPath);
            RemovePrivateOwnerIfEmpty(ApplicationKey);
            NotifyShell();
        }
    }

    public void OpenDefaultAppsSettings()
    {
        if (!_isSystemRoot)
        {
            throw new InvalidOperationException("An isolated registry service cannot open system settings.");
        }

        Process.Start(new ProcessStartInfo("ms-settings:defaultapps") { UseShellExecute = true });
    }

    private bool HasCompleteRegistration(string path) =>
        IsValidExecutablePath(path)
        && string.Equals(ReadString(ApplicationKey, "Owner"), Owner, StringComparison.Ordinal)
        && string.Equals(ReadString(ProgIdKey, "Owner"), Owner, StringComparison.Ordinal)
        && string.Equals(ReadString(ProgIdKey + @"\shell\open\command", ""), Command(path), StringComparison.Ordinal)
        && string.Equals(ReadString(ProgIdKey + @"\DefaultIcon", ""), Icon(path), StringComparison.Ordinal)
        && string.Equals(ReadString(CapabilitiesKey, "ApplicationName"), ApplicationName, StringComparison.Ordinal)
        && string.Equals(ReadString(CapabilitiesKey, "ApplicationIcon"), Icon(path), StringComparison.Ordinal)
        && string.Equals(ReadString(RegisteredApplicationsKey, ApplicationId), CapabilitiesKey, StringComparison.Ordinal)
        && s_extensions.All(extension =>
        {
            using RegistryKey? key = _registryRoot.OpenSubKey(@"Software\Classes\" + extension + @"\OpenWithProgids");
            return string.Equals(ReadString(CapabilitiesKey + @"\FileAssociations", extension), ProgId, StringComparison.Ordinal)
                && key?.GetValue(ProgId) is byte[] bytes && bytes.Length == 0
                && key.GetValueKind(ProgId) == RegistryValueKind.None;
        });

    private bool HasCoreOwnership(string path)
    {
        if (!IsValidExecutablePath(path)
            || !string.Equals(ReadString(ApplicationKey, "Owner"), Owner, StringComparison.Ordinal))
        {
            return false;
        }

        // Missing entries can result from a failed or interrupted registration and
        // must still be removable. Contradictory owners or commands cannot be claimed.
        return IsMissingOrMatchingString(ProgIdKey, "Owner", Owner)
            && IsMissingOrMatchingString(ProgIdKey + @"\shell\open\command", "", Command(path));
    }

    private bool IsMissingOrMatchingString(string keyPath, string name, string expected)
    {
        using RegistryKey? key = _registryRoot.OpenSubKey(keyPath);
        object? value = key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return value is null || value is string text && string.Equals(text, expected, StringComparison.Ordinal);
    }

    private void EnsurePrivateKeyCanBeUsed(string path)
    {
        using RegistryKey? key = _registryRoot.OpenSubKey(path);
        if (key is not null && !string.Equals(key.GetValue("Owner") as string, Owner, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("An existing registry key does not belong to this application.");
        }
    }

    private string? ReadString(string keyPath, string name)
    {
        using RegistryKey? key = _registryRoot.OpenSubKey(keyPath);
        return key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
    }

    private void SetString(string keyPath, string name, string value)
    {
        using RegistryKey key = _registryRoot.CreateSubKey(keyPath);
        key.SetValue(name, value, RegistryValueKind.String);
    }

    private void DeleteMatchingValue(string keyPath, string name, string expected, bool pruneKey = true)
    {
        using (RegistryKey? key = _registryRoot.OpenSubKey(keyPath, writable: true))
        {
            if (string.Equals(key?.GetValue(name) as string, expected, StringComparison.Ordinal))
            {
                key!.DeleteValue(name, throwOnMissingValue: false);
            }
        }

        if (pruneKey)
        {
            DeleteEmptyKey(keyPath);
        }
    }

    private void DeleteEmptyKey(string keyPath)
    {
        using (RegistryKey? key = _registryRoot.OpenSubKey(keyPath))
        {
            if (key is null || key.ValueCount != 0 || key.SubKeyCount != 0)
            {
                return;
            }
        }

        _registryRoot.DeleteSubKey(keyPath, throwOnMissingSubKey: false);
    }

    private void RemovePrivateOwnerIfEmpty(string keyPath)
    {
        using (RegistryKey? key = _registryRoot.OpenSubKey(keyPath, writable: true))
        {
            // Preserve unknown metadata and our ownership proof together. Otherwise
            // future opt-in registration would mistake this remaining key for a collision.
            if (key?.ValueCount == 1 && key.SubKeyCount == 0
                && string.Equals(key.GetValue("Owner") as string, Owner, StringComparison.Ordinal))
            {
                key.DeleteValue("Owner", throwOnMissingValue: false);
            }
        }

        DeleteEmptyKey(keyPath);
    }

    private void NotifyShell()
    {
        if (_isSystemRoot)
        {
            SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
        }
    }

    private static string Command(string path) => $"\"{path}\" \"%1\"";

    private static string Icon(string path) => $"\"{path}\",0";

    private static bool PathsEqual(string? first, string second) =>
        string.Equals(first, second, StringComparison.OrdinalIgnoreCase);

    private static bool IsValidExecutablePath(string path) =>
        Path.IsPathFullyQualified(path)
        && string.Equals(Path.GetFileName(path), "ModernImageViewer.App.exe", StringComparison.OrdinalIgnoreCase)
        && !path.Contains('"', StringComparison.Ordinal)
        && !path.Contains('\r', StringComparison.Ordinal)
        && !path.Contains('\n', StringComparison.Ordinal);

    private static bool IsPublishedExecutable()
    {
        string? processPath = Environment.ProcessPath;
        if (processPath is null || !IsValidExecutablePath(processPath) || !File.Exists(processPath))
        {
            return false;
        }

        string configPath = Path.ChangeExtension(processPath, ".runtimeconfig.json");
        if (!File.Exists(configPath))
        {
            // Single-file self-contained publishes embed their runtime configuration.
            return string.IsNullOrEmpty(Assembly.GetEntryAssembly()?.Location);
        }

        try
        {
            using JsonDocument config = JsonDocument.Parse(File.ReadAllText(configPath));
            return config.RootElement.TryGetProperty("runtimeOptions", out JsonElement options)
                && options.TryGetProperty("includedFrameworks", out _)
                && !options.TryGetProperty("framework", out _)
                && !options.TryGetProperty("frameworks", out _);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    private static bool HasInstallationMarker(string executablePath)
    {
        string markerPath = Path.Combine(Path.GetDirectoryName(executablePath)!, "ModernImageViewer.install.json");
        try
        {
            using FileStream marker = File.OpenRead(markerPath);
            if (marker.Length is <= 0 or > 4096)
            {
                return false;
            }

            byte[] content = new byte[checked((int)marker.Length)];
            marker.ReadExactly(content);
            using JsonDocument document = JsonDocument.Parse(content);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("distribution", out JsonElement distribution)
                && distribution.ValueKind == JsonValueKind.String
                && string.Equals(distribution.GetString(), "installer", StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    private sealed record RegistrationProfile(
        string ProgId,
        string ApplicationId,
        string ApplicationName,
        string Owner,
        string ApplicationKey,
        bool IsInstalled)
    {
        public static RegistrationProfile Portable { get; } = new(
            "ModernImageViewer.Portable.Image", "ModernImageViewer.Portable", "Modern Image Viewer (Portable)",
            "ModernImageViewer.Portable.v1", @"Software\ModernImageViewer\Portable", false);

        public static RegistrationProfile Installed { get; } = new(
            "ModernImageViewer.Installed.Image", "ModernImageViewer.Installed", "Modern Image Viewer",
            "ModernImageViewer.Installed.v1", @"Software\ModernImageViewer\Installed", true);
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);

    private sealed class RegistrationLease : IDisposable
    {
        private readonly Mutex _mutex;

        public RegistrationLease(string name)
        {
            _mutex = new Mutex(initiallyOwned: false, name);
            try
            {
                if (!_mutex.WaitOne(TimeSpan.FromSeconds(5)))
                {
                    throw new InvalidOperationException("Another copy is updating file associations. Please try again.");
                }
            }
            catch (AbandonedMutexException)
            {
                // The previous process crashed; this thread now owns the mutex.
            }
            catch
            {
                _mutex.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            _mutex.ReleaseMutex();
            _mutex.Dispose();
        }
    }
}
