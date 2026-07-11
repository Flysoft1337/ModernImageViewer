using Microsoft.Extensions.DependencyInjection;

using ModernImageViewer.Application.Images;
using ModernImageViewer.Codecs.Wic;

namespace ModernImageViewer.Codecs;

public static class DependencyInjection
{
    public static IServiceCollection AddCodecs(this IServiceCollection services)
    {
        services.AddSingleton<IImageDecoder, WicImageDecoder>();
        return services;
    }
}
