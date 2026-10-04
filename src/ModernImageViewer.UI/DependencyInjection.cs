using Microsoft.Extensions.DependencyInjection;

using ModernImageViewer.UI.Localization;
using ModernImageViewer.UI.Themes;
using ModernImageViewer.UI.ViewModels;

namespace ModernImageViewer.UI;

public static class DependencyInjection
{
    public static IServiceCollection AddUserInterface(this IServiceCollection services)
    {
        services.AddSingleton<ILocalizationService, LocalizationService>();
        services.AddSingleton<ThemeService>();
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<MainWindow>();
        return services;
    }
}
