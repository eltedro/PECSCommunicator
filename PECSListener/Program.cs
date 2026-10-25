using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PECSListener
{
    internal static class Program
    {
        // One Listener per PC: the port can only be owned by one process, so a second copy
        // would just fail with "Failed to listen on prefix". "Global\" makes the guard cover
        // every Windows session (e.g. a copy started remotely and one started by hand).
        private const string MutexName = @"Global\PECSListener.SingleInstance";

        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        private const int SW_RESTORE = 9;

        /// <summary>
        ///  The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main()
        {
            using var mutex = new Mutex(initiallyOwned: true, MutexName, out bool isFirstInstance);
            if (!isFirstInstance)
            {
                if (!BringExistingToFront())
                    MessageBox.Show("PECS Listener is already running on this PC.", "PECS Listener",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // To customize application configuration such as set high DPI settings or default font,
            // see https://aka.ms/applicationconfiguration.
            ApplicationConfiguration.Initialize();
            Application.Run(new MainForm());
        }

        /// <summary>
        /// Restores and focuses the window of the copy that is already running.
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
                    ShowWindow(hWnd, SW_RESTORE);
                    SetForegroundWindow(hWnd);
                    return true;
                }
            }
            return false;
        }
    }
}
