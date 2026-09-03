using System;
using System.Threading;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppLifecycle;

namespace RhythmHub;

public static class Program
{
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    [STAThread]
    static void Main(string[] args)
    {
        AttachConsole(-1);

        try
        {
            if (args.Length >= 1)
            {
                if (args[0] == "--scan")
                {
                    var dm = new Managers.DeviceManager();
                    var res = dm.ScanForDevices();
                    Console.WriteLine($"Found {res.count} devices:\n{res.log}");
                    Environment.Exit(0);
                    return;
                }
                if (args.Length >= 2)
                {
                    if (args[0] == "--revert")
                    {
                        int exitCode = Managers.DeviceManager.ExecuteRevertPnp(args[1]) ? 0 : 1;
                        Environment.Exit(exitCode);
                        return;
                    }
                    else if (args[0] == "--winusb")
                    {
                        int exitCode = Managers.DeviceManager.ExecuteSwitchToWinUsbPnp(args[1]) ? 0 : 1;
                        Environment.Exit(exitCode);
                        return;
                    }
                }
            }

            WinRT.ComWrappersSupport.InitializeComWrappers();

            Microsoft.UI.Xaml.Application.Start((p) =>
            {
                var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
                SynchronizationContext.SetSynchronizationContext(context);
                new App();
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Startup Fatal Error] {ex}");
            System.IO.File.WriteAllText("fatal_startup_error.txt", ex.ToString());
        }
    }
}
