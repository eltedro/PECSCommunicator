using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PECSCommunicator;

static class Program
{
    // TODO #8: one Communicator per PC. With "Start with Windows" on, a manual launch would otherwise
    // open a second copy that sends every tap twice. "Global\" covers every Windows session.
    private const string MutexName = @"Global\PECSCommunicator.SingleInstance";

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    private const int SW_SHOW = 5;

    [STAThread]
    static int Main(string[] args)
    {
        // Developer check of the display-selection logic; no UI, no mutex. See ScreenPlacement.RunSelfTest.
        if (args.Contains("--selftest-placement"))
            return ScreenPlacement.RunSelfTest() ? 0 : 1;

        // Android TODO #5: send one "Walk" press to all listeners and print per-listener results; no UI, no mutex.
        // Extra arguments are additional URLs for this run only, e.g. a bogus one to test a down listener.
        if (args.Length > 0 && args[0] == "--selftest-send")
            return MainForm.RunSendSelfTest(args.Skip(1));

        using var mutex = new Mutex(initiallyOwned: true, MutexName, out bool isFirstInstance);
        if (!isFirstInstance)
        {
            if (!BringExistingToFront())
                MessageBox.Show("PECS Communicator is already running on this PC.", "PECS Communicator",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        return 0;
    }

    /// <summary>
    /// Focuses the window of the copy that is already running, without changing its size or placement.
    /// Returns false if it can't be reached (e.g. it runs in another user's session).
    /// </summary>
    private static bool BringExistingToFront()
    {
        using var current = Process.GetCurrentProcess();
        foreach (var other in Process.GetProcessesByName(current.ProcessName))
        {
            using (other)
            {
                if (other.Id == current.Id || other.SessionId != current.SessionId) continue;
                var hWnd = other.MainWindowHandle;
                if (hWnd == IntPtr.Zero) continue;
                ShowWindow(hWnd, SW_SHOW);
                SetForegroundWindow(hWnd);
                return true;
            }
        }
        return false;
    }
}
