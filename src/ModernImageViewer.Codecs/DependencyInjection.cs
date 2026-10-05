using Microsoft.Extensions.DependencyInjection;

using ModernImageViewer.Application.Editing;
using ModernImageViewer.Application.Images;
using ModernImageViewer.Codecs.Editing;

namespace ModernImageViewer.Codecs;

public static class DependencyInjection
{
    public static IServiceCollection AddCodecs(this IServiceCollection services)
    {
        services.AddSingleton<ImageDecoder>();
        services.AddSingleton<IImageExportService, ImageExportService>();
        services.AddSingleton<IImageDecoder>(provider => provider.GetRequiredService<ImageDecoder>());
        services.AddSingleton<IThumbnailDecoder>(provider => provider.GetRequiredService<ImageDecoder>());
        services.AddSingleton<IRegionImageDecoder>(provider => provider.GetRequiredService<ImageDecoder>());
        services.AddSingleton<IPrefetchImageDecoder>(provider => provider.GetRequiredService<ImageDecoder>());
        return services;
    }
}
