using System.Windows;
using System.Windows.Input;

namespace ModernImageViewer.UI.Controls;

public partial class ImageViewport
{
    private const double TouchInertiaDeceleration = 0.006; // DIP per millisecond squared.
    private int _touchContacts;

    private void OnTouchManipulationStarting(object sender, ManipulationStartingEventArgs e)
    {
        if (_disposed || _isUnloaded || Image is null)
        {
            e.Cancel();
            return;
        }

        _lastPointer = null;
        if (Canvas.IsMouseCaptured) { Canvas.ReleaseMouseCapture(); }
        Focus();
        e.ManipulationContainer = Canvas;
        e.Mode = ManipulationModes.Translate | ManipulationModes.Scale;
        _touchContacts = e.Manipulators.Count();
        e.Handled = true;
    }

    private void OnTouchManipulationDelta(object sender, ManipulationDeltaEventArgs e)
    {
        e.Handled = true;
        if (_disposed || _isUnloaded || Image is null)
        {
            e.Complete();
            return;
        }

        Vector translation = e.DeltaManipulation.Translation;
        int contacts = e.IsInertial ? 0 : e.Manipulators.Count();
        double scale = contacts >= 2 && contacts == _touchContacts ? e.DeltaManipulation.Scale.X : 1;
        _touchContacts = contacts;
        if (scale == 1 && translation == default) { return; }

        if (scale != 1)
        {
            // WPF reports the current center. Map the image point under the previous center to the current one.
            Point previousCenter = e.ManipulationOrigin - translation;
            _transform.ZoomAt(scale, previousCenter.X, previousCenter.Y);
        }
        if (translation != default) { _transform.Pan(translation.X, translation.Y); }
        NotifyTransformChanged();
    }

    private void OnTouchInertiaStarting(object sender, ManipulationInertiaStartingEventArgs e)
    {
        e.Handled = true;
        if (SystemParameters.ClientAreaAnimation)
        {
            e.TranslationBehavior.DesiredDeceleration = TouchInertiaDeceleration;
        }
    }

    private void OnTouchManipulationCompleted(object sender, ManipulationCompletedEventArgs e)
    {
        _touchContacts = 0;
        e.Handled = true;
    }

    internal void StopTouchManipulation()
    {
        if (IsManipulationEnabled && Manipulation.IsManipulationActive(this))
        {
            Manipulation.CompleteManipulation(this);
        }
    }
}
