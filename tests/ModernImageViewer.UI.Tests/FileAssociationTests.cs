using Microsoft.Win32;

using ModernImageViewer.Application.Images;
using ModernImageViewer.Application.Integration;
using ModernImageViewer.Platform.Integration;

namespace ModernImageViewer.UI.Tests;

public sealed class FileAssociationTests
{
    [Fact]
    public void RegistrationPreservesDefaultsAndOnlyCurrentCopyCanRemoveCandidates()
    {
        using IsolatedRegistry registry = new();
        const string firstPath = @"C:\图片 & 相册\ModernImageViewer.App.exe";
        const string movedPath = @"D:\Apps (New)\ModernImageViewer.App.exe";
        using (RegistryKey extension = registry.Root.CreateSubKey(@"Software\Classes\.jpg"))
        {
            extension.SetValue("", "Other.Image");
            using RegistryKey candidates = extension.CreateSubKey("OpenWithProgids");
            candidates.SetValue("Other.Image", Array.Empty<byte>(), RegistryValueKind.None);
        }

        using (RegistryKey choice = registry.Root.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.jpg\UserChoice"))
        {
            choice.SetValue("ProgId", "Other.Image");
            choice.SetValue("Hash", "untouched");
        }

        WindowsFileAssociationService first = new(registry.Root, firstPath, canRegister: true);
        first.Register();
        Assert.Equal(new FileAssociationStatus(true, true, firstPath, true), first.ReadStatus());
        using (RegistryKey? command = registry.Root.OpenSubKey(@"Software\Classes\ModernImageViewer.Portable.Image\shell\open\command"))
        {
            Assert.Equal($"\"{firstPath}\" \"%1\"", command?.GetValue(""));
        }

        WindowsFileAssociationService moved = new(registry.Root, movedPath, canRegister: true);
        moved.Register();
        first.Unregister();
        Assert.Equal(new FileAssociationStatus(true, false, movedPath, true), first.ReadStatus());
        using (RegistryKey metadata = registry.Root.CreateSubKey(@"Software\Classes\ModernImageViewer.Portable.Image\ForeignMetadata"))
        {
            metadata.SetValue("Note", "preserve");
        }
        using (RegistryKey metadata = registry.Root.CreateSubKey(@"Software\ModernImageViewer\Portable\ForeignMetadata"))
        {
            metadata.SetValue("Note", "preserve application metadata");
        }

        using (RegistryKey capabilities = registry.Root.OpenSubKey(@"Software\ModernImageViewer\Portable\Capabilities", writable: true)!)
        {
            capabilities.SetValue("ApplicationDescription", "Custom description to preserve");
        }

        moved.Unregister();
        Assert.False(moved.ReadStatus().IsRegistered);
        using (RegistryKey? capabilities = registry.Root.OpenSubKey(@"Software\ModernImageViewer\Portable\Capabilities"))
        {
            Assert.Equal("Custom description to preserve", capabilities?.GetValue("ApplicationDescription"));
        }
        using RegistryKey? imageExtension = registry.Root.OpenSubKey(@"Software\Classes\.jpg");
        Assert.Equal("Other.Image", imageExtension?.GetValue(""));
        using RegistryKey? remainingCandidates = imageExtension?.OpenSubKey("OpenWithProgids");
        Assert.NotNull(remainingCandidates?.GetValue("Other.Image"));
        Assert.Null(remainingCandidates?.GetValue("ModernImageViewer.Portable.Image"));
        using RegistryKey? userChoice = registry.Root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.jpg\UserChoice");
        Assert.Equal("untouched", userChoice?.GetValue("Hash"));
        Assert.Equal("Other.Image", userChoice?.GetValue("ProgId"));
        using RegistryKey? foreignMetadata = registry.Root.OpenSubKey(@"Software\Classes\ModernImageViewer.Portable.Image\ForeignMetadata");
        Assert.Equal("preserve", foreignMetadata?.GetValue("Note"));
        using RegistryKey? applicationMetadata = registry.Root.OpenSubKey(@"Software\ModernImageViewer\Portable\ForeignMetadata");
        Assert.Equal("preserve application metadata", applicationMetadata?.GetValue("Note"));

        // Unknown private metadata survives cleanup without making subsequent
        // opt-in registration permanently collide with this application's own key.
        moved.Register();
        Assert.True(moved.ReadStatus().IsRegistered);
        using (RegistryKey incomplete = registry.Root.OpenSubKey(@"Software\Classes\.png\OpenWithProgids", writable: true)!)
        {
            incomplete.DeleteValue("ModernImageViewer.Portable.Image");
        }
        using (RegistryKey incomplete = registry.Root.OpenSubKey(@"Software\Classes\ModernImageViewer.Portable.Image\shell\open\command", writable: true)!)
        {
            incomplete.DeleteValue("");
        }
        Assert.False(moved.ReadStatus().IsRegistered);
        Assert.True(moved.ReadStatus().IsOwnedByCurrentExecutable);
        moved.Unregister();
        using RegistryKey? jpegCandidates = registry.Root.OpenSubKey(@"Software\Classes\.jpeg\OpenWithProgids");
        Assert.Null(jpegCandidates?.GetValue("ModernImageViewer.Portable.Image"));
        Assert.Equal("preserve", foreignMetadata?.GetValue("Note"));
        Assert.Equal("preserve application metadata", applicationMetadata?.GetValue("Note"));
    }

    [Fact]
    public void ModifiedCommandAndNonPublishedExecutablesCannotBeClaimedOrDeleted()
    {
        using IsolatedRegistry registry = new();
        const string executablePath = @"C:\Portable\ModernImageViewer.App.exe";
        WindowsFileAssociationService developmentHost = new(registry.Root, executablePath, canRegister: false);
        Assert.False(developmentHost.ReadStatus().CanRegister);
        Assert.Throws<InvalidOperationException>(developmentHost.Register);
        WindowsFileAssociationService dotnetHost = new(registry.Root, @"C:\dotnet\dotnet.exe", canRegister: true);
        Assert.False(dotnetHost.ReadStatus().CanRegister);

        WindowsFileAssociationService portable = new(registry.Root, executablePath, canRegister: true);
        portable.Register();
        using (RegistryKey command = registry.Root.OpenSubKey(@"Software\Classes\ModernImageViewer.Portable.Image\shell\open\command", writable: true)!)
        {
            command.SetValue("", @"""C:\Other\Viewer.exe"" ""%1""");
        }

        Assert.False(portable.ReadStatus().IsOwnedByCurrentExecutable);
        portable.Unregister();
        using RegistryKey? existing = registry.Root.OpenSubKey(@"Software\Classes\ModernImageViewer.Portable.Image\shell\open\command");
        Assert.Equal(@"""C:\Other\Viewer.exe"" ""%1""", existing?.GetValue(""));

        using RegistryKey ownership = registry.Root.OpenSubKey(@"Software\Classes\ModernImageViewer.Portable.Image", writable: true)!;
        ownership.SetValue("Owner", "ForeignOwner");
        Assert.Throws<InvalidOperationException>(portable.Register);
    }

    [Fact]
    public void InstalledAndPortableRegistrationsRemainIndependent()
    {
        using IsolatedRegistry registry = new();
        const string installedPath = @"C:\Users\Viewer\Programs\ModernImageViewer\ModernImageViewer.App.exe";
        const string portablePath = @"D:\便携相册\ModernImageViewer.App.exe";
        WindowsFileAssociationService portable = new(registry.Root, portablePath, canRegister: true);
        WindowsFileAssociationService installed = new(registry.Root, installedPath, canRegister: true, isInstalled: true);
        portable.Register();
        installed.Register();

        Assert.Equal(new FileAssociationStatus(true, true, installedPath, true, true), installed.ReadStatus());
        Assert.Equal(new FileAssociationStatus(true, true, portablePath, true), portable.ReadStatus());
        using (RegistryKey? command = registry.Root.OpenSubKey(@"Software\Classes\ModernImageViewer.Installed.Image\shell\open\command"))
        {
            Assert.Equal($"\"{installedPath}\" \"%1\"", command?.GetValue(""));
        }
        using (RegistryKey? capabilities = registry.Root.OpenSubKey(@"Software\ModernImageViewer\Installed\Capabilities"))
        {
            Assert.Equal("Modern Image Viewer", capabilities?.GetValue("ApplicationName"));
        }
        using (RegistryKey? applications = registry.Root.OpenSubKey(@"Software\RegisteredApplications"))
        {
            Assert.Equal(@"Software\ModernImageViewer\Installed\Capabilities", applications?.GetValue("ModernImageViewer.Installed"));
            Assert.Equal(@"Software\ModernImageViewer\Portable\Capabilities", applications?.GetValue("ModernImageViewer.Portable"));
        }

        installed.Unregister();
        Assert.False(installed.ReadStatus().IsRegistered);
        Assert.True(installed.ReadStatus().IsInstalled);
        Assert.True(portable.ReadStatus().IsRegistered);
        foreach (string extension in SupportedImageFormats.Extensions)
        {
            using RegistryKey? candidates = registry.Root.OpenSubKey(@"Software\Classes\" + extension + @"\OpenWithProgids");
            Assert.Null(candidates?.GetValue("ModernImageViewer.Installed.Image"));
            Assert.NotNull(candidates?.GetValue("ModernImageViewer.Portable.Image"));
        }

        installed.Register();
        portable.Unregister();
        Assert.True(installed.ReadStatus().IsRegistered);
        Assert.False(portable.ReadStatus().IsRegistered);

        // Version 0.2 registered only JPEG and PNG. A newer copy must still be able
        // to remove its entries, including its previous application description.
        using (RegistryKey capabilities = registry.Root.OpenSubKey(@"Software\ModernImageViewer\Installed\Capabilities", writable: true)!)
        {
            capabilities.SetValue("ApplicationDescription", "Browse JPEG and PNG images with Modern Image Viewer.");
            using RegistryKey formats = capabilities.OpenSubKey("FileAssociations", writable: true)!;
            foreach (string extension in SupportedImageFormats.Extensions.Skip(3))
            {
                formats.DeleteValue(extension);
                using RegistryKey candidates = registry.Root.OpenSubKey(@"Software\Classes\" + extension + @"\OpenWithProgids", writable: true)!;
                candidates.DeleteValue("ModernImageViewer.Installed.Image");
            }
        }
        Assert.False(installed.ReadStatus().IsRegistered);
        Assert.True(installed.ReadStatus().IsOwnedByCurrentExecutable);
        installed.Unregister();
        using RegistryKey? remainingApplication = registry.Root.OpenSubKey(@"Software\ModernImageViewer\Installed");
        using RegistryKey? remainingProgId = registry.Root.OpenSubKey(@"Software\Classes\ModernImageViewer.Installed.Image");
        Assert.Null(remainingApplication);
        Assert.Null(remainingProgId);
    }

    private sealed class IsolatedRegistry : IDisposable
    {
        private readonly string _path = @"Software\ModernImageViewer.Tests\" + Guid.NewGuid().ToString("N");

        public IsolatedRegistry() => Root = Registry.CurrentUser.CreateSubKey(_path);

        public RegistryKey Root { get; }

        public void Dispose()
        {
            Root.Dispose();
            Registry.CurrentUser.DeleteSubKeyTree(_path, throwOnMissingSubKey: false);
        }
    }
}
