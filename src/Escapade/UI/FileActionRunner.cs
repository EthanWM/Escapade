using Escapade.Core;
using Vanara.PInvoke;
using Vanara.Windows.Shell;
using static Vanara.PInvoke.Shell32;
using PROPERTYKEY = Vanara.PInvoke.Ole32.PROPERTYKEY;

namespace Escapade.UI;

/// <summary>What running, undoing or redoing a <see cref="FileAction"/> did, for refreshing panes and reporting.</summary>
/// <param name="DidSomething">Anything changed on disk. If not, an undo entry has nothing left to act on.</param>
/// <param name="Folders">Folders whose listings changed.</param>
/// <param name="Select">Full paths to select afterwards, in whichever pane shows them.</param>
/// <param name="Note">Extra detail for the status bar, such as items that couldn't be undone.</param>
internal sealed record ActionOutcome(
    string? Error,
    bool Aborted,
    bool DidSomething,
    IReadOnlyList<string> Folders,
    IReadOnlyList<string> Select,
    string? Note)
{
    public bool Failed => Error is not null || Aborted;
}

/// <summary>Does, undoes and redoes <see cref="FileAction"/>s with the shell.</summary>
internal static class FileActionRunner
{
    /// <summary>Runs the action (first time, or redo). Records what happened on the action for a later undo.</summary>
    public static async Task<ActionOutcome> DoAsync(FileAction action, nint owner)
    {
        var sources = action.RedoSources(Path.Exists);
        if (sources.Count == 0)
            return Nothing(action, "The items no longer exist.");

        var preexisting = action.ExistingDestinations(Path.Exists);
        FileOpResult result;
        switch (action.Kind)
        {
            case ActionKind.Copy:
                result = await FileOperationRunner.RunAsync(owner, FileOperationRunner.Standard,
                    b => b.Operations.QueueCopyOperation(sources.Select(b.Item).ToList(), b.Folder(action.Target)));
                break;
            case ActionKind.Move:
                result = await FileOperationRunner.RunAsync(owner, FileOperationRunner.Standard,
                    b => b.Operations.QueueMoveOperation(sources.Select(b.Item).ToList(), b.Folder(action.Target)));
                break;
            case ActionKind.Rename:
                result = await FileOperationRunner.RunAsync(owner, FileOperationRunner.Standard,
                    b => b.Operations.QueueRenameOperation(b.Item(sources[0]), action.Target));
                break;
            case ActionKind.NewFolder:
                var folder = sources[0];
                var name = PathUtil.UniqueName(action.Target, n => Path.Exists(Path.Join(folder, n)));
                result = await FileOperationRunner.RunAsync(owner, FileOperationRunner.Standard,
                    b => b.Operations.QueueNewItemOperation(b.Folder(folder), name, FileAttributes.Directory, null!));
                break;
            default:
                result = await FileOperationRunner.RunAsync(owner, FileOperationRunner.Recycle,
                    b => b.Operations.QueueDeleteOperation(sources.Select(b.Item).ToList()));
                break;
        }

        var reported = result.Items;
        if (action.Kind == ActionKind.Recycle)
        {
            // A recycled item with no new Recycle Bin location was deleted outright, unless it's
            // still here, in which case the user chose "Skip" and nothing happened to it.
            reported = reported.Where(c => c.Result.Length > 0 || !Path.Exists(c.Source)).ToList();
        }
        action.Record(reported, preexisting);
        var select = action.Kind == ActionKind.Recycle ? [] : action.Changes.Select(c => c.Result).ToList();
        return new ActionOutcome(result.Error, result.Aborted, action.HasChanges, action.AffectedFolders, select, null);
    }

    public static async Task<ActionOutcome> UndoAsync(FileAction action, nint owner)
    {
        var plan = action.PlanUndo(Path.Exists);
        var notes = new List<string>();
        if (action.Partial)
        {
            notes.Add(action.Kind == ActionKind.Recycle
                ? "Items that were too large for the Recycle Bin were deleted permanently and can't be restored."
                : "Items that merged into or replaced existing ones were left alone.");
        }

        if (plan.Items.Count == 0)
            return Nothing(action, "The items have been moved or deleted since.");

        string? error;
        bool aborted;
        List<string> select;
        int missing = plan.Missing;

        switch (plan.Kind)
        {
            case UndoStepKind.Recycle:
            {
                var result = await FileOperationRunner.RunAsync(owner, FileOperationRunner.Recycle,
                    b => b.Operations.QueueDeleteOperation(plan.Items.Select(c => b.Item(c.Result)).ToList()));
                (error, aborted, select) = (result.Error, result.Aborted, []);
                break;
            }
            case UndoStepKind.MoveBack:
            {
                // Back under the original name too, which also undoes a "keep both" rename.
                var result = await FileOperationRunner.RunAsync(owner, FileOperationRunner.Standard, b =>
                {
                    foreach (var change in plan.Items)
                    {
                        var parent = PathUtil.GetParent(change.Source)!;
                        b.Operations.QueueMoveOperation(b.Item(change.Result), b.Folder(parent), Path.GetFileName(change.Source));
                    }
                });
                (error, aborted) = (result.Error, result.Aborted);
                select = plan.Items.Select(c => c.Source).Where(Path.Exists).ToList();
                break;
            }
            case UndoStepKind.RenameBack:
            {
                var change = plan.Items[0];
                var result = await FileOperationRunner.RunAsync(owner, FileOperationRunner.Standard,
                    b => b.Operations.QueueRenameOperation(b.Item(change.Result), Path.GetFileName(change.Source)));
                (error, aborted) = (result.Error, result.Aborted);
                select = Path.Exists(change.Source) ? [change.Source] : [];
                break;
            }
            default:
            {
                var restore = await RestoreFromRecycleBinAsync(owner, plan.Items);
                (error, aborted) = (restore.Error, restore.Aborted);
                missing += restore.Missing;
                select = plan.Items.Select(c => c.Source).Where(Path.Exists).ToList();
                if (restore.Found == 0 && error is null && !aborted)
                    return Nothing(action, restore.Detail ?? "The items are no longer in the Recycle Bin.");
                break;
            }
        }

        // What actually changed: anything recycled or moved away from where the action left it.
        bool didSomething = plan.Kind == UndoStepKind.Recycle
            ? plan.Items.Any(c => !Path.Exists(c.Result))
            : select.Count > 0;
        if (missing > 0)
            notes.Insert(0, $"{Count(missing)} couldn't be undone because they were moved or deleted since.");

        return new ActionOutcome(error, aborted, didSomething, action.AffectedFolders, select,
            notes.Count > 0 ? string.Join(" ", notes) : null);
    }

