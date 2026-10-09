using System.Text.Json;

using ModernImageViewer.Application.Observations;

namespace ModernImageViewer.Tests;

public sealed class BrowsingObservationTests
{
    private static readonly string[] ExportProperties = ["RequestId", "Phase", "SinceStartMs", "SinceRequestMs"];
    [Fact]
    public void PhasesUseOneClockAndEachRequestHasItsOwnOrigin()
    {
        double time = 10;
        BrowsingObservation observation = new(() => time);
        observation.WindowVisible();
        time = 25;
        observation.OpenRequested(1, false);
        time = 35;
        observation.FramePainted(1, false);
        time = 40;
        observation.NavigationAvailable(1);
        time = 55;
        observation.FramePainted(1, true);
        Assert.Equal(6, observation.Samples.Count);
        Assert.Null(observation.Samples[0].SinceRequestMs);
        Assert.Equal(10, observation.Samples[3].SinceRequestMs);
        Assert.Equal(30, observation.Samples[5].SinceRequestMs);
    }

    [Fact]
    public void SupersededFramesAndNavigationCannotCompleteCurrentRequest()
    {
        BrowsingObservation observation = new();
        observation.OpenRequested(4, false);
        observation.OpenRequested(5, true);
        observation.FramePainted(4, true);
        observation.NavigationAvailable(4);
        Assert.False(observation.HasPhase(BrowsingPhase.FirstRecognizablePainted));
        Assert.False(observation.HasPhase(BrowsingPhase.NavigationAvailable));
        observation.FramePainted(5, false);
        Assert.True(observation.HasPhase(BrowsingPhase.NeighborSwitchCompleted));
        Assert.False(observation.HasPhase(BrowsingPhase.RequiredDetailPainted));
    }

    [Fact]
    public void ReceivedTimeSurvivesInputFilteringAndMapsToItsGeneration()
    {
        double time = 10;
        BrowsingObservation observation = new(() => time);
        observation.RequestReceived();
        time = 20;
        observation.OpenRequested(10000, false);
        time = 30;
        observation.FramePainted(10000, true);
        Assert.Equal(10, observation.Samples[0].SinceStartMs);
        Assert.Equal(20, observation.Samples[2].SinceRequestMs);
        Assert.Equal(0, observation.DroppedRequests);
    }

    [Fact]
    public void LatestReceivedRequestWinsAndDetailDemandRequiresANewPaint()
    {
        double time = 10;
        BrowsingObservation observation = new(() => time);
        observation.RequestReceived();
        time = 15;
        observation.RequestReceived();
        time = 20;
        observation.OpenRequested(2, false);
        observation.FramePainted(2, true);
        Assert.Equal(15, observation.Samples[0].SinceStartMs);
        observation.DetailDemandRequested(2);
        Assert.False(observation.HasPhase(BrowsingPhase.RequiredDetailPainted));
        observation.FramePainted(1, true);
        Assert.False(observation.HasPhase(BrowsingPhase.RequiredDetailPainted));
        observation.FramePainted(2, true);
        Assert.True(observation.HasPhase(BrowsingPhase.RequiredDetailPainted));
        Assert.Equal(2, observation.Samples.Count(sample => sample.Phase == BrowsingPhase.RequiredDetailPainted));
    }

    [Fact]
    public void RepeatedPaintAndStateEventsDoNotDuplicatePhases()
    {
        BrowsingObservation observation = new();
        observation.WindowVisible();
        observation.WindowVisible();
        observation.OpenRequested(1, true);
        observation.OpenRequested(1, true);
        for (int index = 0; index < 20; index++)
        {
            observation.NavigationAvailable(1);
            observation.FramePainted(1, true);
        }
        Assert.Equal(7, observation.Samples.Count);
    }

    [Fact]
    public void BudgetOutcomeIsSeparateFromPaintedDetailAndARequestedDemandResetsIt()
    {
        BrowsingObservation observation = new();
        observation.OpenRequested(10, false);
        observation.FramePainted(10, false);
        observation.DetailBudgetLimited(9);
        Assert.False(observation.HasPhase(BrowsingPhase.DetailBudgetLimited));
        observation.DetailBudgetLimited(10);
        Assert.True(observation.HasPhase(BrowsingPhase.DetailBudgetLimited));
        Assert.False(observation.HasPhase(BrowsingPhase.RequiredDetailPainted));
        observation.DetailDemandRequested(10);
        Assert.False(observation.HasPhase(BrowsingPhase.DetailBudgetLimited));
    }

    [Fact]
    public void LongObservationHasAnExplicitBoundAndContinuesTrackingCurrentCompletion()
    {
        BrowsingObservation observation = new();
        for (int request = 1; request <= BrowsingObservation.MaximumRequests + 5; request++)
        {
            observation.OpenRequested(request, true);
            observation.NavigationAvailable(request);
            observation.FramePainted(request, true);
        }
        Assert.Equal(BrowsingObservation.MaximumRequests * 6, observation.Samples.Count);
        Assert.Equal(5, observation.DroppedRequests);
        Assert.True(observation.HasPhase(BrowsingPhase.RequiredDetailPainted));
    }

    [Fact]
    public void ExportShapeContainsOnlyGenerationPhaseAndDurations()
    {
        BrowsingObservation observation = new(() => 42);
        observation.OpenRequested(1, false);
        using JsonDocument json = JsonDocument.Parse(JsonSerializer.Serialize(observation.Samples));
        Assert.Equal(ExportProperties,
            json.RootElement[0].EnumerateObject().Select(property => property.Name));
    }

    [Fact]
    public void LoadingAndPreviewSeparateInputDecodeAndPaintWithoutAcceptingOldPixels()
    {
        double time = 10;
        BrowsingObservation observation = new(() => time);
        observation.RequestReceived();
        time = 30;
        observation.OpenRequested(2, false);
        time = 50;
        observation.PreviewPublished(1);
        Assert.False(observation.HasPhase(BrowsingPhase.PreviewPublished));
        time = 70;
        observation.PreviewPublished(2);
        observation.PreviewPublished(2);
        time = 90;
        observation.FramePainted(2, false);
        Assert.Equal(20, observation.Samples.Single(s => s.Phase == BrowsingPhase.LoadingPublished).SinceRequestMs);
        Assert.Equal(60, observation.Samples.Single(s => s.Phase == BrowsingPhase.PreviewPublished).SinceRequestMs);
        Assert.Equal(80, observation.Samples.Single(s => s.Phase == BrowsingPhase.FirstRecognizablePainted).SinceRequestMs);
    }
}
