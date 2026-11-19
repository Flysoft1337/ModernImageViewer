using System.Runtime.InteropServices;

using ImageMagick;

using ModernImageViewer.Codecs.Modern;
using ModernImageViewer.Imaging;

namespace ModernImageViewer.Shell;

internal static unsafe class ShellPsdBitmap
{
    private const long MaximumInputBytes = 128L * 1024 * 1024;
    private const long MaximumSourcePixels = 32_000_000;
    private static readonly Lazy<bool> Initialized = new(Initialize);
    private static readonly Lock DecodeGate = new();

    internal static (nint Handle, int Width, int Height) Decode(Stream stream, int maximumEdge)
    {
        if (stream.Length > MaximumInputBytes) { throw new InvalidDataException(); }
        stream.Position = 0;
        using Stream composite = PsdComposite.Open(stream, MaximumSourcePixels, CancellationToken.None, out PixelSize source);
        lock (DecodeGate)
        {
            _ = Initialized.Value;
            using MagickImage image = new();
            image.Read(composite, PsdComposite.CreateReadSettings());
            if (image.Width != source.Width || image.Height != source.Height) { throw new InvalidDataException(); }
            if (Math.Max(source.Width, source.Height) > maximumEdge)
            {
                image.Resize((uint)maximumEdge, (uint)maximumEdge);
            }
            PixelSize size = new((int)image.Width, (int)image.Height);
            if (size.Width < 1 || size.Height < 1 || Math.Max(size.Width, size.Height) > maximumEdge)
            {
                throw new InvalidDataException();
            }
            PsdComposite.ConvertToSrgb(image);
            return (CreateBitmap(image, size), size.Width, size.Height);
        }
    }

    private static nint CreateBitmap(MagickImage image, PixelSize size)
    {
        BitmapHeader header = new()
        {
            HeaderSize = (uint)sizeof(BitmapHeader),
            Width = size.Width,
            Height = -size.Height,
            Planes = 1,
            BitsPerPixel = 32,
        };
        nint bitmap = ShellNative.CreateDIBSection(0, in header, 0, out nint bits, 0, 0);
        if (bitmap == 0) { throw new InvalidOperationException("Could not allocate the PSD preview bitmap."); }
        try
        {
            using IPixelCollection<byte> pixels = image.GetPixels();
            int stride = checked(size.Width * 4);
            for (int row = 0; row < size.Height; ++row)
            {
                byte[] values = pixels.ToByteArray(0, row, (uint)size.Width, 1, PixelMapping.BGRA) ?? throw new InvalidDataException();
                if (values.Length != stride) { throw new InvalidDataException(); }
                PsdComposite.PremultiplyRow(values);
                values.CopyTo(new Span<byte>((byte*)bits + row * stride, stride));
            }
            return bitmap;
        }
        catch
        {
            _ = ShellNative.DeleteObject(bitmap);
            throw;
        }
    }

    private static bool Initialize()
    {
        // Resolve the packaged dependency in the installed application directory, never against the selected file or working directory.
        var address = (delegate* unmanaged<void>)&ModuleAddress;
        char[] pathBuffer = new char[32_768];
        if (ShellNative.GetModuleHandleEx(6, (nint)address, out nint module) == 0) { throw new InvalidOperationException(); }
        uint count;
        fixed (char* path = pathBuffer) { count = ShellNative.GetModuleFileName(module, path, (uint)pathBuffer.Length); }
        if (count == 0 || count >= pathBuffer.Length) { throw new InvalidOperationException(); }
        string directory = Path.GetDirectoryName(new string(pathBuffer, 0, (int)count))!;
        _ = NativeLibrary.Load(Path.GetFullPath(Path.Combine(directory, "..", "Magick.Native-Q8-x64.dll")));
        MagickNET.SetEnvironmentVariable("MAGICK_CONFIGURE_PATH", directory);
        ResourceLimits.Memory = 192 * 1024 * 1024;
        ResourceLimits.Disk = 0;
        ResourceLimits.Thread = 1;
        ResourceLimits.ListLength = 16;
        ResourceLimits.Width = (ulong)ImageDecodeLimits.Default.MaximumDimension;
        ResourceLimits.Height = (ulong)ImageDecodeLimits.Default.MaximumDimension;
        ResourceLimits.MaxMemoryRequest = 128 * 1024 * 1024;
        ResourceLimits.MaxProfileSize = 4 * 1024 * 1024;
        return true;
    }

    [UnmanagedCallersOnly]
    private static void ModuleAddress() { }
}
