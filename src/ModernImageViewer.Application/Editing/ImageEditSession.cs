using ModernImageViewer.Imaging;

namespace ModernImageViewer.Application.Editing;

public sealed class ImageEditSession
{
    public const int HistoryLimit = 64;
    private readonly List<ImageEditRecipe> _undo = [];
    private readonly Stack<ImageEditRecipe> _redo = [];
    private readonly ImageEditRecipe _initial;
    private ImageEditRecipe? _exported;

    public ImageEditSession(PixelSize source, ViewOrientation orientation = default) =>
        Current = _initial = ImageEditRecipe.Create(source, orientation);

    public ImageEditRecipe Current { get; private set; }
    public bool CanUndo => _undo.Count != 0;
    public bool CanRedo => _redo.Count != 0;
    public bool IsModified => Current != _initial;
    public bool HasUnexportedChanges => Current != (_exported ?? _initial);

    public void MarkExported() => _exported = Current;

    public void Apply(ImageEditRecipe recipe)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        if (recipe.SourceSize != _initial.SourceSize) { throw new ArgumentException("The source size cannot change.", nameof(recipe)); }
        if (recipe == Current) { return; }
        if (_undo.Count == HistoryLimit) { _undo.RemoveAt(0); }
        _undo.Add(Current);
        _redo.Clear();
        Current = recipe;
    }

    public void Undo()
    {
        if (!CanUndo) { return; }
        _redo.Push(Current);
        Current = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
    }

    public void Redo()
    {
        if (!CanRedo) { return; }
        _undo.Add(Current);
        Current = _redo.Pop();
    }

    public void Reset() => Apply(_initial);
}
