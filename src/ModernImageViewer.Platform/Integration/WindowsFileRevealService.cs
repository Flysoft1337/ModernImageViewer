using System.IO;
using System.Runtime.InteropServices;

using ModernImageViewer.Application.Integration;

namespace ModernImageViewer.Platform.Integration;

/// <summary>Reveals one existing file without passing its path through command-line parsing.</summary>
public sealed class WindowsFileRevealService : IFileRevealService
{
    // A blocked network path or Shell extension must not start additional native workers.
    private static readonly SemaphoreSlim s_revealGate = new(1, 1);
    private readonly IFileRevealNativeApi _native;
    private readonly Func<string, FileAttributes> _getAttributes;

    public WindowsFileRevealService()
        : this(new FileRevealNativeApi(), File.GetAttributes)
    {
    }

    internal WindowsFileRevealService(IFileRevealNativeApi native, Func<string, FileAttributes> getAttributes)
    {
        _native = native;
        _getAttributes = getAttributes;
    }

    public async Task<FileRevealResult> RevealAsync(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            await s_revealGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return FileRevealResult.Canceled;
        }

        TaskCompletionSource<FileRevealResult> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread worker = new(() =>
        {
            try
            {
                completion.TrySetResult(RevealOnWorker(path, cancellationToken));
            }
            catch (Exception)
            {
                // An optional Shell action must not terminate the viewer on a worker exception.
                completion.TrySetResult(FileRevealResult.Unavailable);
            }
            finally
            {
                s_revealGate.Release();
            }
        })
        {
            IsBackground = true,
            Name = "Image file reveal",
        };

        try
        {
            worker.SetApartmentState(ApartmentState.STA);
            worker.Start();
        }
        catch (Exception exception) when (exception is PlatformNotSupportedException or ThreadStateException or OutOfMemoryException)
        {
            s_revealGate.Release();
            return FileRevealResult.Unavailable;
        }

        try
        {
            // Native Shell work cannot be interrupted, but cancellation can return immediately.
            // Its worker still owns the gate and releases every PIDL before the next request.
            return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return FileRevealResult.Canceled;
        }
    }

    private FileRevealResult RevealOnWorker(string path, CancellationToken cancellationToken)
    {
        bool initialized = false;
        IntPtr folderId = IntPtr.Zero;
        IntPtr fileId = IntPtr.Zero;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            {
                return FileRevealResult.Unavailable;
            }

            string fullPath = Path.GetFullPath(path);
            if ((_getAttributes(fullPath) & FileAttributes.Directory) != 0)
            {
                return FileRevealResult.MissingFile;
            }

            string? folder = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrEmpty(folder))
            {
                return FileRevealResult.Unavailable;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (_native.InitializeApartment() < 0)
            {
                return FileRevealResult.Unavailable;
            }

            initialized = true;
            folderId = _native.CreateItemIdList(folder);
            fileId = _native.CreateItemIdList(fullPath);
            if (folderId == IntPtr.Zero || fileId == IntPtr.Zero)
            {
                return FileRevealResult.Unavailable;
            }

            IntPtr childId = _native.GetLastItemId(fileId);
            if (childId == IntPtr.Zero)
            {
                return FileRevealResult.Unavailable;
            }

            cancellationToken.ThrowIfCancellationRequested();
            int result = _native.OpenFolderAndSelectItem(folderId, childId);
            return cancellationToken.IsCancellationRequested
                ? FileRevealResult.Canceled
                : result >= 0 ? FileRevealResult.Success : FileRevealResult.Unavailable;
        }
        catch (OperationCanceledException)
        {
            return FileRevealResult.Canceled;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return FileRevealResult.MissingFile;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException or System.Security.SecurityException
            or COMException or DllNotFoundException or EntryPointNotFoundException)
        {
            return FileRevealResult.Unavailable;
        }
        finally
        {
            if (fileId != IntPtr.Zero)
            {
                _native.FreeItemIdList(fileId);
            }

            if (folderId != IntPtr.Zero)
            {
                _native.FreeItemIdList(folderId);
            }

            if (initialized)
            {
                _native.UninitializeApartment();
            }
        }
    }
}

internal interface IFileRevealNativeApi
{
    int InitializeApartment();
    void UninitializeApartment();
    IntPtr CreateItemIdList(string path);
    IntPtr GetLastItemId(IntPtr absoluteItemId);
    int OpenFolderAndSelectItem(IntPtr folderId, IntPtr childId);
    void FreeItemIdList(IntPtr itemId);
}

internal sealed class FileRevealNativeApi : IFileRevealNativeApi
{
    public int InitializeApartment() => CoInitializeEx(IntPtr.Zero, 0x2);

    public void UninitializeApartment() => CoUninitialize();

    public IntPtr CreateItemIdList(string path) => ILCreateFromPath(path);

    public IntPtr GetLastItemId(IntPtr absoluteItemId) => ILFindLastID(absoluteItemId);

    public int OpenFolderAndSelectItem(IntPtr folderId, IntPtr childId) =>
        SHOpenFolderAndSelectItems(folderId, 1, [childId], 0);

    public void FreeItemIdList(IntPtr itemId) => ILFree(itemId);

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint initialization);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "ILCreateFromPathW", ExactSpelling = true)]
    private static extern IntPtr ILCreateFromPath(string path);

    [DllImport("shell32.dll")]
    private static extern IntPtr ILFindLastID(IntPtr absoluteItemId);

    [DllImport("shell32.dll")]
    private static extern void ILFree(IntPtr itemId);

    [DllImport("shell32.dll")]
    private static extern int SHOpenFolderAndSelectItems(IntPtr folderId, uint itemCount,
        [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] IntPtr[] childIds, uint flags);
}
