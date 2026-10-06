using ModernImageViewer.Application.Images;
using ModernImageViewer.Imaging;

namespace ModernImageViewer.Tests;

public sealed class FramePlaybackTests
{
    [Fact]
    public void StartsPausedAndDefaultClockCanStartWithoutWaiting()
    {
        ImageSequenceInfo info = Animation(1, 100, 200);
        FramePlaybackController controller = new(info);
        Assert.Same(info, controller.Info);
        Assert.Equal(0, controller.Index);
        Assert.Equal(0, controller.ElapsedMilliseconds);
        Assert.False(controller.IsPlaying);
        Assert.False(controller.IsEnded);
        Assert.False(controller.IsSuspended);
        Assert.Null(controller.NextDueMilliseconds);
        controller.Play();
        Assert.True(controller.IsPlaying);
        Assert.Equal(100, controller.NextDueMilliseconds);
    }

    [Theory]
    [InlineData(0, 0, 100)]
    [InlineData(99, 0, 1)]
    [InlineData(100, 1, 200)]
    [InlineData(299, 1, 1)]
    [InlineData(300, 2, 50)]
    [InlineData(349, 2, 1)]
    [InlineData(350, 0, 100)]
    [InlineData(799, 0, 1)]
    public void InfinitePlaybackUsesTimeAndExactFrameBoundaries(long elapsed, int index, long due)
    {
        FakeClock clock = new() { Now = 12_000 };
        FramePlaybackController controller = new(Animation(null, 100, 200, 50), clock.Read);
        controller.Play();
        clock.Now += elapsed;
        Assert.Equal(index, controller.GetTargetFrame());
        Assert.Equal(elapsed, controller.ElapsedMilliseconds);
        Assert.Equal(due, controller.NextDueMilliseconds);
        Assert.True(controller.IsPlaying);
        Assert.False(controller.IsEnded);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    public void FinitePlaybackStopsAfterLastFrameDelayAndRequiresRestart(int plays)
    {
        FakeClock clock = new();
        FramePlaybackController controller = new(Animation(plays, 100, 200, 50), clock.Read);
        controller.Play();
        clock.Now = 350L * plays - 1;
        Assert.Equal(2, controller.GetTargetFrame());
        Assert.True(controller.IsPlaying);
        Assert.False(controller.IsEnded);
        Assert.Equal(1, controller.NextDueMilliseconds);
        clock.Now++;
        Assert.Equal(2, controller.GetTargetFrame());
        Assert.True(controller.IsEnded);
        Assert.False(controller.IsPlaying);
        Assert.Equal(350L * plays, controller.ElapsedMilliseconds);
        Assert.Null(controller.NextDueMilliseconds);
        clock.Now = long.MaxValue;
        controller.Play();
        Assert.Equal(2, controller.GetTargetFrame());
        Assert.True(controller.IsEnded);
        Assert.False(controller.IsPlaying);
        controller.Restart();
        Assert.Equal(0, controller.Index);
        Assert.Equal(0, controller.ElapsedMilliseconds);
        Assert.True(controller.IsPlaying);
        Assert.False(controller.IsEnded);
        Assert.Equal(100, controller.NextDueMilliseconds);
    }

    [Fact]
    public void PauseCapturesUnpolledTimeAndResumeKeepsRemainingDelay()
    {
        FakeClock clock = new();
        FramePlaybackController controller = new(Animation(2, 100, 200), clock.Read);
        controller.Play();
        clock.Now = 125;
        controller.Pause();
        Assert.Equal(1, controller.Index);
        Assert.Equal(125, controller.ElapsedMilliseconds);
        Assert.False(controller.IsPlaying);
        clock.Now = 50_000;
        Assert.Equal(1, controller.GetTargetFrame());
        controller.Pause();
        Assert.Equal(125, controller.ElapsedMilliseconds);
        controller.Play();
        Assert.Equal(175, controller.NextDueMilliseconds);
        clock.Now += 174;
        Assert.Equal(1, controller.GetTargetFrame());
        clock.Now++;
        Assert.Equal(0, controller.GetTargetFrame());
        Assert.Equal(300, controller.ElapsedMilliseconds);
        controller.Play();
        clock.Now += 100;
        Assert.Equal(1, controller.GetTargetFrame());
        Assert.Equal(400, controller.ElapsedMilliseconds);
    }

    [Fact]
    public void SeekPausesAndResumesFromSelectedFrameInFirstPlay()
    {
        FakeClock clock = new();
        FramePlaybackController controller = new(Animation(2, 100, 200, 50), clock.Read);
        controller.Play();
        clock.Now = 650;
        controller.GetTargetFrame();
        controller.Seek(1);
        Assert.Equal(1, controller.Index);
        Assert.Equal(100, controller.ElapsedMilliseconds);
        Assert.False(controller.IsPlaying);
        Assert.False(controller.IsEnded);
        clock.Now = 20_000;
        Assert.Equal(1, controller.GetTargetFrame());
        controller.Play();
        Assert.Equal(200, controller.NextDueMilliseconds);
        clock.Now += 200;
        Assert.Equal(2, controller.GetTargetFrame());
        clock.Now += 400;
        Assert.Equal(2, controller.GetTargetFrame());
        Assert.True(controller.IsEnded);
        controller.Seek(2);
        Assert.False(controller.IsEnded);
        Assert.Equal(300, controller.ElapsedMilliseconds);
        controller.Play();
        Assert.Equal(50, controller.NextDueMilliseconds);
        controller.Seek(0);
        Assert.Equal(0, controller.GetTargetFrame());
        Assert.Equal(0, controller.ElapsedMilliseconds);
        Assert.False(controller.IsPlaying);
    }

    [Fact]
    public void SuspensionFreezesTimeAndVisibilityResumeDoesNotCatchUp()
    {
        FakeClock clock = new();
        FramePlaybackController controller = new(Animation(1, 100, 200), clock.Read);
        controller.Play();
        clock.Now = 75;
        controller.SetSuspended(true);
        Assert.True(controller.IsPlaying);
        Assert.True(controller.IsSuspended);
        Assert.Equal(75, controller.ElapsedMilliseconds);
        Assert.Null(controller.NextDueMilliseconds);
        clock.Now = 10_000;
        Assert.Equal(0, controller.GetTargetFrame());
        controller.SetSuspended(true);
        controller.SetSuspended(false);
        Assert.Equal(25, controller.NextDueMilliseconds);
        clock.Now += 25;
        Assert.Equal(1, controller.GetTargetFrame());
        Assert.Equal(100, controller.ElapsedMilliseconds);
        Assert.False(controller.IsEnded);
    }

    [Fact]
    public void UserPauseAndSuspensionRemainIndependentIncludingSeekAndRestart()
    {
        FakeClock clock = new();
        FramePlaybackController controller = new(Animation(null, 100, 200), clock.Read);
        controller.Play();
        controller.SetSuspended(true);
        controller.Pause();
        clock.Now = 1_000;
        controller.SetSuspended(false);
        Assert.False(controller.IsPlaying);
        Assert.Equal(0, controller.ElapsedMilliseconds);
        controller.SetSuspended(true);
        controller.Play();
        clock.Now = 2_000;
        Assert.Equal(0, controller.GetTargetFrame());
        controller.Seek(1);
        controller.SetSuspended(false);
        Assert.False(controller.IsPlaying);
        Assert.Equal(1, controller.GetTargetFrame());
        controller.SetSuspended(true);
        controller.Restart();
        Assert.True(controller.IsPlaying);
        Assert.True(controller.IsSuspended);
        Assert.Equal(0, controller.Index);
        Assert.Equal(0, controller.ElapsedMilliseconds);
        clock.Now = 3_000;
        controller.SetSuspended(false);
        clock.Now += 100;
        Assert.Equal(1, controller.GetTargetFrame());
        Assert.Equal(100, controller.ElapsedMilliseconds);
        controller.Pause();
        controller.SetSuspended(true);
        controller.SetSuspended(false);
        Assert.False(controller.IsPlaying);
    }

    [Fact]
    public void PagesNavigateWithoutReadingClockOrPlaying()
    {
        ImageSequenceInfo pages = Animation(1, 0, 0, 0) with { Kind = ImageSequenceKind.Pages };
        FramePlaybackController controller = new(pages, () => throw new InvalidOperationException("Pages must not read time."));
        controller.Play();
        controller.Seek(2);
        Assert.Equal(2, controller.GetTargetFrame());
        Assert.False(controller.IsPlaying);
        Assert.False(controller.IsEnded);
        Assert.Equal(0, controller.ElapsedMilliseconds);
        Assert.Null(controller.NextDueMilliseconds);
        controller.SetSuspended(true);
        controller.Play();
        controller.SetSuspended(false);
        controller.Restart();
        Assert.Equal(0, controller.Index);
        Assert.False(controller.IsPlaying);
        controller.Pause();
        Assert.False(controller.IsEnded);
    }

    [Fact]
    public void ChangedReportsOnlyFinalFrameAndPlaybackStateNotElapsedTicks()
    {
        FakeClock clock = new();
        FramePlaybackController controller = new(Animation(1, 100, 200, 50), clock.Read);
        List<(int Index, bool Playing, bool Ended, bool Suspended)> states = [];
        controller.Changed += (sender, args) =>
        {
            Assert.Same(controller, sender);
            Assert.Same(EventArgs.Empty, args);
            states.Add((controller.Index, controller.IsPlaying, controller.IsEnded, controller.IsSuspended));
        };
        controller.Pause();
        controller.GetTargetFrame();
        Assert.Empty(states);
        controller.Play();
        controller.Play();
        clock.Now = 50;
        controller.GetTargetFrame();
        Assert.Single(states);
        clock.Now = 310;
        Assert.Equal(2, controller.GetTargetFrame());
        Assert.Equal(2, states.Count);
        Assert.Equal((2, true, false, false), states[^1]);
        clock.Now = 350;
        controller.GetTargetFrame();
        Assert.Equal((2, false, true, false), states[^1]);
        Assert.Equal(3, states.Count);
        controller.Pause();
        controller.Play();
        controller.GetTargetFrame();
        Assert.Equal(3, states.Count);
        controller.Seek(2);
        Assert.Equal((2, false, false, false), states[^1]);
        controller.Seek(2);
        Assert.Equal(4, states.Count);
        controller.SetSuspended(true);
        controller.SetSuspended(true);
        Assert.Equal((2, false, false, true), states[^1]);
        Assert.Equal(5, states.Count);
    }

    [Fact]
    public void LargeJumpSkipsIntermediateFramesWithoutIntermediateNotifications()
    {
        FakeClock clock = new();
        FramePlaybackController controller = new(Animation(null, 2, 3, 5), clock.Read);
        controller.Play();
        int changes = 0;
        controller.Changed += (_, _) => changes++;
        clock.Now = 1_000_000_004;
        Assert.Equal(1, controller.GetTargetFrame());
        Assert.Equal(1, controller.NextDueMilliseconds);
        Assert.Equal(1, changes);
        Assert.Equal(1, controller.GetTargetFrame());
        Assert.Equal(1, changes);
    }

    [Fact]
    public void InfinitePhaseSurvivesClockSubtractionAndAccumulatedLongOverflow()
    {
        FakeClock clock = new() { Now = long.MinValue };
        FramePlaybackController controller = new(Animation(null, 2, 3, 5), clock.Read);
        controller.Play();
        clock.Now = -1;
        Assert.Equal(2, controller.GetTargetFrame());
        Assert.Equal(long.MaxValue, controller.ElapsedMilliseconds);
        Assert.Equal(3, controller.NextDueMilliseconds);
        clock.Now = 0;
        Assert.Equal(2, controller.GetTargetFrame());
        Assert.Equal(long.MaxValue, controller.ElapsedMilliseconds);
        Assert.Equal(2, controller.NextDueMilliseconds);
        clock.Now = long.MaxValue;
        Assert.Equal(2, controller.GetTargetFrame());
        Assert.Equal(5, controller.NextDueMilliseconds);
        controller.Restart();
        Assert.Equal(0, controller.ElapsedMilliseconds);
        Assert.Equal(0, controller.Index);
        Assert.Equal(2, controller.NextDueMilliseconds);
    }

    [Fact]
    public void FiniteLargeJumpClampsTimeAndPreservesLastFrame()
    {
        FakeClock clock = new() { Now = long.MinValue };
        FramePlaybackController controller = new(Animation(2, int.MaxValue, int.MaxValue), clock.Read);
        controller.Play();
        clock.Now = long.MaxValue;
        Assert.Equal(1, controller.GetTargetFrame());
        Assert.Equal(4L * int.MaxValue, controller.ElapsedMilliseconds);
        Assert.True(controller.IsEnded);
        Assert.Null(controller.NextDueMilliseconds);
    }

    [Fact]
    public void RejectsBackwardClockWithoutCorruptingPlaybackState()
    {
        FakeClock clock = new() { Now = 100 };
        FramePlaybackController controller = new(Animation(null, 10, 20), clock.Read);
        controller.Play();
        clock.Now = 99;
        Assert.Throws<InvalidOperationException>(() => controller.GetTargetFrame());
        Assert.True(controller.IsPlaying);
        Assert.Equal(0, controller.ElapsedMilliseconds);
        clock.Now = 110;
        Assert.Equal(1, controller.GetTargetFrame());
        Assert.Equal(10, controller.ElapsedMilliseconds);
    }

    [Fact]
    public void ValidatesMetadataAndTotalPlaybackOverflowExplicitly()
    {
        Assert.Throws<ArgumentNullException>(() => new FramePlaybackController(null!));
        Assert.Throws<ArgumentException>(() => new FramePlaybackController(Animation(1)));
        Assert.Throws<ArgumentException>(() => new FramePlaybackController(Animation(1, 10) with { Frames = null! }));
        Assert.Throws<ArgumentException>(() => new FramePlaybackController(Animation(1, 10) with { Frames = [null!] }));
        Assert.Throws<ArgumentException>(() => new FramePlaybackController(Animation(1, 10) with { Kind = ImageSequenceKind.Static }));
        Assert.Throws<ArgumentException>(() => new FramePlaybackController(Animation(1, 10) with { Kind = (ImageSequenceKind)99 }));
        ImageSequenceInfo invalidIndex = Animation(1, 10);
        Assert.Throws<ArgumentException>(() => new FramePlaybackController(invalidIndex with { Frames = [invalidIndex.Frames[0] with { Index = 1 }] }));
        Assert.Throws<OverflowException>(() => new FramePlaybackController(Animation(int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue)));
        FramePlaybackController wideDuration = new(Animation(1, int.MaxValue, int.MaxValue));
        wideDuration.Seek(1);
        Assert.Equal((long)int.MaxValue, wideDuration.ElapsedMilliseconds);
        wideDuration.Play();
        Assert.Equal((long)int.MaxValue, wideDuration.NextDueMilliseconds);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void RejectsInvalidEffectiveDelaysAndPlayCounts(int value)
    {
        Assert.Throws<ArgumentException>(() => new FramePlaybackController(Animation(1, 10, value)));
        Assert.Throws<ArgumentException>(() => new FramePlaybackController(Animation(value, 10)));
    }

    [Fact]
    public void OneMillisecondIsValidAndRawDelayDoesNotDriveClock()
    {
        FakeClock clock = new();
        ImageSequenceInfo info = Animation(1, 1, 1);
        FramePlaybackController controller = new(info with
        {
            Frames = info.Frames.Select(frame => frame with { RawDurationMilliseconds = 0 }).ToArray()
        }, clock.Read);
        controller.Play();
        clock.Now = 1;
        Assert.Equal(1, controller.GetTargetFrame());
        Assert.Equal(1, controller.NextDueMilliseconds);
        clock.Now = 2;
        Assert.Equal(1, controller.GetTargetFrame());
        Assert.True(controller.IsEnded);
    }

    [Fact]
    public void InvalidSeekLeavesRunningStateUntouched()
    {
        FakeClock clock = new();
        FramePlaybackController controller = new(Animation(1, 100, 200), clock.Read);
        controller.Play();
        clock.Now = 125;
        controller.GetTargetFrame();
        Assert.Throws<ArgumentOutOfRangeException>(() => controller.Seek(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => controller.Seek(2));
        Assert.Throws<ArgumentOutOfRangeException>(() => controller.Seek(int.MaxValue));
        Assert.True(controller.IsPlaying);
        Assert.Equal(1, controller.Index);
        Assert.Equal(125, controller.ElapsedMilliseconds);
        Assert.Equal(175, controller.NextDueMilliseconds);
    }

    private static ImageSequenceInfo Animation(int? plays, params int[] delays) => new(
        ImageSequenceKind.Animation,
        delays.Select((delay, index) => new ImageFrameInfo(index, new(1, 1), new(0, 0, 1, 1),
            RawDurationMilliseconds: delay, DurationMilliseconds: delay)).ToArray(), plays);

    private sealed class FakeClock
    {
        public long Now { get; set; }
        public long Read() => Now;
    }
}
