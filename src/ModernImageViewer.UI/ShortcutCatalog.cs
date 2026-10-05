using System.Windows.Input;

using ModernImageViewer.UI.Localization;

namespace ModernImageViewer.UI;

public enum ViewerAction
{
    OpenImage,
    OpenFolder,
    PreviousImage,
    NextImage,
    FirstImage,
    LastImage,
    FitImage,
    ActualSize,
    ZoomIn,
    ZoomOut,
    RefreshFolder,
    ToggleSlideshow,
    ToggleInformation,
    ToggleFilmstrip,
    ToggleFullScreen,
    DismissOverlay,
    RevealInExplorer,
    ShowShortcutHelp,
    RotateRight,
    RotateLeft,
}

public readonly record struct ViewerShortcutGesture(Key Key, ModifierKeys Modifiers = ModifierKeys.None);

public sealed record ViewerShortcutDefinition(
    ViewerAction Action,
    string GroupResourceKey,
    string ActionResourceKey,
    IReadOnlyList<ViewerShortcutGesture> Gestures);

public sealed record ShortcutHelpRow(ViewerAction Action, string Description, string Keys);

public sealed record ShortcutHelpGroup(string Title, IReadOnlyList<ShortcutHelpRow> Rows);

/// <summary>Defines both keyboard matching and the localized help shown to users.</summary>
public static class ShortcutCatalog
{
    public static IReadOnlyList<ViewerShortcutDefinition> Definitions { get; } = Array.AsReadOnly<ViewerShortcutDefinition>(
    [
        Define(ViewerAction.OpenImage, "Open", "OpenImage", new ViewerShortcutGesture(Key.O, ModifierKeys.Control)),
        Define(ViewerAction.OpenFolder, "Open", "OpenFolder", new ViewerShortcutGesture(Key.O, ModifierKeys.Control | ModifierKeys.Shift)),
        Define(ViewerAction.PreviousImage, "Navigation", "PreviousImage", new ViewerShortcutGesture(Key.Left)),
        Define(ViewerAction.NextImage, "Navigation", "NextImage", new ViewerShortcutGesture(Key.Right)),
        Define(ViewerAction.FirstImage, "Navigation", "FirstImage", new ViewerShortcutGesture(Key.Home)),
        Define(ViewerAction.LastImage, "Navigation", "LastImage", new ViewerShortcutGesture(Key.End)),
        Define(ViewerAction.FitImage, "Canvas", "FitImage", new ViewerShortcutGesture(Key.D0), new ViewerShortcutGesture(Key.NumPad0)),
        Define(ViewerAction.ActualSize, "Canvas", "ActualSize", new ViewerShortcutGesture(Key.D1), new ViewerShortcutGesture(Key.NumPad1)),
        Define(ViewerAction.ZoomIn, "Canvas", "ZoomIn", new ViewerShortcutGesture(Key.OemPlus), new ViewerShortcutGesture(Key.Add), new ViewerShortcutGesture(Key.OemPlus, ModifierKeys.Shift)),
        Define(ViewerAction.ZoomOut, "Canvas", "ZoomOut", new ViewerShortcutGesture(Key.OemMinus), new ViewerShortcutGesture(Key.Subtract)),
        Define(ViewerAction.RotateRight, "Canvas", "RotateRight", new ViewerShortcutGesture(Key.R, ModifierKeys.Control)),
        Define(ViewerAction.RotateLeft, "Canvas", "RotateLeft", new ViewerShortcutGesture(Key.R, ModifierKeys.Control | ModifierKeys.Shift)),
        Define(ViewerAction.ToggleSlideshow, "Playback", "ToggleSlideshow", new ViewerShortcutGesture(Key.F6), new ViewerShortcutGesture(Key.Space)),
        Define(ViewerAction.ToggleInformation, "Window", "ToggleInformation", new ViewerShortcutGesture(Key.I, ModifierKeys.Control)),
        Define(ViewerAction.ToggleFilmstrip, "Window", "ToggleFilmstrip", new ViewerShortcutGesture(Key.T, ModifierKeys.Control)),
        Define(ViewerAction.ToggleFullScreen, "Window", "ToggleFullScreen", new ViewerShortcutGesture(Key.F11)),
        Define(ViewerAction.DismissOverlay, "Window", "DismissOverlay", new ViewerShortcutGesture(Key.Escape)),
        Define(ViewerAction.ShowShortcutHelp, "Window", "ShowShortcutHelp", new ViewerShortcutGesture(Key.F1)),
        Define(ViewerAction.RefreshFolder, "File", "RefreshFolder", new ViewerShortcutGesture(Key.F5)),
        Define(ViewerAction.RevealInExplorer, "File", "RevealInExplorer", new ViewerShortcutGesture(Key.E, ModifierKeys.Control | ModifierKeys.Shift)),
    ]);

    public static ViewerAction? Match(Key key, ModifierKeys modifiers)
    {
        foreach (ViewerShortcutDefinition definition in Definitions)
        {
            foreach (ViewerShortcutGesture gesture in definition.Gestures)
            {
                if (gesture.Key == key && gesture.Modifiers == modifiers)
                {
                    return definition.Action;
                }
            }
        }

        return null;
    }

    public static IReadOnlyList<ShortcutHelpGroup> CreateHelpGroups(ILocalizationService localization)
    {
        ArgumentNullException.ThrowIfNull(localization);
        return Definitions.GroupBy(definition => definition.GroupResourceKey)
            .Select(group => new ShortcutHelpGroup(localization.GetString(group.Key),
                Array.AsReadOnly(group.Select(definition => new ShortcutHelpRow(definition.Action,
                    localization.GetString(definition.ActionResourceKey),
                    string.Join(" / ", definition.Gestures.Select(gesture => FormatGesture(gesture, localization)))))
                    .ToArray())))
            .ToArray();
    }

    private static ViewerShortcutDefinition Define(ViewerAction action, string group, string label,
        params ViewerShortcutGesture[] gestures) =>
        new(action, $"Shortcut_Group_{group}", $"Shortcut_Action_{label}", Array.AsReadOnly(gestures));

    private static string FormatGesture(ViewerShortcutGesture gesture, ILocalizationService localization)
    {
        string key = gesture.Key switch
        {
            Key.D0 => "0",
            Key.D1 => "1",
            Key.OemPlus => "=",
            Key.OemMinus => "−",
            Key.Left => "←",
            Key.Right => "→",
            Key.NumPad0 => localization.GetString("Shortcut_Key_NumPad0"),
            Key.NumPad1 => localization.GetString("Shortcut_Key_NumPad1"),
            Key.Add => localization.GetString("Shortcut_Key_NumPadPlus"),
            Key.Subtract => localization.GetString("Shortcut_Key_NumPadMinus"),
            Key.Space => localization.GetString("Shortcut_Key_Space"),
            Key.Escape => "Esc",
            _ => gesture.Key.ToString(),
        };

        if (gesture.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            key = $"{localization.GetString("Shortcut_Key_Shift")} + {key}";
        }
        if (gesture.Modifiers.HasFlag(ModifierKeys.Control))
        {
            key = $"{localization.GetString("Shortcut_Key_Control")} + {key}";
        }
        return key;
    }
}
