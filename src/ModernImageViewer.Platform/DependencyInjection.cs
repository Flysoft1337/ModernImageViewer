using Microsoft.Extensions.DependencyInjection;

using ModernImageViewer.Application.Images;
using ModernImageViewer.Application.Settings;
using ModernImageViewer.Platform.Files;
using ModernImageViewer.Platform.Settings;

namespace ModernImageViewer.Platform;

public static class DependencyInjection
{
    public static IServiceCollection AddPlatform(this IServiceCollection services)
    {
        services.AddSingleton<IUserSettingsService, UserSettingsService>();
        services.AddSingleton<IImageFilePicker, WindowsImageFilePicker>();
        return services;
    }
}
