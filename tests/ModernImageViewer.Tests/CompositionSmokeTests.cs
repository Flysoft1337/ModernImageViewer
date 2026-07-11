using Microsoft.Extensions.DependencyInjection;

using ModernImageViewer.Application;
using ModernImageViewer.Codecs;
using ModernImageViewer.Metadata;
using ModernImageViewer.Platform;

namespace ModernImageViewer.Tests;

public sealed class CompositionSmokeTests
{
    [Fact]
    public void ModuleRegistrationsCanBuildServiceProvider()
    {
        ServiceCollection services = new();

        services
            .AddApplication()
            .AddCodecs()
            .AddMetadata()
            .AddPlatform();

        using ServiceProvider provider = services.BuildServiceProvider(
            new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true,
            });

        Assert.NotNull(provider);
    }
}
