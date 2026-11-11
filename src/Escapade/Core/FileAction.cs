namespace Escapade.Core;

internal enum ActionKind
{
    Copy,
    Move,
    Rename,
    NewFolder,
    Recycle,
}

/// <summary>
/// One top-level item an operation changed. <see cref="Source"/> is where it was (empty for a new
/// folder). <see cref="Result"/> is where it is now, or for <see cref="ActionKind.Recycle"/> the
/// item's path inside the Recycle Bin.
/// </summary>
internal sealed record ItemChange(string Source, string Result);

internal enum UndoStepKind
{
    /// <summary>Send each <see cref="ItemChange.Result"/> to the Recycle Bin (undoes copy, new folder).</summary>
    Recycle,
    /// <summary>Move each Result back to Source's folder under Source's name (undoes move).</summary>
    MoveBack,
    /// <summary>Rename each Result back to Source's name (undoes rename).</summary>
    RenameBack,
    /// <summary>Restore each Result from the Recycle Bin (undoes delete).</summary>
    Restore,
}

/// <param name="Missing">Recorded items that can no longer be undone because they've gone.</param>
internal sealed record UndoPlan(UndoStepKind Kind, IReadOnlyList<ItemChange> Items, int Missing);

/// <summary>
/// A user file operation, recorded for undo and redo. Holds only path strings; the shell work is
/// done elsewhere. <see cref="Sources"/> and <see cref="Target"/> are the request, which redo
/// repeats; <see cref="Changes"/> is what actually happened last time it ran, which undo reverses.
/// </summary>
internal sealed class FileAction
{
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

    private FileAction(ActionKind kind, IReadOnlyList<string> sources, string target)
    {
        Kind = kind;
        Sources = sources;
        Target = target;
    }

    public static FileAction Copy(IReadOnlyList<string> sources, string destinationFolder) =>
        new(ActionKind.Copy, sources, destinationFolder);

    public static FileAction Move(IReadOnlyList<string> sources, string destinationFolder) =>
        new(ActionKind.Move, sources, destinationFolder);

    public static FileAction Rename(string path, string newName) => new(ActionKind.Rename, [path], newName);

    public static FileAction NewFolder(string parentFolder, string name) => new(ActionKind.NewFolder, [parentFolder], name);

    public static FileAction Recycle(IReadOnlyList<string> paths) => new(ActionKind.Recycle, paths, "");

    public ActionKind Kind { get; }

    /// <summary>Copy/Move/Recycle: the items. Rename: the one item. NewFolder: the parent folder.</summary>
    public IReadOnlyList<string> Sources { get; }

    /// <summary>Copy/Move: destination folder. Rename: new name. NewFolder: folder name. Recycle: unused.</summary>
    public string Target { get; }

    public IReadOnlyList<ItemChange> Changes { get; private set; } = [];

    /// <summary>Part of what happened can't be undone (merged into or replaced an existing item, or deleted outright).</summary>
    public bool Partial { get; private set; }

    public bool HasChanges => Changes.Count > 0;

    public string Description => Kind switch
    {
        ActionKind.Copy => $"Copy {Subject()} to {PathUtil.ShortName(Target)}",
        ActionKind.Move => $"Move {Subject()} to {PathUtil.ShortName(Target)}",
        ActionKind.Rename => $"Rename “{Path.GetFileName(Sources[0])}” to “{Target}”",
        ActionKind.NewFolder => $"New folder “{(Changes.Count > 0 ? Path.GetFileName(Changes[0].Result) : Target)}”",
        _ => $"Delete {Subject()}",
    };

    /// <summary>Folders whose listings change when this action is done or undone.</summary>
    public IReadOnlyList<string> AffectedFolders
    {
        get
        {
            var folders = new List<string>();
            if (Kind is ActionKind.Copy or ActionKind.Move)
                folders.Add(Target);
            if (Kind == ActionKind.NewFolder)
                folders.Add(Sources[0]);
            else if (Kind != ActionKind.Copy)
                folders.AddRange(Sources.Select(s => PathUtil.GetParent(s) ?? PathUtil.ComputerRoot));
            return folders.Distinct(PathComparer).ToList();
        }
    }

