using System.Windows;

using ModernImageViewer.Application.Images;
using ModernImageViewer.Platform.Integration;

namespace ModernImageViewer.UI.Tests;

public sealed class ClipboardFileServiceTests
{
    [Fact]
    public void ReadsOnlyExactFileDropAndRejectsOversizedListsWithoutTruncation()
    {
        string[] paths = [@"C:\图片\第二张.jpg", @"C:\图片\第一张.png"];
        FileOnlyData data = new(paths);
        int snapshots = 0;
        WindowsClipboardFileService service = new(() => { snapshots++; return data; });
        Assert.Equal(paths, service.ReadFiles());
        Assert.Equal(1, snapshots);
        data.Value = new string[OpenRequest.MaximumPaths];
        Assert.Equal(OpenRequest.MaximumPaths, service.ReadFiles().Count);
        data.Value = new string[OpenRequest.MaximumPaths + 1];
        Assert.Throws<ArgumentException>(() => service.ReadFiles());
        data.Value = null; // Empty, text-only or bitmap-only clipboard.
        Assert.Empty(service.ReadFiles());
        data.Value = "not a file list";
        Assert.Empty(service.ReadFiles());
        Assert.Empty(new WindowsClipboardFileService(() => null).ReadFiles());
    }

    // Any accidental image conversion, format enumeration or second-format read fails the test.
    private sealed class FileOnlyData(object? value) : IDataObject
    {
        public object? Value { get; set; } = value;
        public object GetData(string format, bool autoConvert)
        {
            Assert.Equal(DataFormats.FileDrop, format);
            Assert.False(autoConvert);
            return Value!;
        }
        public object GetData(string format) => throw new InvalidOperationException();
        public object GetData(Type format) => throw new InvalidOperationException();
        public bool GetDataPresent(string format, bool autoConvert) => throw new InvalidOperationException();
        public bool GetDataPresent(string format) => throw new InvalidOperationException();
        public bool GetDataPresent(Type format) => throw new InvalidOperationException();
        public string[] GetFormats(bool autoConvert) => throw new InvalidOperationException();
        public string[] GetFormats() => throw new InvalidOperationException();
        public void SetData(string format, object data, bool autoConvert) => throw new InvalidOperationException();
        public void SetData(string format, object data) => throw new InvalidOperationException();
        public void SetData(Type format, object data) => throw new InvalidOperationException();
        public void SetData(object data) => throw new InvalidOperationException();
    }
}
