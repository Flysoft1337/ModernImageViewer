using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

using ModernImageViewer.Application.Images;
using ModernImageViewer.Application.Integration;
using ModernImageViewer.Imaging;
using ModernImageViewer.Platform.Integration;

namespace ModernImageViewer.UI.Tests;

public sealed class ImageClipboardServiceTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public async Task IdentityCopyUsesPaddedSourceStrideAndKeepsAnIndependentNativeSnapshot(int offset)
    {
        await OnStaThread(async () =>
        {
            byte[] source = new byte[offset + 24];
            byte[] expected = [1, 2, 3, 255, 64, 32, 16, 128, 4, 5, 6, 255, 7, 8, 9, 255];
            expected.AsSpan(0, 8).CopyTo(source.AsSpan(offset));
            expected.AsSpan(8, 8).CopyTo(source.AsSpan(offset + 12));
            byte[] original = (byte[])source.Clone();
            IDataObject? written = null;
            WindowsImageClipboardService service = new(() => written, data => written = data);
            await service.WriteAsync(new(new(2, 2), 12, source.AsMemory(offset, 24), default), TestContext.Current.CancellationToken);
            Assert.Equal(original, source);
            Array.Fill(source, (byte)99);
            using PixelBuffer output = await service.ReadInput().Image!.ReadPixelsAsync(new(2, 2), 16, TestContext.Current.CancellationToken);
            Assert.Equal(expected, output.Pixels.ToArray());
            BitmapSource bitmap = Assert.IsAssignableFrom<BitmapSource>(written!.GetData(DataFormats.Bitmap, autoConvert: false));
            byte[] native = new byte[16];
            bitmap.CopyPixels(native, 8, 0);
            Assert.Equal(expected, native);
        });
    }

    [Fact]
    public void EncodedStreamCapsCapacityWhenGrowingAnIrregularBuffer()
    {
        using WindowsImageClipboardService.LimitedPngStream stream = new(TestContext.Current.CancellationToken);
        stream.SetLength(17L * 1024 * 1024);
        stream.Position = stream.Length;
        stream.WriteByte(1);
        Assert.Equal(ClipboardImageLimits.EncodedBytes, stream.Capacity);
        Assert.Equal((17L * 1024 * 1024) + 1, stream.Length);
        Assert.Equal(ImageOpenError.ImageTooLarge, Assert.Throws<ImageDecodeException>(() =>
            stream.SetLength(ClipboardImageLimits.EncodedBytes + 1)).Error);
        Assert.Equal(ClipboardImageLimits.EncodedBytes, stream.Capacity);
    }

    [Fact]
    public void TwoByTwoDownsampleBlendsPremultipliedColorAndAlphaInOneDestination()
    {
        byte[] pixels = [0, 0, 0, 0, 128, 0, 0, 128, 0, 255, 0, 255, 0, 0, 255, 255];
        byte[] original = (byte[])pixels.Clone();
        byte[] output = WindowsImageClipboardService.CreateOutputPixels(
            new(new(2, 2), 8, pixels, default), new(1, 1), TestContext.Current.CancellationToken);
        Assert.Equal(new byte[] { 32, 64, 64, 160 }, output);
        Assert.Equal(original, pixels);
    }

    [Fact]
    public async Task TransparentPngRoundTripsAndWinsOverBitmapOnCallerSta()
    {
        await OnStaThread(async () =>
        {
            int caller = Environment.CurrentManagedThreadId;
            DataObject? written = null;
            int reads = 0;
            WindowsImageClipboardService service = new(() => { reads++; return written; }, data =>
            {
                Assert.Equal(caller, Environment.CurrentManagedThreadId);
                Assert.Equal(ApartmentState.STA, Thread.CurrentThread.GetApartmentState());
                written = Snapshot(data);
            });
            byte[] pixels = [0, 0, 0, 0, 64, 32, 16, 128, 3, 2, 1, 255];
            await service.WriteAsync(new(new(3, 1), 12, pixels, default));
            Assert.NotNull(written);
            // An incompatible bitmap must not be requested when a PNG exists.
            written.SetData(DataFormats.Bitmap, "invalid bitmap", autoConvert: false);
            ImageClipboardInput input = service.ReadInput();
            Assert.Empty(input.Files);
            Assert.Equal(1, reads);
            Assert.Equal(new PixelSize(3, 1), input.Image!.SourceSize);
            using PixelBuffer output = await input.Image.ReadPixelsAsync(new(3, 1), 12, TestContext.Current.CancellationToken);
            Assert.Equal(pixels, output.Pixels.ToArray());
        });
    }

    [Fact]
    public async Task WriterCanRetainPngStreamAndCompatibilityBitmapPreservesPremultipliedAlpha()
    {
        await OnStaThread(async () =>
        {
            IDataObject? written = null;
            WindowsImageClipboardService service = new(() => written, data => written = data);
            byte[] pixels = [0, 0, 0, 0, 64, 32, 16, 128];
            await service.WriteAsync(new(new(2, 1), 8, pixels, default), TestContext.Current.CancellationToken);
            Assert.NotNull(written);
            Stream png = Assert.IsAssignableFrom<Stream>(written.GetData("PNG", autoConvert: false));
            Assert.True(png.CanRead);
            Assert.Equal(0, png.Position);
            BitmapSource bitmap = Assert.IsAssignableFrom<BitmapSource>(written.GetData(DataFormats.Bitmap, autoConvert: false));
            Assert.Equal(PixelFormats.Pbgra32, bitmap.Format);
            byte[] compatibilityPixels = new byte[8];
            bitmap.CopyPixels(compatibilityPixels, 8, 0);
            Assert.Equal(pixels, compatibilityPixels);
            using PixelBuffer input = await service.ReadInput().Image!.ReadPixelsAsync(new(2, 1), 8, TestContext.Current.CancellationToken);
            Assert.Equal(pixels, input.Pixels.ToArray());
        });
    }

    [Fact]
    public async Task NonFreezableBitmapIsRejectedAndMtaReaderUsesOneStaSnapshot()
    {
        await OnStaThread(async () =>
        {
            DataObject? written = null;
            WindowsImageClipboardService service = new(() => written, data => written = Snapshot(data));
            await service.WriteAsync(new(new(1, 1), 4, new byte[4], default), TestContext.Current.CancellationToken);
            BitmapImage bitmap = new();
            bitmap.BeginInit();
            bitmap.StreamSource = Assert.IsAssignableFrom<Stream>(written!.GetData("PNG", autoConvert: false));
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            BindingOperations.SetBinding(bitmap, BitmapImage.DecodePixelWidthProperty, new Binding { Source = 1 });
            Assert.False(bitmap.CanFreeze);
            DataObject invalid = new();
            invalid.SetData(DataFormats.Bitmap, bitmap, autoConvert: false);
            Assert.Equal(ImageOpenError.CorruptFile, Assert.Throws<ImageDecodeException>(() =>
                new WindowsImageClipboardService(() => invalid, _ => Assert.Fail()).ReadInput()).Error);
        });
        int reads = 0;
        WindowsImageClipboardService mtaService = new(() =>
        {
            Assert.Equal(ApartmentState.STA, Thread.CurrentThread.GetApartmentState());
            reads++;
            return new DataObject();
        }, _ => Assert.Fail());
        ImageClipboardInput empty = await Task.Run(mtaService.ReadInput, TestContext.Current.CancellationToken);
        Assert.Empty(empty.Files);
        Assert.Null(empty.Image);
        Assert.Equal(1, reads);
    }

    [Fact]
    public async Task BitmapInputFreezesOriginalAndReadsBoundedPremultipliedPreview()
    {
        await OnStaThread(async () =>
        {
            BitmapSource source = BitmapSource.Create(4, 2, 96, 96, PixelFormats.Bgra32, null,
                Enumerable.Range(0, 8).SelectMany(_ => new byte[] { 100, 50, 20, 128 }).ToArray(), 16);
            DataObject data = new();
            data.SetData(DataFormats.Bitmap, source, autoConvert: false);
            MemoryImageInput input = new WindowsImageClipboardService(() => data, _ => Assert.Fail()).ReadInput().Image!;
            Assert.True(source.IsFrozen);
            Assert.Equal(new PixelSize(4, 2), input.SourceSize);
            using PixelBuffer preview = await input.ReadPixelsAsync(new(2, 1), 8, TestContext.Current.CancellationToken);
            Assert.Equal(new PixelSize(2, 1), preview.Size);
            Assert.Equal(input.SourceSize, preview.SourceSize);
            Assert.Equal(new byte[] { 50, 25, 10, 128, 50, 25, 10, 128 }, preview.Pixels.ToArray());
            ImageDecodeException error = await Assert.ThrowsAsync<ImageDecodeException>(() =>
                input.ReadPixelsAsync(new(2, 1), 7, CancellationToken.None));
            Assert.Equal(ImageOpenError.ImageTooLarge, error.Error);
            using CancellationTokenSource cancelled = new();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => input.ReadPixelsAsync(new(2, 1), 8, cancelled.Token));
        });
    }

    [Fact]
    public async Task AllRotationAndFlipCombinationsPreserveSharedPaddedPixels()
    {
        await OnStaThread(async () =>
        {
            PixelSize size = new(3, 2);
            byte[] pixels = [1, 0, 0, 255, 2, 0, 0, 255, 3, 0, 0, 255, 99, 99, 99, 99,
                4, 0, 0, 255, 5, 0, 0, 255, 6, 0, 0, 255, 99, 99, 99, 99];
            byte[] original = (byte[])pixels.Clone();
            IDataObject? written = null;
            WindowsImageClipboardService service = new(() => written, data => written = Snapshot(data));
            for (int turns = 0; turns < 4; turns++)
            {
                for (int flips = 0; flips < 4; flips++)
                {
                    ViewOrientation orientation = default;
                    for (int t = 0; t < turns; t++) { orientation = orientation.RotateRight(); }
                    if ((flips & 1) != 0) { orientation = orientation.FlipHorizontal(); }
                    if ((flips & 2) != 0) { orientation = orientation.FlipVertical(); }
                    await service.WriteAsync(new(size, 16, pixels, orientation, OriginalSize: true));
                    MemoryImageInput input = service.ReadInput().Image!;
                    PixelSize display = orientation.GetDisplaySize(size);
                    Assert.Equal(display, input.SourceSize);
                    using PixelBuffer output = await input.ReadPixelsAsync(display, 24, TestContext.Current.CancellationToken);
                    byte[] expected = new byte[24];
                    for (int y = 0; y < size.Height; y++)
                    {
                        for (int x = 0; x < size.Width; x++)
                        {
                            var point = orientation.ToDisplayPoint(size, x + .5, y + .5);
                            Array.Copy(pixels, (y * 16) + (x * 4), expected, (((int)point.Y * display.Width) + (int)point.X) * 4, 4);
                        }
                    }
                    Assert.Equal(expected, output.Pixels.ToArray());
                    Assert.Equal(original, pixels);
                }
            }
        });
    }

    [Fact]
    public async Task DefaultWriteBoundsDisplaySizeAndOriginalWriteKeepsDimensions()
    {
        await OnStaThread(async () =>
        {
            IDataObject? written = null;
            WindowsImageClipboardService service = new(() => written, data => written = Snapshot(data));
            byte[] pixels = new byte[3000 * 2 * 4];
            await service.WriteAsync(new(new(3000, 2), 12000, pixels, default));
            Assert.Equal(new PixelSize(2560, 1), service.ReadInput().Image!.SourceSize);
            await service.WriteAsync(new(new(3000, 2), 12000, pixels, default(ViewOrientation).RotateRight()));
            Assert.Equal(new PixelSize(1, 1600), service.ReadInput().Image!.SourceSize);
            await service.WriteAsync(new(new(3000, 2), 12000, pixels, default, OriginalSize: true));
            Assert.Equal(new PixelSize(3000, 2), service.ReadInput().Image!.SourceSize);
        });
    }

    [Fact]
    public async Task FileDropHasPriorityAndHardLimitAndEmptyInputIsEmpty()
    {
        await OnStaThread(() =>
        {
            DataObject data = new();
            string[] paths = ["first.png", "second.jpg"];
            data.SetData(DataFormats.FileDrop, paths, autoConvert: false);
            data.SetData("PNG", new byte[] { 1 }, autoConvert: false);
            int reads = 0;
            WindowsImageClipboardService service = new(() => { reads++; return data; }, _ => Assert.Fail());
            ImageClipboardInput input = service.ReadInput();
            Assert.Equal(paths, input.Files);
            Assert.Null(input.Image);
            Assert.Equal(1, reads);
            data.SetData(DataFormats.FileDrop, new string[128], autoConvert: false);
            Assert.Equal(128, service.ReadInput().Files.Count);
            data.SetData(DataFormats.FileDrop, new string[129], autoConvert: false);
            Assert.Throws<ArgumentException>(() => service.ReadInput());
            Assert.Equal(new ImageClipboardInput(Array.Empty<string>()), new WindowsImageClipboardService(() => null, _ => Assert.Fail()).ReadInput());
            ImageClipboardInput empty = new WindowsImageClipboardService(() => new DataObject(), _ => Assert.Fail()).ReadInput();
            Assert.Empty(empty.Files);
            Assert.Null(empty.Image);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task PngRejectsCorruptionAndHeaderOrEncodedBudgetsBeforeDecode()
    {
        await OnStaThread(() =>
        {
            foreach (var sample in new[]
            {
                (Bytes: new byte[] { 1, 2, 3 }, Error: ImageOpenError.CorruptFile),
                (Bytes: PngHeader(0, 1), Error: ImageOpenError.CorruptFile),
                (Bytes: PngHeader(32769, 1), Error: ImageOpenError.ImageTooLarge),
                (Bytes: PngHeader(4097, 4096), Error: ImageOpenError.ImageTooLarge),
            })
            {
                DataObject data = new();
                data.SetData("PNG", sample.Bytes, autoConvert: false);
                ImageDecodeException error = Assert.Throws<ImageDecodeException>(() =>
                    new WindowsImageClipboardService(() => data, _ => Assert.Fail()).ReadInput());
                Assert.Equal(sample.Error, error.Error);
            }
            using LengthOnlyStream oversized = new();
            DataObject large = new();
            large.SetData("PNG", oversized, autoConvert: false);
            Assert.Equal(ImageOpenError.ImageTooLarge, Assert.Throws<ImageDecodeException>(() =>
                new WindowsImageClipboardService(() => large, _ => Assert.Fail()).ReadInput()).Error);
            COMException failure = (COMException)Marshal.GetExceptionForHR(unchecked((int)0x800401D0))!;
            Assert.Same(failure, Assert.Throws<COMException>(() =>
                new WindowsImageClipboardService(() => throw failure, _ => Assert.Fail()).ReadInput()));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task PngDecodeIsDeferredAndOpenMemoryMapsAsynchronousFailures()
    {
        await OnStaThread(async () =>
        {
            DataObject corruptData = new();
            corruptData.SetData("PNG", PngHeader(2, 2), autoConvert: false);
            // A valid header with missing PNG chunks passes the STA snapshot, but cannot decode.
            MemoryImageInput corrupt = new WindowsImageClipboardService(() => corruptData, _ => Assert.Fail()).ReadInput().Image!;
            Assert.Equal(new PixelSize(2, 2), corrupt.SourceSize);
            using ImageOpenCoordinator coordinator = new(new NoPicker(), new NoFileDecoder());
            Assert.False(await coordinator.OpenMemoryAsync(corrupt, cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal(ImageOpenError.CorruptFile, coordinator.State.Error);

            IDataObject? data = null;
            WindowsImageClipboardService service = new(() => data, output => data = output);
            await service.WriteAsync(new(new(1, 1), 4, new byte[4], default), TestContext.Current.CancellationToken);
            MemoryImageInput png = service.ReadInput().Image!;
            MemoryImageInput constrained = new(png.SourceSize, (maximum, _, token) => png.ReadPixelsAsync(maximum, 3, token));
            Assert.False(await coordinator.OpenMemoryAsync(constrained, cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal(ImageOpenError.ImageTooLarge, coordinator.State.Error);
            Assert.True(await coordinator.OpenMemoryAsync(png, cancellationToken: TestContext.Current.CancellationToken));
            Assert.True(coordinator.State.IsMemorySource);
            // Reuse the lazy frozen PNG source for a second bounded read.
            using PixelBuffer again = await png.ReadPixelsAsync(new(1, 1), 4, TestContext.Current.CancellationToken);
            Assert.Equal(new byte[4], again.Pixels.ToArray());
        });
    }

    [Fact]
    public async Task HighBitDepthSourcesAreRejectedBeforeFreezingOrPngDecoding()
    {
        await OnStaThread(() =>
        {
            byte[] header = PngHeader(1, 1);
            header[24] = 16;
            DataObject png = new();
            png.SetData("PNG", header, autoConvert: false);
            Assert.Equal(ImageOpenError.UnsupportedFormat, Assert.Throws<ImageDecodeException>(() =>
                new WindowsImageClipboardService(() => png, _ => Assert.Fail()).ReadInput()).Error);
            BitmapSource highDepth = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Rgb48, null, new byte[6], 6);
            DataObject bitmap = new();
            bitmap.SetData(DataFormats.Bitmap, highDepth, autoConvert: false);
            Assert.Equal(ImageOpenError.UnsupportedFormat, Assert.Throws<ImageDecodeException>(() =>
                new WindowsImageClipboardService(() => bitmap, _ => Assert.Fail()).ReadInput()).Error);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task InvalidOrCancelledWritesNeverPublish()
    {
        WindowsImageClipboardService service = new(() => null, _ => Assert.Fail("Unexpected clipboard write"));
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.WriteAsync(new(new(1, 1), 4, new byte[4], default), cancelled.Token));
        Assert.Equal(ImageOpenError.CorruptFile, (await Assert.ThrowsAsync<ImageDecodeException>(() =>
            service.WriteAsync(new(new(2, 1), 4, new byte[4], default), TestContext.Current.CancellationToken))).Error);
        Assert.Equal(ImageOpenError.ImageTooLarge, (await Assert.ThrowsAsync<ImageDecodeException>(() =>
            service.WriteAsync(new(new(8193, 4096), 8193 * 4, ReadOnlyMemory<byte>.Empty, default, true), TestContext.Current.CancellationToken))).Error);
    }

    [Fact]
    public async Task LargeExistingMainImageCanCopyPreviewButCannotCopyOriginal()
    {
        await OnStaThread(async () =>
        {
            DataObject? written = null;
            WindowsImageClipboardService service = new(() => written, data => written = Snapshot(data));
            byte[] pixels = new byte[5000 * 3500 * 4];
            Assert.True(pixels.LongLength > ClipboardImageLimits.SourceBytes);
            await service.WriteAsync(new(new(5000, 3500), 20000, pixels, default), TestContext.Current.CancellationToken);
            PixelSize preview = service.ReadInput().Image!.SourceSize;
            Assert.InRange(preview.Width, 1, 2560);
            Assert.InRange(preview.Height, 1, 1600);
            Assert.Equal(ImageOpenError.ImageTooLarge, (await Assert.ThrowsAsync<ImageDecodeException>(() =>
                service.WriteAsync(new(new(5000, 3500), 20000, pixels, default, true), TestContext.Current.CancellationToken))).Error);
        });
    }

    [Fact]
    public async Task CancellationAtFinalStaDispatchPreventsPublishing()
    {
        await OnStaThread(async () =>
        {
            using CancellationTokenSource cancellation = new();
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            void CancelAtDispatch(object? sender, DispatcherHookEventArgs args)
            {
                if (args.Operation.Priority == DispatcherPriority.Normal) { cancellation.Cancel(); }
            }
            dispatcher.Hooks.OperationStarted += CancelAtDispatch;
            try
            {
                WindowsImageClipboardService service = new(() => null, _ => Assert.Fail("Late write"));
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    service.WriteAsync(new(new(1, 1), 4, new byte[4], default), cancellation.Token));
            }
            finally { dispatcher.Hooks.OperationStarted -= CancelAtDispatch; }
        });
    }

    private static DataObject Snapshot(IDataObject data)
    {
        DataObject result = new();
        MemoryStream png = Assert.IsAssignableFrom<MemoryStream>(data.GetData("PNG", autoConvert: false));
        Assert.Equal(0, png.Position);
        result.SetData("PNG", new MemoryStream(png.ToArray()), autoConvert: false);
        BitmapSource bitmap = Assert.IsAssignableFrom<BitmapSource>(data.GetData(DataFormats.Bitmap, autoConvert: false));
        Assert.True(bitmap.IsFrozen);
        result.SetData(DataFormats.Bitmap, bitmap, autoConvert: false);
        return result;
    }

    private static byte[] PngHeader(uint width, uint height)
    {
        byte[] header = new byte[33];
        new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(header, 0);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8), 13);
        "IHDR"u8.CopyTo(header.AsSpan(12));
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16), width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(20), height);
        header[24] = 8;
        header[25] = 6;
        return header;
    }

    private sealed class LengthOnlyStream : MemoryStream
    {
        public override long Length => ClipboardImageLimits.EncodedBytes + 1;
        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Must check length first");
        public override int Read(Span<byte> buffer) => throw new InvalidOperationException("Must check length first");
    }

    private sealed class NoPicker : IImageFilePicker
    {
        public Task<string?> PickImageAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("Unexpected file picker");
    }

    private sealed class NoFileDecoder : IImageDecoder
    {
        public Task<PixelBuffer> DecodeAsync(string path, CancellationToken cancellationToken) => throw new InvalidOperationException("Unexpected file decoder");
    }

    private static Task OnStaThread(Func<Task> action)
    {
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread thread = new(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await action(); completion.SetResult(); }
                catch (Exception exception) { completion.SetException(exception); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal); }
            }));
            Dispatcher.Run();
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
