using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

using ModernImageViewer.UI.Controls;

namespace ModernImageViewer.UI.Tests;

internal static class TouchViewportRegression
{
    public static void Verify(MainWindow window)
    {
        ImageViewport viewport = (ImageViewport)window.FindName("Viewport");
        Assert.True(viewport.IsManipulationEnabled);
        viewport.Fit();
        TouchContact first = new(1, new Point(100, 110));
        TouchContact second = new(2, new Point(200, 110));
        int starts = 0;
        int contacts = 0;
        int inertialDeltas = 0;
        int completions = 0;
        void OnStarted(object? sender, ManipulationStartedEventArgs e) => starts++;
        void OnDelta(object? sender, ManipulationDeltaEventArgs e)
        {
            if (e.IsInertial) { inertialDeltas++; }
            else { contacts = e.Manipulators.Count(); }
        }
        void OnCompleted(object? sender, ManipulationCompletedEventArgs e) => completions++;
        // The viewport handles deltas; use the same routed events even when they are handled.
        viewport.ManipulationStarted += OnStarted;
        viewport.AddHandler(UIElement.ManipulationDeltaEvent, new EventHandler<ManipulationDeltaEventArgs>(OnDelta), handledEventsToo: true);
        viewport.AddHandler(UIElement.ManipulationCompletedEvent, new EventHandler<ManipulationCompletedEventArgs>(OnCompleted), handledEventsToo: true);
        try
        {
            var anchor = viewport.ToSourcePoint(new Point(150, 110));
            var neighbor = viewport.ToSourcePoint(new Point(160, 110));
            Manipulation.AddManipulator(viewport, first);
            Manipulation.AddManipulator(viewport, second);
            WaitFor(() => starts == 1, "two touch contacts");

            first.Position = new Point(80, 140);
            second.MoveTo(new Point(260, 140));
            WaitFor(() => viewport.ToCanvasPoint(neighbor.X, neighbor.Y).X - viewport.ToCanvasPoint(anchor.X, anchor.Y).X > 14,
                "pinch scaling");
            AssertPoint(new Point(170, 140), viewport.ToCanvasPoint(anchor.X, anchor.Y));

            first.Position = new Point(120, 140);
            second.MoveTo(new Point(220, 140));
            WaitFor(() => viewport.ToCanvasPoint(neighbor.X, neighbor.Y).X - viewport.ToCanvasPoint(anchor.X, anchor.Y).X < 12,
                "pinch zooming out");
            AssertPoint(new Point(170, 140), viewport.ToCanvasPoint(anchor.X, anchor.Y));

            Manipulation.RemoveManipulator(viewport, second);
            // Let WPF rebase the gesture to the remaining contact.
            WaitFor(() => contacts == 1, "one remaining touch contact");
            Point finger = first.Position;
            var dragged = viewport.ToSourcePoint(finger);
            for (int i = 0; i < 4; i++)
            {
                finger += new Vector(20, 10);
                first.MoveTo(finger);
                WaitFor(() => (viewport.ToCanvasPoint(dragged.X, dragged.Y) - finger).Length < 1, "single-finger pan");
            }
            Point atRelease = viewport.ToCanvasPoint(dragged.X, dragged.Y);
            double span = (viewport.ToCanvasPoint(neighbor.X, neighbor.Y) - viewport.ToCanvasPoint(anchor.X, anchor.Y)).Length;
            Manipulation.RemoveManipulator(viewport, first);
            if (SystemParameters.ClientAreaAnimation)
            {
                WaitFor(() => inertialDeltas > 0 && viewport.ToCanvasPoint(dragged.X, dragged.Y).X > atRelease.X + 1,
                    "inertial pan after release");
                double inertialSpan = (viewport.ToCanvasPoint(neighbor.X, neighbor.Y) - viewport.ToCanvasPoint(anchor.X, anchor.Y)).Length;
                Assert.Equal(span, inertialSpan, precision: 5);
            }
            viewport.Fit();
            WaitFor(() => completions > 0 && !Manipulation.IsManipulationActive(viewport), "gesture completion");
        }
        finally
        {
            viewport.StopTouchManipulation();
            viewport.ManipulationStarted -= OnStarted;
            viewport.RemoveHandler(UIElement.ManipulationDeltaEvent, new EventHandler<ManipulationDeltaEventArgs>(OnDelta));
            viewport.RemoveHandler(UIElement.ManipulationCompletedEvent, new EventHandler<ManipulationCompletedEventArgs>(OnCompleted));
            viewport.Fit();
        }
    }

    private static void AssertPoint(Point expected, Point actual) =>
        Assert.InRange((actual - expected).Length, 0, 1);

    private static void WaitFor(Func<bool> condition, string operation)
    {
        if (condition()) { return; }
        Stopwatch elapsed = Stopwatch.StartNew();
        DispatcherFrame frame = new();
        DispatcherTimer timer = new(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) =>
        {
            if (condition() || elapsed.Elapsed > TimeSpan.FromSeconds(2)) { frame.Continue = false; }
        };
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        Assert.True(condition(), operation);
    }

    private sealed class TouchContact(int id, Point position) : IManipulator
    {
        public int Id => id;
        public Point Position { get; set; } = position;
        public event EventHandler? Updated;
        public Point GetPosition(IInputElement relativeTo) => Position;
        public void ManipulationEnded(bool cancel) { }
        public void MoveTo(Point next)
        {
            Position = next;
            Updated?.Invoke(this, EventArgs.Empty);
        }
    }
}
