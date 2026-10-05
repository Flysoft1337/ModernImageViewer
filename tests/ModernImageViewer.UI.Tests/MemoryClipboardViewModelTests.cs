using System.Globalization;
using System.IO;
using System.Threading.Channels;

using ModernImageViewer.Application.Browsing;
using ModernImageViewer.Application.Images;
using ModernImageViewer.Application.Integration;
using ModernImageViewer.Imaging;
using ModernImageViewer.UI.Localization;
using ModernImageViewer.UI.ViewModels;

namespace ModernImageViewer.UI.Tests;

public sealed class MemoryClipboardViewModelTests
{
    [Fact]
    public async Task PastedImageHasNoFileOperationsAndCopyUsesImmutableOrientedPixels()
    {
        ClipboardService clipboard = new();
        PixelSize size = new(2, 1);
        byte[] pixels = [0, 0, 255, 255, 255, 0, 0, 255];
        clipboard.Input = new([], new(size, (_, _, _) => Task.FromResult(new PixelBuffer(size, 8, pixels))));
        using ImageOpenCoordinator coordinator = new(new NullPicker(), new NoDecode());
        ImageBrowseSession browsing = new();
        using MainWindowViewModel model = new(new Localization(), coordinator, browsing, imageClipboard: clipboard);
        await model.PasteFilesCommand.ExecuteAsync();
        Assert.True(model.Presentation.IsMemorySource);
        Assert.Equal("Clipboard_Image", model.CurrentFileName);
        Assert.Empty(model.CurrentFilePath);
        Assert.Empty(model.DirectoryPath);
        Assert.False(model.CanReveal);
        Assert.False(model.CanCopyPath);
        Assert.False(model.CanSort);
        Assert.False(model.CanMovePrevious);
        Assert.False(model.CanMoveNext);
        Assert.False(model.CanPlaySlideshow);
        Assert.Empty(model.BrowseItems);
        Assert.True(model.CanCopyOriginal);
        Guid firstIdentity = model.Presentation.Source!.Identity;
        model.UpdateViewOrientation(default(ViewOrientation).RotateRight().FlipHorizontal());
        await model.CopyPreviewCommand.ExecuteAsync();
        Assert.False(clipboard.Written!.OriginalSize);
        Assert.Equal(90, clipboard.Written.Orientation.RotationDegrees);
        Assert.True(clipboard.Written.Orientation.IsFlippedHorizontally);
        Assert.Equal(pixels, clipboard.Written.Pixels.ToArray());
        await model.CopyOriginalCommand.ExecuteAsync();
        Assert.True(clipboard.Written!.OriginalSize);
        Assert.Equal("Clipboard_OriginalCopied", model.StatusText);
        await model.PasteFilesCommand.ExecuteAsync();
        Assert.NotEqual(firstIdentity, model.Presentation.Source!.Identity);
        Assert.Equal(pixels, clipboard.Written.Pixels.ToArray()); // Disposed wrapper did not invalidate the copy snapshot.
    }

    [Fact]
    public async Task OriginalCopyRequiresFullPixelsAndOpeningAnotherSourceCancelsPendingCopy()
    {
        ClipboardService clipboard = new();
        PixelSize size = new(4, 2);
        clipboard.Input = new([], new(size, (maximum, _, _) => Task.FromResult(maximum == size
            ? new PixelBuffer(size, 16, new byte[32])
            : new PixelBuffer(new(2, 1), 8, new byte[8], sourceSize: size))));
        using ImageOpenCoordinator coordinator = new(new NullPicker(), new NoDecode());
        using MainWindowViewModel model = new(new Localization(), coordinator, new(), imageClipboard: clipboard);
        await model.PasteFilesCommand.ExecuteAsync();
        Assert.True(model.ShowPreviewStatus);
        Assert.False(model.CanCopyOriginal);
        await model.RefineImageAsync();
        Assert.True(model.CanCopyOriginal);
        clipboard.WaitForCancellation = true;
        Task copying = model.CopyPreviewCommand.ExecuteAsync();
        CancellationToken pending = await clipboard.Writes.Reader.ReadAsync(TestContext.Current.CancellationToken);
        await model.PasteFilesCommand.ExecuteAsync();
        Assert.True(pending.IsCancellationRequested);
        await copying;
        Assert.Null(clipboard.Written);
    }

    private sealed class ClipboardService : IImageClipboardService
    {
        public ImageClipboardInput Input { get; set; } = new([]);
        public ImageClipboardPixels? Written { get; private set; }
        public bool WaitForCancellation { get; set; }
        public Channel<CancellationToken> Writes { get; } = Channel.CreateUnbounded<CancellationToken>();
        public ImageClipboardInput ReadInput() => Input;
        public async Task WriteAsync(ImageClipboardPixels image, CancellationToken cancellationToken = default)
        {
            if (WaitForCancellation)
            {
                Writes.Writer.TryWrite(cancellationToken);
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            Written = image;
        }
    }

    private sealed class NullPicker : IImageFilePicker
    {
        public Task<string?> PickImageAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }
    private sealed class NoDecode : IImageDecoder
    {
        public Task<PixelBuffer> DecodeAsync(string path, CancellationToken cancellationToken) => throw new IOException("Memory images must not reach a file decoder");
    }
    private sealed class Localization : ILocalizationService
    {
        public event EventHandler? CultureChanged { add { } remove { } }
        public CultureInfo CurrentCulture => CultureInfo.GetCultureInfo("en-US");
        public IReadOnlyList<SupportedLanguage> SupportedLanguages { get; } = [new("en-US", "English")];
        public void Initialize() { }
        public void SetCulture(string cultureName) { }
        public string GetString(string name) => name;
    }
}
