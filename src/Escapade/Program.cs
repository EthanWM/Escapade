using System.Diagnostics;
using Escapade.Core;
using Escapade.UI;

namespace Escapade;

internal static class Program
{
    /// <summary>Set before closing the main window to start a fresh Escapade once this one has gone (theme changes).</summary>
    public static bool RestartRequested { get; set; }

    [STAThread]
    private static void Main(string[] args)
    {
        // One window, never many: a second launch hands its folders to the first and leaves.
        var mutex = new Mutex(initiallyOwned: true, SingleInstance.MutexName, out bool first);
        try
        {
            ApplicationConfiguration.Initialize();
            // Before any window exists: the colour mode can't change afterwards.
            Theme.Apply(AppSettings.Load(SettingsFile.DefaultFolder).ThemeChoice);

            if (!first)
            {
                if (!SingleInstance.TrySend(args))
                {
                    MessageBox.Show("Escapade is already running but isn't responding.", "Escapade",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                return;
            }

            Application.Run(new MainForm(args));

            if (RestartRequested && Environment.ProcessPath is { } exe)
            {
                // Let go of the single-instance mutex first, or the new copy would think this one
                // is still running and hand itself over to it.
                mutex.ReleaseMutex();
                mutex.Dispose();
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false })?.Dispose();
            }
        }
        finally
        {
            mutex.Dispose();
        }
    }
}