    /// <summary>
    /// Destination paths that already exist before a copy or move runs. If the operation then
    /// reports one of these as its result, it merged into (or replaced) something that was
    /// already there, and undoing it would remove the user's existing item.
    /// </summary>
    public HashSet<string> ExistingDestinations(Func<string, bool> exists)
    {
        var existing = new HashSet<string>(PathComparer);
        if (Kind is not (ActionKind.Copy or ActionKind.Move))
            return existing;
        foreach (var source in Sources)
        {
            var destination = Path.Join(Target, Path.GetFileName(source));
            if (exists(destination))
                existing.Add(destination);
        }
        return existing;
    }

    /// <summary>
    /// Records what the shell reported doing. Keeps only the top-level items (the shell also
    /// reports every file inside a copied folder), and drops anything that can't be safely undone.
    /// </summary>
    /// <param name="preexisting">From <see cref="ExistingDestinations"/>, captured before the operation.</param>
    public void Record(IEnumerable<ItemChange> reported, IReadOnlySet<string>? preexisting = null)
    {
        var requested = new HashSet<string>(Sources, PathComparer);
        var kept = new Dictionary<string, ItemChange>(PathComparer); // by Source, or Result for new folders
        bool partial = false;

        foreach (var change in reported)
        {
            switch (Kind)
            {
                case ActionKind.NewFolder:
                    if (change.Result.Length > 0 && PathUtil.SameLocation(PathUtil.GetParent(change.Result), Sources[0]))
                        kept[change.Result] = change with { Source = "" };
                    continue;

                case ActionKind.Recycle:
                    if (!requested.Contains(change.Source))
                        continue;
                    if (change.Result.Length == 0)
                        partial = true; // too big for the Recycle Bin, so it was deleted outright
                    else
                        kept[change.Source] = change;
                    continue;

                default:
                    if (!requested.Contains(change.Source) || change.Result.Length == 0)
                        continue;
                    if (preexisting?.Contains(change.Result) == true)
                    {
                        partial = true;
                        continue;
                    }
                    kept[change.Source] = change;
                    continue;
            }
        }

        Changes = kept.Values.ToList();
        Partial = partial;
    }

    /// <summary>What undoing this would do right now, skipping recorded items that have since gone.</summary>
    public UndoPlan PlanUndo(Func<string, bool> exists)
    {
        List<ItemChange> items;
        UndoStepKind kind;
        switch (Kind)
        {
            case ActionKind.Copy:
            case ActionKind.NewFolder:
                kind = UndoStepKind.Recycle;
                items = Changes.Where(c => exists(c.Result)).ToList();
                break;
            case ActionKind.Move:
                kind = UndoStepKind.MoveBack;
                items = Changes.Where(c => exists(c.Result) && PathUtil.GetParent(c.Source) is { } parent && exists(parent)).ToList();
                break;
            case ActionKind.Rename:
                kind = UndoStepKind.RenameBack;
                items = Changes.Where(c => exists(c.Result)).ToList();
                break;
            default:
                // Whether each item is still in the Recycle Bin is only known by looking in it.
                kind = UndoStepKind.Restore;
                items = Changes.ToList();
                break;
        }
        return new UndoPlan(kind, items, Changes.Count - items.Count);
    }

    /// <summary>The request's inputs that still exist, for redo.</summary>
    public IReadOnlyList<string> RedoSources(Func<string, bool> exists) =>
        Sources.Where(exists).ToList();

    private string Subject() => Sources.Count == 1 ? $"“{Path.GetFileName(Sources[0])}”" : $"{Sources.Count} items";
}
