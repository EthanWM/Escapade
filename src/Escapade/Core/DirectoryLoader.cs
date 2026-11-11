using System.IO.Enumeration;

namespace Escapade.Core;

/// <summary>
/// Receives listing results. Called on a background thread. Ownership of <paramref name="batch"/>
/// passes to the callee. The final call has <paramref name="completed"/> set, and carries
/// <paramref name="error"/> if the listing stopped early.
/// </summary>
public delegate void LoadCallback(List<FileEntry> batch, bool completed, Exception? error);

/// <summary>Enumerates a location on a background thread and streams results in time-sliced batches.</summary>
public static class DirectoryLoader
{
    private static readonly EnumerationOptions Options = new()
    {
        // The default skips Hidden and System entries; a dev file manager needs to see .git etc.
        AttributesToSkip = 0,
        // Non-recursive, so the only thing that can be inaccessible is the folder itself, and
        // that must surface as an error rather than a silently empty list.
        IgnoreInaccessible = false,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
    };

    public static void Start(string location, int batchIntervalMs, CancellationToken token, LoadCallback callback) =>
        Task.Run(() => Load(location, batchIntervalMs, token, callback));

    private static void Load(string location, int batchIntervalMs, CancellationToken token, LoadCallback callback)
    {
        var batch = new List<FileEntry>();
        try
        {
            IEnumerable<FileEntry> source = PathUtil.IsComputerRoot(location)
                ? EnumerateDrives()
                : EnumerateDirectory(location);

            long lastFlush = Environment.TickCount64;
            foreach (var entry in source)
            {
                if (token.IsCancellationRequested)
                    return;

                batch.Add(entry);
                if (Environment.TickCount64 - lastFlush >= batchIntervalMs)
                {
                    callback(batch, completed: false, error: null);
                    batch = [];
                    lastFlush = Environment.TickCount64;
                }
            }

            if (!token.IsCancellationRequested)
                callback(batch, completed: true, error: null);
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
                callback(batch, completed: true, error: ex);
        }
    }

    private static IEnumerable<FileEntry> EnumerateDirectory(string path) =>
        new FileSystemEnumerable<FileEntry>(path, static (ref FileSystemEntry e) =>
        {
            bool isDirectory = e.IsDirectory;
            return new FileEntry(
                e.FileName.ToString(),
                isDirectory ? EntryKind.Directory : EntryKind.File,
                e.Attributes,
                isDirectory ? -1 : e.Length,
                e.LastWriteTimeUtc.UtcDateTime);
        }, Options);

    private static List<FileEntry> EnumerateDrives()
    {
        var drives = new List<FileEntry>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            string label = "";
            long total = -1;
            try
            {
                // IsReady can block briefly on optical/network drives - another reason this runs off the UI thread.
                if (drive.IsReady)
                {
                    label = drive.VolumeLabel;
                    total = drive.TotalSize;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }

            var kindName = drive.DriveType switch
            {
                DriveType.Fixed => "Local Disk",
                DriveType.Removable => "Removable Disk",
                DriveType.Network => "Network Drive",
                DriveType.CDRom => "CD Drive",
                DriveType.Ram => "RAM Disk",
                _ => "Drive",
            };
            var letter = drive.Name.TrimEnd('\\');
            var display = $"{(label.Length > 0 ? label : kindName)} ({letter})";

            drives.Add(new FileEntry(drive.Name, EntryKind.Drive, 0, total, default, display, kindName));
        }
        return drives;
    }
}
