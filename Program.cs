// ============================================================
// File: Program.cs
// Description: Application entry point
// ============================================================

using System;
using System.Windows.Forms;
using PLCManager.UI.Forms;

namespace PLCManager
{
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            // Global unhandled exception handlers
            Application.ThreadException += (s, e) =>
            {
                Services.AppLogger.Instance.Critical("App", "Unhandled thread exception", e.Exception);
                MessageBox.Show($"Critical error:\n{e.Exception.Message}",
                    "PLC Manager", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };

            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                Services.AppLogger.Instance.Critical("App",
                    "Unhandled domain exception",
                    e.ExceptionObject as Exception);
            };

            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}
