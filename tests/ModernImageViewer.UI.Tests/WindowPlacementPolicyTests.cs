using System.Windows;

using ModernImageViewer.Application.Settings;

namespace ModernImageViewer.UI.Tests;

public sealed class WindowPlacementPolicyTests
{
    [Fact]
    public void RemovedDisplayClampsEntireWindowIntoCurrentWorkArea()
    {
        Rect restored = WindowPlacementPolicy.Restore(new(3500, -900, 1800, 1000, 2), new(0, 0, 1280, 720), 1);
        Assert.Equal(new Rect(0, 0, 1280, 720), restored);
    }

    [Fact]
    public void DpiChangeKeepsLogicalSizeAndReprojectsPhysicalOrigin()
    {
        Rect restored = WindowPlacementPolicy.Restore(new(-1000, 100, 800, 600, 1.5), new(-1920, 0, 1920, 1080), 1);
        Assert.Equal(new Rect(-1500, 150, 800, 600), restored);
    }
}
