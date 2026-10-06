using ModernImageViewer.Application.Images;
using ModernImageViewer.Application.Observations;
using ModernImageViewer.Imaging;

namespace ModernImageViewer.Tests;

public sealed class BrowsingFrameObservationTests
{
    [Fact]
    public void LoadingRetainedPixelsCannotCompleteANewRequest()
    {
        using PixelBuffer image = CreatePixels(4, 8);
        ImageOpenState previous = new(ImageOpenStatus.Loaded, image,
            Source: new(Guid.NewGuid(), ImageSourceKind.File), RequestId: 1);
        ImageOpenState loading = previous with { Status = ImageOpenStatus.Loading, RequestId = 2 };
        Assert.False(BrowsingFrameObservation.IsCurrent(loading, previous, 2));
        Assert.False(BrowsingFrameObservation.IsCurrent(loading, loading, 2));
        Assert.False(BrowsingFrameObservation.IsCurrent(previous, previous, 2));
    }

    [Fact]
    public void AFrameMustMatchGenerationIdentityPixelsAndRegion()
    {
        using PixelBuffer image = CreatePixels(4, 8);
        using PixelBuffer other = CreatePixels(4, 8);
        ImageOpenState state = new(ImageOpenStatus.Loaded, image,
            Source: new(Guid.NewGuid(), ImageSourceKind.File), RequestId: 9);
        Assert.True(BrowsingFrameObservation.IsCurrent(state, state with { }, 9));
        Assert.False(BrowsingFrameObservation.IsCurrent(state, state with { RequestId = 8 }, 9));
        Assert.False(BrowsingFrameObservation.IsCurrent(state, state with { Source = new(Guid.NewGuid(), ImageSourceKind.File) }, 9));
        Assert.False(BrowsingFrameObservation.IsCurrent(state, state with { Image = other }, 9));
        Assert.False(BrowsingFrameObservation.IsCurrent(state, state with { Region = new(other, new(0, 0, 4, 4)) }, 9));
    }

    [Fact]
    public void RequiredDetailUsesPhysicalDensityAndIntegerDecoderRounding()
    {
        using PixelBuffer image = CreatePixels(4, 9);
        ImageOpenState state = new(ImageOpenStatus.Loaded, image);
        Assert.True(BrowsingFrameObservation.HasRequiredDetail(state, .5, null));
        Assert.True(BrowsingFrameObservation.HasRequiredDetail(state, 5.0 / 9, null));
        Assert.False(BrowsingFrameObservation.HasRequiredDetail(state, 5.01 / 9, null));
        Assert.False(BrowsingFrameObservation.HasRequiredDetail(state, 1, null));
        Assert.False(BrowsingFrameObservation.HasRequiredDetail(state with { IsRefining = true }, .25, null));
        Assert.False(BrowsingFrameObservation.HasRequiredDetail(state, double.NaN, null));
    }

    [Fact]
    public void RegionDetailMustCoverCurrentVisibleBoundsAndBePaintedAfterLoading()
    {
        using PixelBuffer image = CreatePixels(2, 16);
        using PixelBuffer pixels = CreatePixels(4, 16);
        ImageOpenState state = new(ImageOpenStatus.Loaded, image, Region: new(pixels, new(2, 2, 4, 4)));
        Assert.True(BrowsingFrameObservation.HasRequiredDetail(state, 1, new(3, 3, 2, 2)));
        Assert.False(BrowsingFrameObservation.HasRequiredDetail(state, 1, new(0, 0, 4, 4)));
        Assert.False(BrowsingFrameObservation.HasRequiredDetail(state with { IsRegionLoading = true }, 1, new(3, 3, 2, 2)));
        Assert.False(BrowsingFrameObservation.HasRequiredDetail(state, 1, null));
    }

    private static PixelBuffer CreatePixels(int edge, int sourceEdge) =>
        new(new(edge, edge), edge * 4, new byte[edge * edge * 4], sourceSize: new(sourceEdge, sourceEdge));
}
