using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

using SkiaSharp;

namespace ModernImageViewer.UI.Tests;

internal static class AnimationCodecFixtures
{
    internal sealed record GifFrame(int X, int Y, int Width, int Height, byte[] Colors,
        int Disposal = 1, int Delay = 3);

    internal static byte[] Gif(int width = 4, int height = 2, int? repeats = null,
        IReadOnlyList<GifFrame>? frames = null)
    {
        frames ??=
        [
            new(0, 0, 4, 2, [1, 1, 1, 1, 1, 1, 1, 1]),
            new(0, 0, 2, 2, [2, 0, 0, 2], Disposal: 3),
            new(2, 0, 2, 2, [3, 3, 3, 3]),
            new(0, 0, 2, 2, [2, 2, 2, 2], Disposal: 2),
            new(2, 0, 2, 2, [3, 3, 3, 3]),
        ];
        using MemoryStream output = new();
        using BinaryWriter writer = new(output);
        writer.Write("GIF89a"u8);
        writer.Write(checked((ushort)width));
        writer.Write(checked((ushort)height));
        writer.Write(new byte[] { 0x81, 0, 0, 0, 0, 0, 255, 0, 0, 0, 255, 0, 0, 0, 255 });
        if (repeats is { } repeat)
        {
            writer.Write(new byte[] { 0x21, 0xff, 11 });
            writer.Write("NETSCAPE2.0"u8);
            writer.Write(new byte[] { 3, 1 });
            writer.Write(checked((ushort)repeat));
            writer.Write((byte)0);
        }
        foreach (GifFrame frame in frames)
        {
            writer.Write(new byte[] { 0x21, 0xf9, 4, checked((byte)((frame.Disposal * 4) + 1)) });
            writer.Write(checked((ushort)frame.Delay));
            writer.Write(new byte[] { 0, 0, 0x2c });
            writer.Write(checked((ushort)frame.X));
            writer.Write(checked((ushort)frame.Y));
            writer.Write(checked((ushort)frame.Width));
            writer.Write(checked((ushort)frame.Height));
            writer.Write(new byte[] { 0, 2 });
            // Clear before every literal keeps the test encoder's code width fixed at 3 bits.
            List<byte> packed = [];
            int bits = 0;
            int bitCount = 0;
            void Code(int code)
            {
                bits |= code << bitCount;
                bitCount += 3;
                while (bitCount >= 8)
                {
                    packed.Add((byte)(bits & 255));
                    bits >>= 8;
                    bitCount -= 8;
                }
            }
            foreach (byte color in frame.Colors) { Code(4); Code(color); }
            Code(5);
            if (bitCount > 0) { packed.Add((byte)bits); }
            for (int offset = 0; offset < packed.Count; offset += 255)
            {
                int length = Math.Min(255, packed.Count - offset);
                writer.Write((byte)length);
                writer.Write(packed.GetRange(offset, length).ToArray());
            }
            writer.Write((byte)0);
        }
        writer.Write((byte)0x3b);
        writer.Flush();
        return output.ToArray();
    }

