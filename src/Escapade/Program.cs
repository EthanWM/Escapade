using Escapade.UI;

namespace Escapade;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // One window, never many: a second launch hands its folders to the first and leaves.
        using var mutex = new Mutex(initiallyOwned: true, SingleInstance.MutexName, out bool first);
        if (!first)
        {
            if (!SingleInstance.TrySend(args))
            {
                ApplicationConfiguration.Initialize();
                MessageBox.Show("Escapade is already running but isn't responding.", "Escapade",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(args));
        GC.KeepAlive(mutex);
    }
}