    private static ActionOutcome Nothing(FileAction action, string note) =>
        new(null, false, false, action.AffectedFolders, [], note);

    private static string Count(int n) => n == 1 ? "1 item" : $"{n} items";

    private sealed record RestoreResult(int Found, int Missing, string? Error, bool Aborted = false, string? Detail = null);

    private static readonly HRESULT Cancelled = unchecked((int)0x800704C7); // HRESULT_FROM_WIN32(ERROR_CANCELLED)

    /// <summary>
    /// Restores recycled items by invoking the Recycle Bin's own "Restore" verb ("undelete") on
    /// them. That puts each item back where it came from and removes its $I metadata file, which
    /// moving the $R file back by hand would leave behind.
    /// </summary>
    private static Task<RestoreResult> RestoreFromRecycleBinAsync(nint owner, IReadOnlyList<ItemChange> items) =>
        FileOperationRunner.RunOnStaAsync(() =>
        {
            var found = new List<ShellItem>();
            var others = new List<ShellItem>();
            try
            {
                using var bin = new ShellFolder(KNOWNFOLDERID.FOLDERID_RecycleBinFolder);
                var filter = FolderItemFilter.Folders | FolderItemFilter.NonFolders
                    | FolderItemFilter.IncludeHidden | FolderItemFilter.IncludeSuperHidden;

                // Primary match: the exact bin item the delete reported creating.
                var unmatched = items.ToDictionary(i => i.Result, StringComparer.OrdinalIgnoreCase);
                foreach (var child in bin.EnumerateChildren(filter))
                {
                    if (unmatched.Remove(FileOperationRunner.PathOf(child)))
                        found.Add(child);
                    else
                        others.Add(child);
                }

                // Fallback: same original folder and name, newest deletion first. Covers the shell
                // describing the same bin item differently at delete time and when listed.
                foreach (var change in unmatched.Values.ToList())
                {
                    if (FindByOrigin(others, change.Source) is { } match)
                    {
                        others.Remove(match);
                        found.Add(match);
                        unmatched.Remove(change.Result);
                    }
                }

                int missing = items.Count - found.Count;
                if (found.Count == 0)
                {
                    var lookedFor = items.Count > 0 ? $" (looked for {items[0].Result})" : "";
                    return new RestoreResult(0, missing, null, Detail: $"The items are no longer in the Recycle Bin{lookedFor}.");
                }
                if (found.Count == 0)
                    return new RestoreResult(0, missing, null);

                using var menu = ShellContextMenuHost.ForItems(bin, found, owner);
                var hr = menu.InvokeVerb(owner, "undelete");
                if (hr.Succeeded)
                    return new RestoreResult(found.Count, missing, null);
                if (hr == Cancelled)
                    return new RestoreResult(found.Count, missing, null, Aborted: true);
                return new RestoreResult(found.Count, missing, $"Restore failed ({hr}).");
            }
            catch (Exception ex)
            {
                return new RestoreResult(found.Count, 0, ex.Message);
            }
            finally
            {
                found.ForEach(i => i.Dispose());
                others.ForEach(i => i.Dispose());
            }
        });

    /// <summary>The newest bin item deleted from <paramref name="originalPath"/>'s folder under its name.</summary>
    private static ShellItem? FindByOrigin(List<ShellItem> candidates, string originalPath)
    {
        var folder = Path.GetDirectoryName(originalPath);
        var name = Path.GetFileName(originalPath);
        var stem = Path.GetFileNameWithoutExtension(originalPath); // in case extensions are hidden
        ShellItem? best = null;
        var bestDate = DateTime.MinValue;

        foreach (var candidate in candidates)
        {
            try
            {
                if (!candidate.Properties.TryGetValue(PROPERTYKEY.System.Recycle.DeletedFrom, out object? from)
                    || !PathUtil.SameLocation((from as string)?.TrimEnd('\\'), folder?.TrimEnd('\\')))
                {
                    continue;
                }
                candidate.Properties.TryGetValue(PROPERTYKEY.System.ItemNameDisplay, out object? display);
                var candidateName = display as string ?? candidate.Name;
                if (!string.Equals(candidateName, name, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(candidateName, stem, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                candidate.Properties.TryGetValue(PROPERTYKEY.System.Recycle.DateDeleted, out object? deleted);
                var date = deleted switch
                {
                    DateTime d => d,
                    System.Runtime.InteropServices.ComTypes.FILETIME f =>
                        DateTime.FromFileTimeUtc(((long)f.dwHighDateTime << 32) | (uint)f.dwLowDateTime),
                    _ => DateTime.MinValue,
                };
                if (best is null || date > bestDate)
                    (best, bestDate) = (candidate, date);
            }
            catch (Exception)
            {
                // A bin item whose properties can't be read just isn't a candidate.
            }
        }
        return best;
    }
}
