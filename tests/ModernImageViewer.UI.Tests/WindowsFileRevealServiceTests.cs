using System.IO;
using System.Runtime.InteropServices;

using ModernImageViewer.Application.Integration;
using ModernImageViewer.Platform.Integration;

namespace ModernImageViewer.UI.Tests;

public sealed class WindowsFileRevealServiceTests
{
    [Fact]
    public async Task UnicodeAndPunctuationPathsReachTheShellUnchangedOnAnStaWorker()
    {
        string path = Path.Combine(Path.GetTempPath(), "图片 空格,逗号", "照片 (1),你好.png");
        FakeNativeApi native = new();
        string? inspectedPath = null;
        WindowsFileRevealService service = new(native, candidate =>
        {
            inspectedPath = candidate;
            return FileAttributes.Normal;
        });

        Assert.Equal(FileRevealResult.Success, await service.RevealAsync(path, TestContext.Current.CancellationToken));
        Assert.Equal(path, inspectedPath);
        Assert.Equal([Path.GetDirectoryName(path)!, path], native.Paths);
        Assert.Equal(ApartmentState.STA, native.Apartment);
        Assert.Equal([new IntPtr(2), new IntPtr(1)], native.Freed);
        Assert.Equal(1, native.Uninitializations);
        Assert.Equal(new IntPtr(1), native.SelectedFolder);
        Assert.Equal(new IntPtr(3), native.SelectedChild);
    }

    [Theory]
    [InlineData(0, FileRevealResult.MissingFile)]
    [InlineData(1, FileRevealResult.Unavailable)]
    [InlineData(2, FileRevealResult.MissingFile)]
    [InlineData(3, FileRevealResult.Unavailable)]
    public async Task MissingAndUnreadableFilesDoNotInvokeTheShell(int failure, FileRevealResult expected)
    {
        FakeNativeApi native = new();
        WindowsFileRevealService service = new(native, _ => failure switch
        {
            0 => throw new FileNotFoundException(),
            1 => throw new UnauthorizedAccessException(),
            2 => FileAttributes.Directory,
            _ => throw new IOException(),
        });

        Assert.Equal(expected, await service.RevealAsync(Path.Combine(Path.GetTempPath(), "missing.png"), TestContext.Current.CancellationToken));
        Assert.Empty(native.Paths);
        Assert.Equal(0, native.Initializations);
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(1, 1, 1)]
    [InlineData(2, 1, 1)]
    [InlineData(3, 2, 1)]
    [InlineData(4, 2, 1)]
    [InlineData(5, 2, 1)]
    public async Task ShellFailuresReleaseEveryOwnedItemList(int failure, int freedCount, int uninitializations)
    {
        FakeNativeApi native = new() { Failure = failure };
        WindowsFileRevealService service = new(native, _ => FileAttributes.Normal);

        Assert.Equal(FileRevealResult.Unavailable, await service.RevealAsync(Path.Combine(Path.GetTempPath(), "image.png"), TestContext.Current.CancellationToken));
        Assert.Equal(freedCount, native.Freed.Count);
        Assert.Equal(uninitializations, native.Uninitializations);
        Assert.Equal(native.Freed.Count, native.Freed.Distinct().Count());
    }

    [Fact]
    public async Task CancellationReturnsPromptlyAndKeepsTheNativeWorkerBounded()
    {
        using ManualResetEventSlim release = new();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeNativeApi native = new()
        {
            BeforeSelect = () =>
            {
                entered.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(10)))
                {
                    throw new IOException("The test did not release the native worker.");
                }
            },
        };
        WindowsFileRevealService service = new(native, _ => FileAttributes.Normal);
        string path = Path.Combine(Path.GetTempPath(), "image.png");
        using CancellationTokenSource activeCancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        Task<FileRevealResult> active = service.RevealAsync(path, activeCancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        FakeNativeApi queuedNative = new();
        WindowsFileRevealService queuedService = new(queuedNative, _ => FileAttributes.Normal);
        using CancellationTokenSource queuedCancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        Task<FileRevealResult> queued = queuedService.RevealAsync(path, queuedCancellation.Token);
        try
        {
            queuedCancellation.Cancel();
            Assert.Equal(FileRevealResult.Canceled, await queued.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            Assert.Equal(0, queuedNative.Initializations);
            activeCancellation.Cancel();
            Assert.Equal(FileRevealResult.Canceled, await active.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            Assert.Empty(native.Freed);
        }
        finally
        {
            release.Set();
        }

        // Acquiring the same gate proves that the canceled native worker finished cleanup.
        Assert.Equal(FileRevealResult.Success, await queuedService.RevealAsync(path, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.Equal(2, native.Freed.Count);
        Assert.Equal(1, native.Uninitializations);
    }

    private sealed class FakeNativeApi : IFileRevealNativeApi
    {
        public int Failure { get; init; } = -1;
        public Action? BeforeSelect { get; init; }
        public List<string> Paths { get; } = [];
        public List<IntPtr> Freed { get; } = [];
        public ApartmentState Apartment { get; private set; }
        public int Initializations { get; private set; }
        public int Uninitializations { get; private set; }
        public IntPtr SelectedFolder { get; private set; }
        public IntPtr SelectedChild { get; private set; }

        public int InitializeApartment()
        {
            Initializations++;
            Apartment = Thread.CurrentThread.GetApartmentState();
            return Failure == 0 ? unchecked((int)0x80004005) : 0;
        }

        public void UninitializeApartment() => Uninitializations++;

        public IntPtr CreateItemIdList(string path)
        {
            Paths.Add(path);
            return Failure == Paths.Count ? IntPtr.Zero : new IntPtr(Paths.Count);
        }

        public IntPtr GetLastItemId(IntPtr absoluteItemId) => Failure == 3 ? IntPtr.Zero : new IntPtr(3);

        public int OpenFolderAndSelectItem(IntPtr folderId, IntPtr childId)
        {
            SelectedFolder = folderId;
            SelectedChild = childId;
            BeforeSelect?.Invoke();
            if (Failure == 5)
            {
                Marshal.ThrowExceptionForHR(unchecked((int)0x80004005));
            }

            return Failure == 4 ? unchecked((int)0x80004005) : 0;
        }

        public void FreeItemIdList(IntPtr itemId) => Freed.Add(itemId);
    }
}