    internal static byte[] WebP(ushort totalPlays = 2, ushort orientation = 1)
    {
        using MemoryStream output = new();
        using BinaryWriter writer = new(output);
        writer.Write("RIFF"u8);
        writer.Write(0);
        writer.Write("WEBP"u8);
        Chunk(writer, "VP8X", [(byte)(orientation == 1 ? 0x12 : 0x1a), 0, 0, 0, 3, 0, 0, 1, 0, 0]);
        Chunk(writer, "ANIM", [255, 255, 255, 255, (byte)totalPlays, (byte)(totalPlays >> 8)]);
        (int X, int Width, SKColor Color, int Flags, int Duration)[] frames =
        [
            (0, 4, SKColors.Red, 2, 30),
            (0, 2, new SKColor(0, 255, 0, 128), 1, 10),
            (2, 2, SKColors.Blue, 2, 40),
        ];
        foreach (var frame in frames)
        {
            using SKBitmap bitmap = new(new SKImageInfo(frame.Width, 2, SKColorType.Bgra8888, SKAlphaType.Premul));
            bitmap.Erase(frame.Color);
            using SKPixmap pixmap = bitmap.PeekPixels();
            using SKData encoded = SKWebpEncoder.Encode(pixmap,
                new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossless, 100))!;
            byte[] bytes = encoded.ToArray();
            using MemoryStream payload = new();
            using BinaryWriter frameWriter = new(payload);
            U24(frameWriter, frame.X / 2);
            U24(frameWriter, 0);
            U24(frameWriter, frame.Width - 1);
            U24(frameWriter, 1);
            U24(frameWriter, frame.Duration);
            frameWriter.Write((byte)frame.Flags);
            frameWriter.Write(bytes, 12, bytes.Length - 12);
            frameWriter.Flush();
            Chunk(writer, "ANMF", payload.ToArray());
        }
        if (orientation != 1)
        {
            Chunk(writer, "EXIF", [73, 73, 42, 0, 8, 0, 0, 0, 1, 0, 18, 1, 3, 0, 1, 0, 0, 0,
                (byte)orientation, 0, 0, 0, 0, 0, 0, 0]);
        }
        writer.Flush();
        byte[] result = output.ToArray();
        BitConverter.GetBytes(result.Length - 8).CopyTo(result, 4);
        return result;
    }

    internal static byte[] LargeGif() => Gif(4096, 4096, 0,
    [
        new(1536, 1536, 1024, 1024, Enumerable.Repeat((byte)1, 1024 * 1024).ToArray(), Delay: 5),
        new(1536, 1536, 1024, 1024, Enumerable.Repeat((byte)2, 1024 * 1024).ToArray(), Delay: 7),
        new(1536, 1536, 1024, 1024, Enumerable.Repeat((byte)3, 1024 * 1024).ToArray(), Disposal: 2, Delay: 11),
    ]);

    internal static void ExportValidated(string path, string name)
    {
        string? directory = Environment.GetEnvironmentVariable("MIV_FORMAT_FIXTURE_DIRECTORY");
        if (string.IsNullOrWhiteSpace(directory)) { return; }
        Directory.CreateDirectory(directory);
        File.Copy(path, Path.Combine(directory, name), overwrite: true);
        (string Name, string Basis)[] samples =
        [
            ("animation-infinite.gif", "4x2; 5 frames; GIF repeats=0 (infinite); transparent/offset/previous/background; 30ms each"),
            ("animation-large.gif", "4096x4096 (64MiB source); 3 frames; GIF repeats=0; centered 1024x1024 red/green/blue; 50/70/110ms"),
            ("animation-infinite.webp", "4x2; 3 frames; ANIM loops=0 (infinite); lossless red/SrcOver green/blue; 30/10/40ms"),
            ("animation-finite.webp", "4x2; 3 frames; ANIM loops=2 (two total plays); same lossless pixels as infinite WebP"),
        ];
        StringBuilder readme = new("# Validated Animation Codec Fixtures\n\n"
            + "Generated by AnimationCodecFixtures.cs; exported only after actual decoder pixel assertions pass.\n"
            + "GIF uses fixed palette literals with a clear code before each pixel. WebP uses SkiaSharp 4.153.1 lossless payloads and explicit RIFF/ANIM/ANMF headers.\n"
            + "GIF loop fields count repeats; WebP loop fields count total plays. Short 0/10ms delays are normalized to 100ms by the session.\n"
            + "The large playable GIF is below the 92MiB source-workspace admission bound. Output budgets do not bound all native/process memory.\n\n"
            + "| File | Bytes | SHA-256 | Generation Basis |\n|---|---:|---|---|\n");
        foreach (var sample in samples)
        {
            string exported = Path.Combine(directory, sample.Name);
            if (!File.Exists(exported)) { continue; }
            using FileStream file = File.OpenRead(exported);
            string hash = Convert.ToHexStringLower(SHA256.HashData(file));
            File.WriteAllText(exported + ".sha256", $"{hash}  {sample.Name}\n");
            readme.AppendLine(CultureInfo.InvariantCulture, $"| {sample.Name} | {file.Length} | {hash} | {sample.Basis} |");
        }
        File.WriteAllText(Path.Combine(directory, "animation-codec-README.md"), readme.ToString());
    }

    private static void U24(BinaryWriter writer, int value) =>
        writer.Write(new byte[] { (byte)value, (byte)(value >> 8), (byte)(value >> 16) });

    private static void Chunk(BinaryWriter writer, string type, byte[] data)
    {
        writer.Write(Encoding.ASCII.GetBytes(type));
        writer.Write(data.Length);
        writer.Write(data);
        if ((data.Length & 1) != 0) { writer.Write((byte)0); }
    }
}
