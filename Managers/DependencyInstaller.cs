using System;
using System.Diagnostics;
using System.IO;

namespace RhythmHub.Managers;

public static class DependencyInstaller
{
    public static async Task<bool> IsViGEmBusInstalledAsync()
    {
        return await Task.Run(() =>
        {
            try
            {
                using var client = new Nefarius.ViGEm.Client.ViGEmClient();
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ViGEmBus not found or error checking: {ex.Message}");
                return false;
            }
        });
    }

    public static void InstallViGEmBus(string installerPath)
    {
        if (!File.Exists(installerPath))
        {
            Console.WriteLine($"Installer not found at {installerPath}");
            return;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = installerPath,
                UseShellExecute = true,
                Verb = "runas" // Prompt for elevation
            };
            
            var process = Process.Start(psi);
            process?.WaitForExit();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to launch installer: {ex.Message}");
        }
    }
}
