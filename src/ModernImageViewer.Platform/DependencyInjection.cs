using Microsoft.Extensions.DependencyInjection;

using ModernImageViewer.Application.Images;
using ModernImageViewer.Application.Integration;
using ModernImageViewer.Application.Settings;
using ModernImageViewer.Platform.Files;
using ModernImageViewer.Platform.Integration;
using ModernImageViewer.Platform.Settings;

namespace ModernImageViewer.Platform;

public static class DependencyInjection
{
    public static IServiceCollection AddPlatform(this IServiceCollection services)
    {
        services.AddSingleton<IUserSettingsService, UserSettingsService>();
        services.AddSingleton<IImageFilePicker, WindowsImageFilePicker>();
        services.AddSingleton<IClipboardFileService, WindowsClipboardFileService>();
        services.AddSingleton<IFileRevealService, WindowsFileRevealService>();
        services.AddSingleton<Func<IFileRevealService>>(provider => provider.GetRequiredService<IFileRevealService>);
        services.AddSingleton<IFileAssociationService, WindowsFileAssociationService>();
        services.AddSingleton<Func<IFileAssociationService>>(provider => provider.GetRequiredService<IFileAssociationService>);
        return services;
    }
}
