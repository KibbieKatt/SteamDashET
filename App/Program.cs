using System;
using System.Threading;
using System.Windows.Forms;

namespace SteamDashET;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--self-test")
        {
            try
            {
                SelfChecks.Run();
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
                return 1;
            }
        }

        ApplicationConfiguration.Initialize();
        using var singleInstance = new Mutex(true, @"Local\SteamDashET.App", out bool firstInstance);
        if (!firstInstance)
        {
            MessageBox.Show(
                "SteamDashET is already running. Use its control window to start or stop the gaze dot.",
                "SteamDashET", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        try
        {
            Application.Run(new ControlWindow());
            return 0;
        }
        finally
        {
            singleInstance.ReleaseMutex();
        }
    }
}
