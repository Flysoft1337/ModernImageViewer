using System.IO;
using System.Text;

using ModernImageViewer.Application.Images;
using ModernImageViewer.Codecs.Svg;
using ModernImageViewer.Imaging;

namespace ModernImageViewer.UI.Tests;

public sealed class SvgDecoderTests
{
    [Fact]
    public void LargeVectorRendersDirectlyToBoundedPremultipliedPixels()
    {
        const string markup = "<svg xmlns='http://www.w3.org/2000/svg' width='12000' height='8000'><rect width='12000' height='8000' fill='red' opacity='.5'/></svg>";
        using PixelBuffer image = Decode(markup, new PixelSize(96, 64));
        Assert.Equal(new PixelSize(12_000, 8_000), image.SourceSize);
        Assert.Equal(new PixelSize(96, 64), image.Size);
        Assert.Equal(96 * 64 * 4, image.Pixels.Length);
        Assert.Equal(new byte[] { 0, 0, 128, 128 }, image.Pixels.Span[..4].ToArray());
    }

    [Fact]
    public void PathsTransformsGradientsAndInlineStyleRender()
    {
        const string markup = "<svg xmlns='http://www.w3.org/2000/svg' width='120' height='80' viewBox='-10 -10 120 80'><defs><linearGradient id='g'><stop offset='0' stop-color='red'/><stop offset='1' stop-color='blue'/></linearGradient></defs><g transform='translate(10,10)'><path d='M 0 0 L 80 0 L 80 40 L 0 40 Z' style='fill:url(#g);stroke:white;stroke-width:1'/></g></svg>";
        using PixelBuffer image = Decode(markup);
        int offset = (30 * image.Stride) + (50 * 4);
        Assert.InRange(image.Pixels.Span[offset], (byte)90, (byte)105);
        Assert.InRange(image.Pixels.Span[offset + 2], (byte)150, (byte)165);
        Assert.Equal((byte)255, image.Pixels.Span[offset + 3]);
        Assert.Equal((byte)0, image.Pixels.Span[3]);
    }

