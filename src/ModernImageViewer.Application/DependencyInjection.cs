using Microsoft.Extensions.DependencyInjection;

using ModernImageViewer.Application.Images;

namespace ModernImageViewer.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddSingleton<ImageOpenCoordinator>();
        return services;
    }
}
