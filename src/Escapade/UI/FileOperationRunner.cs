using Escapade.Core;
using Vanara.PInvoke;
using Vanara.Windows.Shell;
using Ops = Vanara.Windows.Shell.ShellFileOperations;

namespace Escapade.UI;

/// <summary>
/// What a shell file operation did. <see cref="Items"/> has one entry per item the shell reported
/// finishing successfully, including items nested inside copied folders.
/// </summary>
internal sealed record FileOpResult(bool Aborted, IReadOnlyList<ItemChange> Items, string? Error);

/// <summary>Creates shell items for an operation on its STA thread and releases them afterwards.</summary>
internal sealed class FileOpBuilder(Ops operations) : IDisposable
{
    private readonly List<IDisposable> _owned = [];

    public Ops Operations { get; } = operations;

    public ShellItem Item(string path)
    {
        var item = new ShellItem(path);
        _owned.Add(item);
        return item;
    }

    public ShellFolder Folder(string path)
    {
        var folder = new ShellFolder(path);
        _owned.Add(folder);
        return folder;
    }

    public void Dispose()
    {
        foreach (var owned in _owned)
            owned.Dispose();
    }
}

/// <summary>
/// Runs shell file operations (IFileOperation) so Windows provides the progress UI, conflict
/// prompts, elevation and the Recycle Bin. IFileOperation must run in an STA, and
/// PerformOperations blocks until the user is done with any dialogs. Each operation therefore
/// gets its own STA thread, and the panes stay responsive during long copies.
/// </summary>
internal static class FileOperationRunner
{
    /// <summary>
    /// Flags for user-invoked changes. Escapade keeps its own undo history, so nothing is added to
    /// Explorer's (FOFX_ADDUNDORECORD): undoing an Escapade change from Explorer would leave
    /// Escapade's history describing things that no longer exist.
    /// </summary>
    public const Ops.OperationFlags Standard = Ops.OperationFlags.AllowUndo | Ops.OperationFlags.NoConfirmMkDir;

    /// <summary>Delete to the Recycle Bin. On a delete, FOF_ALLOWUNDO is what means "recycle".</summary>
    public const Ops.OperationFlags Recycle =
        Standard | Ops.OperationFlags.RecycleOnDelete | Ops.OperationFlags.WantNukeWarning;

    private const int HResultCancelled = unchecked((int)0x800704C7);          // HRESULT_FROM_WIN32(ERROR_CANCELLED)
    private const int HResultCopyEngineCancelled = unchecked((int)0x80270000); // COPYENGINE_E_USER_CANCELLED

    private static int s_active;

    /// <summary>Operations currently running (their shell dialogs may still be open).</summary>
    public static int ActiveCount => Volatile.Read(ref s_active);

    /// <param name="owner">Window that owns the shell's dialogs. Read it on the UI thread.</param>
    /// <param name="queue">Queues the work. Runs on the operation's STA thread.</param>
    public static Task<FileOpResult> RunAsync(nint owner, Ops.OperationFlags flags, Action<FileOpBuilder> queue) =>
        RunOnStaAsync(() => Run(owner, flags, queue));

    /// <summary>
    /// Runs shell work on a new STA thread. The thread is a foreground thread on purpose: closing
    /// the window must not kill a copy or move halfway. The process exits once it finishes.
    /// </summary>
    public static Task<T> RunOnStaAsync<T>(Func<T> work)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Increment(ref s_active);
        var thread = new Thread(() =>
        {
            try
            {
                completion.TrySetResult(work());
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
            finally
            {
                Interlocked.Decrement(ref s_active);
            }
        })
        {
            IsBackground = false,
            Name = "Escapade file operation",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    /// <summary>
    /// A stable string identity for a shell item: its file system path, or for items without one
    /// its absolute parsing name. Used to find Recycle Bin items again later.
    /// </summary>
    public static string PathOf(ShellItem? item)
    {
        if (item is null)
            return "";
        try
        {
            if (item.FileSystemPath is { Length: > 0 } path)
                return path;
            return item.GetDisplayName(ShellItemDisplayString.DesktopAbsoluteParsing) ?? "";
        }
        catch (Exception)
        {
            return "";
        }
    }

    private static FileOpResult Run(nint owner, Ops.OperationFlags flags, Action<FileOpBuilder> queue)
    {
        var items = new List<ItemChange>();
        try
        {
            using var operations = new Ops((HWND)owner) { Options = flags };

            // Record where things actually landed. "Keep both" in a conflict prompt renames the
            // destination, so the source names aren't enough to select or undo the results. For a
            // recycle, the destination is the item's new home inside the Recycle Bin.
            void Track(object? sender, Ops.ShellFileOpEventArgs e)
            {
                if (e.Result.Succeeded)
                    items.Add(new ItemChange(PathOf(e.SourceItem), PathOf(e.DestItem)));
            }
            operations.PostCopyItem += Track;
            operations.PostMoveItem += Track;
            operations.PostRenameItem += Track;
            operations.PostNewItem += Track;
            operations.PostDeleteItem += Track;

            using var builder = new FileOpBuilder(operations);
            queue(builder);
            operations.PerformOperations();
            return new FileOpResult(operations.AnyOperationsAborted, items, null);
        }
        catch (Exception ex) when (ex.HResult is HResultCancelled or HResultCopyEngineCancelled)
        {
            return new FileOpResult(true, items, null);
        }
        catch (Exception ex)
        {
            return new FileOpResult(false, items, ex.Message);
        }
    }
}
