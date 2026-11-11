namespace Escapade.Core;

/// <summary>
/// App-wide undo and redo stacks. Entries are taken off while they run and put back afterwards:
/// on the opposite stack if they worked, or where they came from if they failed, so a partly
/// failed undo can be retried.
/// </summary>
internal sealed class UndoHistory<T>(int capacity = 100) where T : class
{
    private readonly List<T> _undo = []; // oldest first
    private readonly List<T> _redo = []; // oldest first

    public event Action? Changed;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>Newest first, as an undo dropdown lists them.</summary>
    public IReadOnlyList<T> UndoItems => Enumerable.Reverse(_undo).ToList();

    /// <summary>Next to redo first.</summary>
    public IReadOnlyList<T> RedoItems => Enumerable.Reverse(_redo).ToList();

    /// <summary>A new action: it becomes the next thing to undo, and the redo stack is discarded.</summary>
    public void Record(T item)
    {
        _redo.Clear();
        PutUndo(item);
    }

    public T? TakeUndo() => Take(_undo);

    public T? TakeRedo() => Take(_redo);

    /// <summary>Puts an entry back on the undo stack without touching redo (after a redo, or a failed undo).</summary>
    public void PutUndo(T item)
    {
        _undo.Add(item);
        if (_undo.Count > capacity)
            _undo.RemoveAt(0);
        Changed?.Invoke();
    }

    /// <summary>Puts an entry on the redo stack (after an undo, or a failed redo).</summary>
    public void PutRedo(T item)
    {
        _redo.Add(item);
        if (_redo.Count > capacity)
            _redo.RemoveAt(0);
        Changed?.Invoke();
    }

    private T? Take(List<T> stack)
    {
        if (stack.Count == 0)
            return null;
        var item = stack[^1];
        stack.RemoveAt(stack.Count - 1);
        Changed?.Invoke();
        return item;
    }
}
