namespace ModernImageViewer.Imaging;

/// <summary>File attributes observed while the decoder holds its read handle; not a content hash.</summary>
public sealed record ImageFileStamp(long Length, DateTime ModifiedUtc);
