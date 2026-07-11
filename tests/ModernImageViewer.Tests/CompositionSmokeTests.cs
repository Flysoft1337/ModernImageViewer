using Microsoft.Extensions.DependencyInjection;

using ModernImageViewer.Application;
using ModernImageViewer.Metadata;

namespace ModernImageViewer.Tests;

public sealed class CompositionSmokeTests
{
    [Fact]
    public void CoreModuleRegistrationsCanBuildServiceProvider()
    {
        ServiceCollection services = new();
        services.AddApplication().AddMetadata();

        using ServiceProvider provider = services.BuildServiceProvider(
            new ServiceProviderOptions
            {
                ValidateOnBuild = false,
                ValidateScopes = true,
            });

        Assert.NotNull(provider);
    }
}
