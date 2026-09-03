using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using HidSharp;
using RhythmHub.Models;

namespace RhythmHub.Providers;

public class GHLiveHidProvider : IDeviceProvider, IDisposable
{
    public string DeviceName { get; }
    public string DevicePath => _device.DevicePath;
    public string? InstanceId => null;
    public bool IsSynced { get; private set; }
    public string? ErrorMessage { get; private set; }
    public bool RequiresVirtualController => false;

    private readonly HidDevice _device;
    private SafeFileHandle? _handle;

    public GHLiveHidProvider(HidDevice device, string deviceName = "GHLive Guitar (Native HID)")
    {
        _device = device;
        DeviceName = deviceName;
    }

    public async Task StartListeningAsync(Action<int, InstrumentState> onStateChanged, CancellationToken token)
    {
        try
        {
            // Open device natively bypassing HidSharp stream limitations
            _handle = NativeMethods.CreateFile(
                _device.DevicePath,
                NativeMethods.GENERIC_READ | NativeMethods.GENERIC_WRITE,
                NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
                IntPtr.Zero,
                NativeMethods.OPEN_EXISTING,
                NativeMethods.FILE_FLAG_OVERLAPPED,
                IntPtr.Zero);

            if (_handle.IsInvalid)
            {
                Console.WriteLine($"Failed to open device handle natively for {_device.DevicePath}");
                return;
            }

            // Report ID 0x02, followed by the magic payload (9 bytes total as per GHLPokeMachine)
            byte[] pokeData = new byte[] { 0x02, 0x02, 0x08, 0x20, 0x00, 0x00, 0x00, 0x00, 0x00 };

            while (!token.IsCancellationRequested)
            {
                bool success = false;
                if (_handle != null && !_handle.IsInvalid && !_handle.IsClosed)
                {
                    success = NativeMethods.HidD_SetOutputReport(_handle, pokeData, pokeData.Length);
                }
                
                if (success)
                {
                    Console.WriteLine("Sent native HID Poke to GHLive Dongle via Control Endpoint.");
                    IsSynced = true; // Mark as synced once the poke succeeds
                }
                else
                {
                    Console.WriteLine($"Failed to send poke natively. Error code: {Marshal.GetLastWin32Error()}");
                    IsSynced = false;
                    break; // If write fails, device probably disconnected
                }

                // GHLPokeMachine sends the poke every 10 seconds (PS3_WIIU_SLEEP_TIME = 10 * ONE_SECOND)
                await Task.Delay(10000, token);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on stop/disconnect
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error in GHLiveHidProvider: {ex.Message}");
        }
        finally
        {
            IsSynced = false;
            Dispose();
        }
    }

    public void Dispose()
    {
        try
        {
            if (_handle != null && !_handle.IsInvalid && !_handle.IsClosed)
            {
                _handle.Dispose();
            }
        }
        catch { }
        _handle = null;
    }
}

public static class NativeMethods
{
    [DllImport("hid.dll", SetLastError = true)]
    public static extern bool HidD_SetOutputReport(SafeFileHandle hidDeviceObject, byte[] reportBuffer, int reportBufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    public const uint GENERIC_READ = 0x80000000;
    public const uint GENERIC_WRITE = 0x40000000;
    public const uint FILE_SHARE_READ = 0x00000001;
    public const uint FILE_SHARE_WRITE = 0x00000002;
    public const uint OPEN_EXISTING = 3;
    public const uint FILE_FLAG_OVERLAPPED = 0x40000000;
}
