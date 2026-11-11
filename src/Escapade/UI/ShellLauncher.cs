using System.ComponentModel;
using System.Diagnostics;

namespace Escapade.UI;

internal static class ShellLauncher
{
    private const int ERROR_CANCELLED = 1223;

    /// <summary>
    /// Opens a file with its default handler via ShellExecuteEx. With ErrorDialog on, the shell
    /// shows its own UI, including "How do you want to open this file?" for unassociated types.
    /// Returns an error message, or null on success or user cancel.
    /// </summary>
    public static string? Open(string path, IWin32Window? owner)
    {
        try
        {
            var info = new ProcessStartInfo(path)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(path) ?? "",
                ErrorDialog = owner is not null,
                ErrorDialogParentHandle = owner?.Handle ?? 0,
            };
            using var process = Process.Start(info);
            return null;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ERROR_CANCELLED)
        {
            return null; // user dismissed a UAC or "open with" prompt
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            return $"Couldn't open {Path.GetFileName(path)}: {ex.Message}";
        }
    }
}
