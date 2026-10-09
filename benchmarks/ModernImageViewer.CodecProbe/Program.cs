using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;

using ModernImageViewer.Application.Images;
using ModernImageViewer.Codecs;
using ModernImageViewer.Imaging;

namespace ModernImageViewer.CodecProbe;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "generate-png")
        {
            GeneratePng(args[1]);
            Console.WriteLine("Generated streaming 10000 x 10000 RGB PNG.");
            return 0;
        }
        if (args.Length == 2 && args[0] == "browse-detail")
        {
            return await ObserveDetailBrowsingAsync(args[1]);
        }
        if (args.Length != 3 || args[0] != "decode" || args[2] is not ("preview" or "thumbnail"))
        {
            Console.Error.WriteLine("Usage: generate-png <new-path> | decode <path> <preview|thumbnail> | browse-detail <path>");
            return 2;
        }
        ImageDecoder decoder = new();
        PixelSize bound = args[2] == "preview" ? new PixelSize(2560, 1600) : new PixelSize(224, 140);
        using Process process = Process.GetCurrentProcess();
        process.Refresh();
        long beforePrivateBytes = process.PrivateMemorySize64;
        Stopwatch watch = Stopwatch.StartNew();
        using PixelBuffer pixels = args[2] == "preview"
            ? await decoder.DecodePreviewAsync(args[1], bound, CancellationToken.None)
            : await decoder.DecodeThumbnailAsync(args[1], bound, CancellationToken.None);
        watch.Stop();
        process.Refresh();
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            Mode = args[2],
            SourceWidth = pixels.SourceSize.Width,
            SourceHeight = pixels.SourceSize.Height,
            OutputWidth = pixels.Size.Width,
            OutputHeight = pixels.Size.Height,
            OutputBytes = pixels.Pixels.Length,
            BoundWidth = bound.Width,
            BoundHeight = bound.Height,
            ElapsedMs = Math.Round(watch.Elapsed.TotalMilliseconds, 2),
            BeforePrivateBytes = beforePrivateBytes,
            WorkingSetBytes = process.WorkingSet64,
            PrivateBytes = process.PrivateMemorySize64,
            PeakWorkingSetBytes = process.PeakWorkingSet64,
            PeakPagedMemoryBytes = process.PeakPagedMemorySize64,
            ManagedHeapBytes = GC.GetTotalMemory(forceFullCollection: false),
            OS = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            Runtime = RuntimeInformation.FrameworkDescription,
            ProcessorCount = Environment.ProcessorCount,
            Note = "One fresh framework-dependent Release decode process; sampled while output pixels remain retained. "
                + "Elapsed includes codec initialization; process lifetime peaks include runtime startup. "
                + "No WPF canvas/surface/cache. Process counters are not native-only allocations or a whole-pipeline cap; not P95.",
        }));
        return 0;
    }

    private static async Task<int> ObserveDetailBrowsingAsync(string path)
    {
        PixelBuffer.ObserveLifetime = true;
        CountingDecoder decoder = new();
        using ImageOpenCoordinator coordinator = new(new NoPicker(), decoder);
        Stopwatch watch = Stopwatch.StartNew();
        if (!await coordinator.OpenAsync(path)) { throw new InvalidOperationException("Preview failed."); }
        double previewMs = watch.Elapsed.TotalMilliseconds;
        PixelBuffer preview = coordinator.State.Image!;
        PixelSize source = preview.SourceSize;
        long previewBytes = preview.Pixels.Length;
        watch.Restart();
        bool refined = await coordinator.RefineAsync();
        double detailMs = watch.Elapsed.TotalMilliseconds;
        PixelBuffer displayed = coordinator.State.Image!;
        for (int i = 0; i < 20; i++)
        {
            await coordinator.RefineAsync();
            if (!ReferenceEquals(displayed, coordinator.State.Image))
            {
                throw new InvalidOperationException("Repeated detail replaced retained pixels.");
            }
        }
        long retainedBytes = PixelBuffer.ObservedActiveBytes;
        using Process process = Process.GetCurrentProcess();
        process.Refresh();
        long workingSet = process.WorkingSet64;
        long privateBytes = process.PrivateMemorySize64;
        long peakWorkingSet = process.PeakWorkingSet64;
        await coordinator.CloseImageAsync();
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            Mode = "browse-detail",
            SourceWidth = source.Width,
            SourceHeight = source.Height,
            PreviewBytes = previewBytes,
            FullDetailReady = refined && displayed.Size == source,
            DetailBudgetBytes = decoder.DetailBudget,
            decoder.PreviewCalls,
            decoder.DetailCalls,
            decoder.RegionCalls,
            RepeatedRequests = 20,
            PreviewMs = Math.Round(previewMs, 2),
            DetailMs = Math.Round(detailMs, 2),
            RetainedPixelBytes = retainedBytes,
            AfterClosePixelBytes = PixelBuffer.ObservedActiveBytes,
            AfterClosePixelCount = PixelBuffer.ObservedActiveCount,
            WorkingSetBytes = workingSet,
            PrivateBytes = privateBytes,
            PeakWorkingSetBytes = peakWorkingSet,
            Note = "Read-only static coordinator/codec observation, no WPF canvas or mouse interaction; "
                + "no forced GC, no path in report, not a whole-process memory cap or P95.",
        }));
        return refined && decoder.DetailCalls == 1 && PixelBuffer.ObservedActiveCount == 0 ? 0 : 1;
    }

    private sealed class NoPicker : IImageFilePicker
    {
        public Task<string?> PickImageAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }

    private sealed class CountingDecoder : IPreviewImageDecoder, IRegionImageDecoder
    {
        private readonly ImageDecoder _inner = new();
        public int PreviewCalls { get; private set; }
        public int DetailCalls { get; private set; }
        public int RegionCalls { get; private set; }
        public long DetailBudget { get; private set; }
        public Task<PixelBuffer> DecodeAsync(string path, CancellationToken cancellationToken) => _inner.DecodeAsync(path, cancellationToken);
        public Task<PixelBuffer> DecodePreviewAsync(string path, PixelSize maximumSize, CancellationToken cancellationToken)
        {
            PreviewCalls++;
            return _inner.DecodePreviewAsync(path, maximumSize, cancellationToken);
        }
        public Task<PixelBuffer> DecodeDetailAsync(string path, long maximumDecodedBytes, CancellationToken cancellationToken)
        {
            DetailCalls++;
            DetailBudget = maximumDecodedBytes;
            return _inner.DecodeDetailAsync(path, maximumDecodedBytes, cancellationToken);
        }
        public Task<DecodedImageRegion> DecodeRegionAsync(string path, PixelRect region, PixelSize expectedSourceSize,
            long maximumDecodedBytes, CancellationToken cancellationToken)
        {
            RegionCalls++;
            return _inner.DecodeRegionAsync(path, region, expectedSourceSize, maximumDecodedBytes, cancellationToken);
        }
    }

    private static void GeneratePng(string path)
    {
        const int width = 10000;
        const int height = 10000;
        string temporary = path + $".{Guid.NewGuid():N}.zlib.tmp";
        bool ownsTemporary = false;
        bool ownsOutput = false;
        bool completed = false;
        try
        {
            // A filter byte plus one RGB row, rather than a 400 MB BGRA fixture.
            using (FileStream compressed = new(temporary, FileMode.CreateNew, FileAccess.Write))
            using (ZLibStream zlib = new(compressed, CompressionLevel.Fastest))
            {
                ownsTemporary = true;
                byte[] row = new byte[(width * 3) + 1];
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        int offset = (x * 3) + 1;
                        row[offset] = (byte)(x / 40);
                        row[offset + 1] = (byte)(y / 40);
                        row[offset + 2] = (byte)((x / 200) ^ (y / 200));
                    }
                    zlib.Write(row);
                }
            }
            using FileStream output = new(path, FileMode.CreateNew, FileAccess.Write);
            ownsOutput = true;
            output.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
            byte[] header = new byte[13];
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0, 4), width);
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4, 4), height);
            header[8] = 8;
            header[9] = 2; // RGB, 8-bit, non-interlaced
            WriteChunk(output, "IHDR"u8, header);
            using FileStream input = File.OpenRead(temporary);
            byte[] chunk = new byte[64 * 1024];
            int length;
            while ((length = input.Read(chunk)) != 0)
            {
                WriteChunk(output, "IDAT"u8, chunk.AsSpan(0, length));
            }
            WriteChunk(output, "IEND"u8, ReadOnlySpan<byte>.Empty);
            completed = true;
        }
        finally
        {
            if (ownsTemporary)
            {
                File.Delete(temporary);
            }
            if (ownsOutput && !completed)
            {
                File.Delete(path);
            }
        }
    }

    private static void WriteChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> payload)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, payload.Length);
        output.Write(number);
        output.Write(type);
        output.Write(payload);
        uint crc = uint.MaxValue;
        foreach (byte value in type)
        {
            crc = UpdateCrc(crc, value);
        }
        foreach (byte value in payload)
        {
            crc = UpdateCrc(crc, value);
        }
        BinaryPrimitives.WriteUInt32BigEndian(number, ~crc);
        output.Write(number);
    }

    private static uint UpdateCrc(uint crc, byte value)
    {
        crc ^= value;
        for (int bit = 0; bit < 8; bit++)
        {
            crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xEDB88320);
        }
        return crc;
    }
}
