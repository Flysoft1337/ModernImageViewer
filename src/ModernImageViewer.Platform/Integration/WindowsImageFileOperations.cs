using System.IO;
using System.Runtime.InteropServices;

using ModernImageViewer.Application.Integration;

namespace ModernImageViewer.Platform.Integration;

public sealed class WindowsImageFileOperations : IImageFileOperations
{
    public Task<ImageFileOperationResult> RenameAsync(string sourcePath, string destinationPath) => Task.Run(() =>
        OperateOnFile(sourcePath, () =>
        {
            File.Move(sourcePath, destinationPath, overwrite: false);
            return ImageFileOperationResult.Success;
        }));

    public Task<ImageFileOperationResult> RecycleAsync(string path)
    {
        TaskCompletionSource<ImageFileOperationResult> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread worker = new(() => completion.SetResult(OperateOnFile(path, () => RecycleOnShellApartment(path))))
        {
            IsBackground = true,
            Name = "Recycle image file",
        };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        return completion.Task;
    }

    private static ImageFileOperationResult OperateOnFile(string path, Func<ImageFileOperationResult> operation)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.Directory) == 0
                ? operation()
                : ImageFileOperationResult.MissingFile;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or COMException
            or ArgumentException or NotSupportedException)
        {
            return exception.HResult switch
            {
                unchecked((int)0x80070002) or unchecked((int)0x80070003) => ImageFileOperationResult.MissingFile,
                unchecked((int)0x80070050) or unchecked((int)0x800700B7) => ImageFileOperationResult.NameConflict,
                unchecked((int)0x80070005) => ImageFileOperationResult.AccessDenied,
                unchecked((int)0x80070020) or unchecked((int)0x80070021) => ImageFileOperationResult.FileInUse,
                _ => ImageFileOperationResult.Unavailable,
            };
        }
    }

    private static ImageFileOperationResult RecycleOnShellApartment(string path)
    {
        Guid itemId = typeof(IShellItem).GUID;
        Guid transferId = typeof(ITransferSource).GUID;
        Guid transferHandler = new("D5E346A1-F753-4932-B403-4574800E2498");
        Guid recycleFolder = new("B7534046-3ECB-4C18-BE4E-64CD4CB7D6AC");
        IShellItem? source = null;
        IShellItem? parent = null;
        IShellItem? recycleBin = null;
        ITransferSource? transfer = null;
        IShellItem? recycled = null;
        try
        {
            Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(path, IntPtr.Zero, in itemId, out source));
            source.GetParent(out parent);
            parent.BindToHandler(IntPtr.Zero, in transferHandler, in transferId, out transfer);
            Marshal.ThrowExceptionForHR(SHGetKnownFolderItem(in recycleFolder, 0, IntPtr.Zero, in itemId, out recycleBin));
            // Invoke the recycle operation directly. An unavailable Recycle Bin never triggers a permanent delete.
            int result = transfer.RecycleItem(source, recycleBin, 0, out recycled);
            Marshal.ThrowExceptionForHR(result);
            return recycled is not null ? ImageFileOperationResult.Success : ImageFileOperationResult.Unavailable;
        }
        finally
        {
            if (recycled is not null) { Marshal.ReleaseComObject(recycled); }
            if (transfer is not null) { Marshal.ReleaseComObject(transfer); }
            if (recycleBin is not null) { Marshal.ReleaseComObject(recycleBin); }
            if (parent is not null) { Marshal.ReleaseComObject(parent); }
            if (source is not null) { Marshal.ReleaseComObject(source); }
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr context, in Guid interfaceId, out IShellItem item);

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHGetKnownFolderItem(in Guid folderId, uint flags, IntPtr token, in Guid interfaceId, out IShellItem item);

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr context, in Guid handler, in Guid interfaceId, out ITransferSource transfer);
        void GetParent(out IShellItem parent);
    }

    [ComImport, Guid("00ADB003-BDE9-45C6-8E29-D09F9353E108"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITransferSource
    {
        // The preceding members preserve the Shell interface's vtable order.
        void Advise();
        void Unadvise();
        void SetProperties();
        void OpenItem();
        void MoveItem();
        [PreserveSig]
        int RecycleItem(IShellItem source, IShellItem destination, uint flags, out IShellItem? recycled);
    }
}
