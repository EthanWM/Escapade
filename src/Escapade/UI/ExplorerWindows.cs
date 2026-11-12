using System.Runtime.InteropServices;
using Escapade.Core;

namespace Escapade.UI;

internal sealed record ExplorerFolderResult(IReadOnlyList<string> Locations, int UnreadableWindows);

internal static class ExplorerWindows
{
    public static ExplorerFolderResult ReadOpenFolders()
    {
        var shellType = Type.GetTypeFromProgID("Shell.Application")
            ?? throw new InvalidOperationException("Windows Shell automation isn't available.");
        object? shell = null;
        object? windows = null;
        try
        {
            shell = Activator.CreateInstance(shellType)
                ?? throw new InvalidOperationException("Couldn't connect to Windows Shell.");
            windows = ((dynamic)shell).Windows();
            int count = (int)((dynamic)windows).Count;
            var locations = new List<string>();
            int unreadable = 0;

            for (int index = 0; index < count; index++)
            {
                object? window = null;
                try
                {
                    window = ((dynamic)windows).Item(index);
                    if (window is null)
                        continue;

                    dynamic explorerWindow = window;
                    string? executable = explorerWindow.FullName;
                    if (!string.Equals(Path.GetFileName(executable), "explorer.exe", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string? locationUrl = explorerWindow.LocationURL;
                    if (Uri.TryCreate(locationUrl, UriKind.Absolute, out var uri)
                        && uri.IsFile
                        && PathUtil.Normalize(uri.LocalPath) is { } location)
                    {
                        locations.Add(location);
                    }
                }
                catch (COMException)
                {
                    unreadable++;
                }
                finally
                {
                    Release(window);
                }
            }

            return new ExplorerFolderResult(locations, unreadable);
        }
        finally
        {
            Release(windows);
            Release(shell);
        }
    }

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
            Marshal.ReleaseComObject(value);
    }
}
