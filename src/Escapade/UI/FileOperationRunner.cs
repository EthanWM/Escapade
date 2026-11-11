using Vanara.PInvoke;
using Vanara.Windows.Shell;
using Ops = Vanara.Windows.Shell.ShellFileOperations;

namespace Escapade.UI;

internal sealed record FileOpResult(bool Aborted, IReadOnlyList<string> CreatedPaths, string? Error);

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
/// prompts, elevation, Recycle Bin and undo. IFileOperation must run in an STA, and
/// PerformOperations blocks until the user is done with any dialogs. Each operation therefore
/// gets its own STA thread, and the panes stay responsive during long copies.
/// </summary>
internal static class FileOperationRunner
{
    /// <summary>Flags for user-invoked changes that should land on Explorer's undo stack.</summary>
    public const Ops.OperationFlags Undoable =
        Ops.OperationFlags.AllowUndo | Ops.OperationFlags.AddUndoRecord | Ops.OperationFlags.NoConfirmMkDir;

    private const int HResultCancelled = unchecked((int)0x800704C7);          // HRESULT_FROM_WIN32(ERROR_CANCELLED)
    private const int HResultCopyEngineCancelled = unchecked((int)0x80270000); // COPYENGINE_E_USER_CANCELLED

    /// <param name="owner">Window that owns the shell's dialogs. Read it on the UI thread.</param>
    /// <param name="queue">Queues the work. Runs on the operation's STA thread.</param>
    public static Task<FileOpResult> RunAsync(nint owner, Ops.OperationFlags flags, Action<FileOpBuilder> queue)
    {
        var completion = new TaskCompletionSource<FileOpResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var result = Run(owner, flags, queue);
            try
            {
                completion.SetResult(result);
            }
            catch (Exception)
            {
                // The UI may have shut down while the operation ran; nobody is left to notify.
            }
        })
        {
            // Foreground on purpose: closing the window must not kill a copy or move halfway.
            // The process exits once the operation (and the shell's dialogs) finish.
            IsBackground = false,
            Name = "Escapade file operation",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static FileOpResult Run(nint owner, Ops.OperationFlags flags, Action<FileOpBuilder> queue)
    {
        var created = new List<string>();
        try
        {
            using var operations = new Ops((HWND)owner) { Options = flags };

            // Record where things actually landed. "Keep both" in a conflict prompt renames the
            // destination, so the source names aren't enough to select the results afterwards.
            void Track(object? sender, Ops.ShellFileOpEventArgs e)
            {
                if (e.Result.Succeeded && e.DestItem?.FileSystemPath is { Length: > 0 } path)
                    created.Add(path);
            }
            operations.PostCopyItem += Track;
            operations.PostMoveItem += Track;
            operations.PostRenameItem += Track;
            operations.PostNewItem += Track;

            using var builder = new FileOpBuilder(operations);
            queue(builder);
            operations.PerformOperations();
            return new FileOpResult(operations.AnyOperationsAborted, created, null);
        }
        catch (Exception ex) when (ex.HResult is HResultCancelled or HResultCopyEngineCancelled)
        {
            return new FileOpResult(true, created, null);
        }
        catch (Exception ex)
        {
            return new FileOpResult(false, created, ex.Message);
        }
    }
}
