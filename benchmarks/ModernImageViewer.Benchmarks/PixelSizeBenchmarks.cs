using BenchmarkDotNet.Attributes;

using ModernImageViewer.Imaging;

namespace ModernImageViewer.Benchmarks;

[MemoryDiagnoser]
public class PixelSizeBenchmarks
{
    private readonly PixelSize _size = new(7680, 4320);

    [Benchmark]
    public long CalculatePixelCount()
    {
        return _size.PixelCount;
    }
}