    [Theory]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg'><rect width='10' height='10'/></svg>", 300, 150)]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 80 40'><rect width='80' height='40'/></svg>", 300, 150)]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg' width='120' height='60'><rect width='120' height='60'/></svg>", 120, 60)]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg' width='1in' height='25.4mm'><rect width='96' height='96'/></svg>", 96, 96)]
    public void RootDimensionsAndDefaultViewportAreDeterministic(string markup, int width, int height)
    {
        using PixelBuffer image = Decode(markup);
        Assert.Equal(new PixelSize(width, height), image.SourceSize);
    }

    [Fact]
    public void OutputBudgetsSourceLimitsAndCancellationAreEnforced()
    {
        const string markup = "<svg xmlns='http://www.w3.org/2000/svg' width='120' height='80'><rect width='120' height='80'/></svg>";
        using MemoryStream budgetStream = CreateStream(markup);
        Assert.Throws<ImageSizeLimitExceededException>(() => RestrictedSvgImageDecoder.Decode(budgetStream, null, 4, TestContext.Current.CancellationToken));
        Assert.Throws<ImageSizeLimitExceededException>(() => Decode("<svg xmlns='http://www.w3.org/2000/svg' width='40000' height='10'/>"));
        Assert.Throws<ImageSizeLimitExceededException>(() => Decode("<svg xmlns='http://www.w3.org/2000/svg'>" + new string(' ', 1_048_576) + "</svg>"));
        Assert.Throws<ImageSizeLimitExceededException>(() => Decode("<svg xmlns='http://www.w3.org/2000/svg'>" + string.Concat(Enumerable.Repeat("<g>", 34)) + string.Concat(Enumerable.Repeat("</g>", 34)) + "</svg>"));
        Assert.Throws<ImageSizeLimitExceededException>(() => Decode("<svg xmlns='http://www.w3.org/2000/svg'>" + string.Concat(Enumerable.Repeat("<rect width='1' height='1'/>", 2_048)) + "</svg>"));
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();
        using MemoryStream cancelStream = CreateStream(markup);
        Assert.Throws<OperationCanceledException>(() => RestrictedSvgImageDecoder.Decode(cancelStream, null, null, cancelled.Token));
    }

    [Theory]
    [InlineData("<g opacity='.5'><rect width='120' height='80' fill='red'/></g>", 128)]
    [InlineData("<g opacity='.5'><rect width='120' height='80' fill='red' opacity='.5'/></g>", 64)]
    [InlineData("<g opacity='.1' style='opacity:.5'><rect width='120' height='80' fill='red' style='opacity:.5'/></g>", 64)]
    public void OneAndTwoOpacityLayersPreservePremultipliedComposition(string child, byte alpha)
    {
        using PixelBuffer image = Decode("<svg xmlns='http://www.w3.org/2000/svg' width='120' height='80'>" + child + "</svg>");
        Assert.Equal(new byte[] { 0, 0, alpha, alpha }, image.Pixels.Span[..4].ToArray());
    }

    [Theory]
    [InlineData("<g opacity='.9'><g opacity='.9'><rect width='120' height='80' opacity='.9'/></g></g>")]
    [InlineData("<g style='opacity:.9'><g style='opacity:.9'><rect width='120' height='80' style='opacity:.9'/></g></g>")]
    [InlineData("<g opacity='1' style='opacity:.9'><g opacity='.9'><rect width='120' height='80' opacity='.9'/></g></g>")]
    public void ThirdActiveOpacityLayerIsRejectedEvenForShapesAndInlineStyles(string child)
    {
        Assert.Throws<ImageSizeLimitExceededException>(() => Decode(
            "<svg xmlns='http://www.w3.org/2000/svg' width='120' height='80'>" + child + "</svg>"));
    }

    [Fact]
    public void DeepOverlappingOpacityGroupsAreRejectedBeforeNativePictureCreation()
    {
        string markup = "<svg xmlns='http://www.w3.org/2000/svg' width='2560' height='1600'>"
            + string.Concat(Enumerable.Repeat("<g opacity='.99'>", 31))
            + "<rect width='2560' height='1600' fill='red'/><rect width='2560' height='1600' fill='blue'/>"
            + string.Concat(Enumerable.Repeat("</g>", 31)) + "</svg>";
        Assert.Throws<ImageSizeLimitExceededException>(() => Decode(markup, new PixelSize(2560, 1600)));
    }

    [Fact]
    public void LayerBudgetUsesTargetOutputRatherThanVectorDimensions()
    {
        const string markup = "<svg xmlns='http://www.w3.org/2000/svg' width='3000' height='2000'><g opacity='.5'><rect width='3000' height='2000' fill='red' opacity='.5'/></g></svg>";
        // Two full-size opacity layers would need 48,000,000 extra bytes despite a permitted output buffer.
        Assert.Throws<ImageSizeLimitExceededException>(() => Decode(markup));
        using PixelBuffer preview = Decode(markup, new PixelSize(96, 64));
        Assert.Equal(new PixelSize(3000, 2000), preview.SourceSize);
        Assert.Equal(new byte[] { 0, 0, 64, 64 }, preview.Pixels.Span[..4].ToArray());
    }

    [Fact]
    public void StyleOverrideAndSiblingLayersDoNotAccumulateDepth()
    {
        const string markup = "<svg xmlns='http://www.w3.org/2000/svg' width='120' height='80'><g opacity='.5' style='opacity:1'><g opacity='.5'><rect width='120' height='80' fill='red' opacity='.5'/></g></g><rect width='10' height='10' opacity='.5'/></svg>";
        using PixelBuffer image = Decode(markup);
        int offset = (40 * image.Stride) + (60 * 4);
        Assert.Equal(new byte[] { 0, 0, 64, 64 }, image.Pixels.Slice(offset, 4).ToArray());
    }

    [Fact]
    public void RootOpacityAndPercentagesAreIncludedInLayerAccounting()
    {
        const string markup = "<svg xmlns='http://www.w3.org/2000/svg' width='120' height='80' opacity='50%'><g style='opacity:50%'><rect width='120' height='80' fill='red'/></g></svg>";
        using PixelBuffer image = Decode(markup);
        Assert.Equal(new byte[] { 0, 0, 64, 64 }, image.Pixels.Span[..4].ToArray());
        Assert.Throws<ImageSizeLimitExceededException>(() => Decode(markup.Replace("fill='red'", "fill='red' opacity='.5'", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("<rect width='NaN' height='10'/>")]
    [InlineData("<rect width='Infinity' height='10'/>")]
    [InlineData("<rect width='10foo' height='10'/>")]
    [InlineData("<rect width='10' height='10' x='Inf'/>")]
    [InlineData("<polyline points='0 0 10'/>")]
    [InlineData("<polyline points='0 0 10foo 10'/>")]
    [InlineData("<path d='M 0 0 L 10'/>")]
    [InlineData("<path d='M 0 0 L 10 10 BAD'/>")]
    [InlineData("<path d='L 0 0'/>")]
    [InlineData("<path d='M 0 0 C 1 2 3 4 5'/>")]
    [InlineData("<path d='M 0 0 A 5 5 0 2 0 10 10'/>")]
    [InlineData("<path d='M 0 0 A 5 5 0 0110 10'/>")]
    [InlineData("<rect width='10' height='10' transform='translate(1,2) garbage'/>")]
    [InlineData("<rect width='10' height='10' transform='matrix(1 0 0 1 0)'/>")]
    [InlineData("<rect width='10' height='10' transform='rotate(1 2)'/>")]
    [InlineData("<rect width='10' height='10' transform='translate(NaN)'/>")]
    [InlineData("<rect width='10' height='10' transform='scale(1),'/>")]
    public void MalformedGeometryIsRejectedInsteadOfPartialRendering(string child)
    {
        ImageDecodeException exception = Assert.Throws<ImageDecodeException>(() => Decode(
            "<svg xmlns='http://www.w3.org/2000/svg' width='120' height='80'>" + child + "</svg>"));
        Assert.Equal(ImageOpenError.CorruptFile, exception.Error);
    }

    [Theory]
    [InlineData("width='10foo' height='80'")]
    [InlineData("width='NaN' height='80'")]
    [InlineData("viewBox='0 0 120'")]
    [InlineData("viewBox='0 0 120 80 trailing'")]
    public void MalformedRootViewportNeverSilentlyFallsBack(string attributes)
    {
        ImageDecodeException exception = Assert.Throws<ImageDecodeException>(() => Decode(
            "<svg xmlns='http://www.w3.org/2000/svg' " + attributes + "><rect width='10' height='10'/></svg>"));
        Assert.Equal(ImageOpenError.CorruptFile, exception.Error);
    }

    [Fact]
    public void RelativePathsExponentsAndStandardTransformParameterCountsRender()
    {
        const string markup = "<svg xmlns='http://www.w3.org/2000/svg' width='1.2e2px' height='8e1px' viewBox='-1e1 -1e1 1.2e2 8e1'><g transform='translate(10 -2) scale(1,1), rotate(0 0 0) skewX(0) skewY(0) matrix(1 0 0 1 0 0)'><path d='m1e1 1e1 l4e1-0 h-1e1 v2e1 t-1e1 1e1 q-1e1 0 -1e1-1e1 c-1 0-2 0-3 0 s-2 0-3 0 a5 5 0 0 1-5-5 z' fill='red'/><polyline points='0,0 1e1,-1e1 2e1,0' fill='none' stroke='blue'/></g></svg>";
        using PixelBuffer image = Decode(markup);
        Assert.Equal(new PixelSize(120, 80), image.SourceSize);
        Assert.Contains(image.Pixels.ToArray(), value => value != 0);
    }

    [Fact]
    public void PathCommandsWithoutNumbersAreStillChargedToComplexityLimit()
    {
        string markup = "<svg xmlns='http://www.w3.org/2000/svg' width='120' height='80'><path d='M0 0"
            + new string('z', 65_536) + "'/></svg>";
        Assert.Throws<ImageSizeLimitExceededException>(() => Decode(markup));
    }

    [Theory]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("<foreignObject width='10' height='10'/>")]
    [InlineData("<image href='https://example.invalid/a.png'/>")]
    [InlineData("<image href='data:image/png;base64,AAAA'/>")]
    [InlineData("<use href='#loop' id='loop'/>")]
    [InlineData("<clipPath id='loop'><rect width='10' height='10' clip-path='url(#loop)'/></clipPath>")]
    [InlineData("<filter id='f'/>")]
    [InlineData("<mask id='m'/>")]
    [InlineData("<style>@import 'https://example.invalid/a.css';</style>")]
    [InlineData("<style>@font-face { src: url(file:///font.ttf); }</style>")]
    [InlineData("<rect width='10' height='10' onload='alert(1)'/>")]
    [InlineData("<rect width='10' height='10' style='fill:u\\72l(https://example.invalid/a)'/>")]
    [InlineData("<rect width='10' height='10' style='fill:URL (https://example.invalid/a)'/>")]
    [InlineData("<rect width='10' height='10' fill='url(file:///a.svg#g)'/>")]
    [InlineData("<linearGradient id='g' href='#g'/><rect width='10' height='10' fill='url(#g)'/>")]
    [InlineData("<rect id='r' width='10' height='10' fill='url(#r)'/>")]
    [InlineData("<linearGradient id='g' fill='url(#g)'/><rect width='10' height='10' fill='url(#g)'/>")]
    [InlineData("<rect width='10' height='10' fill='url(#missing)'/>")]
    [InlineData("<text x='0' y='10'>font loading excluded</text>")]
    public void ActiveResourcesAndUnsupportedRecursiveFeaturesAreRejectedBeforeRendering(string child)
    {
        ImageDecodeException exception = Assert.Throws<ImageDecodeException>(() => Decode("<svg xmlns='http://www.w3.org/2000/svg' width='120' height='80'>" + child + "</svg>"));
        Assert.Equal(ImageOpenError.UnsupportedFormat, exception.Error);
    }

    [Theory]
    [InlineData("<!DOCTYPE svg [<!ENTITY e SYSTEM 'file:///private.txt'>]><svg xmlns='http://www.w3.org/2000/svg'><title>&e;</title></svg>")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg'><rect></svg>")]
    [InlineData("")]
    public void MalformedXmlAndDtdAreReportedAsCorrupt(string markup)
    {
        ImageDecodeException exception = Assert.Throws<ImageDecodeException>(() => Decode(markup));
        Assert.Equal(ImageOpenError.CorruptFile, exception.Error);
    }

    private static PixelBuffer Decode(string markup, PixelSize? maximumSize = null)
    {
        using MemoryStream stream = CreateStream(markup);
        return RestrictedSvgImageDecoder.Decode(stream, maximumSize, null, TestContext.Current.CancellationToken);
    }

    private static MemoryStream CreateStream(string markup) => new(Encoding.UTF8.GetBytes(markup));
}
