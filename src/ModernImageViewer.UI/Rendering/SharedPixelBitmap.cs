using System.Runtime.InteropServices;

using ModernImageViewer.Imaging;

using SkiaSharp;

namespace ModernImageViewer.UI.Rendering;

public static class SharedPixelBitmap
{
    private static readonly SKBitmapReleaseDelegate ReleasePixels = (_, context) => ((PinnedPixels)context).Dispose();

    public static SKBitmap Create(PixelBuffer image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (!MemoryMarshal.TryGetArray(image.Pixels, out ArraySegment<byte> pixels) || pixels.Array is null)
        {
            throw new InvalidOperationException("An array-backed pixel buffer is required.");
        }

        PinnedPixels pin = new(pixels.Array, pixels.Offset);
        SKBitmap bitmap = new();
        try
        {
            SKImageInfo info = new(image.Size.Width, image.Size.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            if (!bitmap.InstallPixels(info, pin.Address, image.Stride, ReleasePixels, pin))
            {
                throw new InvalidOperationException("The pixel buffer could not be attached to the renderer.");
            }
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            pin.Dispose();
            throw;
        }
    }

    private sealed class PinnedPixels : IDisposable
    {
        private IntPtr _handle;

        public PinnedPixels(byte[] pixels, int offset)
        {
            GCHandle handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            _handle = GCHandle.ToIntPtr(handle);
            Address = IntPtr.Add(handle.AddrOfPinnedObject(), offset);
        }

        public IntPtr Address { get; }

        public void Dispose()
        {
            IntPtr handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
            if (handle != IntPtr.Zero)
            {
                GCHandle.FromIntPtr(handle).Free();
            }
        }
    }
}
