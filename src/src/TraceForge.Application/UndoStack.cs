using TraceForge.Domain;
namespace TraceForge.Application;

/// <summary>One undoable user action. Undo and Redo apply directly to the store (they never register new entries).
/// <paramref name="RefreshTree"/> marks an action on the module tree (rename, move, delete, restore): <paramref name="ModuleId"/> is then the folder or module concerned and the tree must be reloaded.</summary>
public sealed record UndoEntry(string Description, ObjectId? ModuleId, ObjectId? SelectId, Func<Task> Undo, Func<Task> Redo, bool RefreshTree = false);

/// <summary>In-session undo/redo history. Last in, first out; a new action clears the redo history.</summary>
public sealed class UndoStack(int limit = 100)
{
    private readonly List<UndoEntry> _undo = [];
    private readonly List<UndoEntry> _redo = [];

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string? UndoText => _undo.Count > 0 ? _undo[^1].Description : null;
    public string? RedoText => _redo.Count > 0 ? _redo[^1].Description : null;

    public void Push(UndoEntry entry)
    {
        _undo.Add(entry);
        if (_undo.Count > limit) _undo.RemoveAt(0);
        _redo.Clear();
    }

    public void Clear() { _undo.Clear(); _redo.Clear(); }

    /// <summary>Undoes the latest action. If the undo throws, the entry is dropped (the project changed underneath it) and the exception propagates.</summary>
    public async Task<UndoEntry?> UndoAsync()
    {
        if (_undo.Count == 0) return null;
        var entry = _undo[^1]; _undo.RemoveAt(_undo.Count - 1);
        await entry.Undo();
        _redo.Add(entry);
        return entry;
    }

    public async Task<UndoEntry?> RedoAsync()
    {
        if (_redo.Count == 0) return null;
        var entry = _redo[^1]; _redo.RemoveAt(_redo.Count - 1);
        await entry.Redo();
        _undo.Add(entry);
        return entry;
    }
}
