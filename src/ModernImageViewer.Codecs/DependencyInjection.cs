using Microsoft.Extensions.DependencyInjection;

using ModernImageViewer.Application.Images;

namespace ModernImageViewer.Codecs;

public static class DependencyInjection
{
    public static IServiceCollection AddCodecs(this IServiceCollection services)
    {
        services.AddSingleton<ImageDecoder>();
        services.AddSingleton<IImageDecoder>(provider => provider.GetRequiredService<ImageDecoder>());
        services.AddSingleton<IThumbnailDecoder>(provider => provider.GetRequiredService<ImageDecoder>());
        return services;
    }
}
