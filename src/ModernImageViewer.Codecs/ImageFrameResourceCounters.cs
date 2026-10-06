using ModernImageViewer.Codecs.Frames;

namespace ModernImageViewer.Codecs;

/// <summary>Live frame-session resources, including disposal awaiting native completion.</summary>
public static class ImageFrameResourceCounters
{
    public static int ActiveSessionCount => SkiaImageFrameSession.ActiveSessionCount + TiffImageFrameSession.ActiveSessionCount;
    public static int ActiveDecoderCount => SkiaImageFrameSession.ActiveDecoderCount + TiffImageFrameSession.ActiveContextCount;
}
