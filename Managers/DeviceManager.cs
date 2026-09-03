using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Security.Principal;
using System.Management;
using HidSharp;
using LibUsbDotNet;
using LibUsbDotNet.Main;
using Nefarius.Utilities.DeviceManagement.PnP;
using Nefarius.Utilities.DeviceManagement.Extensions;
using Nefarius.Drivers.WinUSB;
using RhythmHub.Models;
using RhythmHub.Providers;
using RhythmHub.Virtual;

namespace RhythmHub.Managers;

public class DeviceManager : IDisposable
{
    private readonly object _lock = new();
    private readonly List<IDeviceProvider> _activeProviders = new();
    private readonly List<(IDeviceProvider Provider, ConcurrentDictionary<int, IVirtualController> VirtualPads, CancellationTokenSource Cts)> _runningSessions = new();
    
    public event Action<IDeviceProvider>? OnDeviceAdded;
    public event Action<IDeviceProvider>? OnDeviceRemoved;
    public event Action? OnDevicesCleared;
    public event Action<string, string, Microsoft.UI.Xaml.Controls.InfoBarSeverity>? OnHotplugEvent;

    private ManagementEventWatcher? _usbInsertWatcher;
    private ManagementEventWatcher? _usbRemoveWatcher;

    public DeviceManager()
    {
        SetupWmiHotplugWatcher();
    }

    public event Action? OnHotplugRescanRequired;
    public volatile bool IsSwappingDriver = false;

    private void SetupWmiHotplugWatcher()
    {
        try
        {
            // Listen to any USB plug event
            _usbInsertWatcher = new ManagementEventWatcher(new WqlEventQuery("SELECT * FROM __InstanceCreationEvent WITHIN 2 WHERE TargetInstance ISA 'Win32_PnPEntity' AND TargetInstance.PNPDeviceID LIKE 'USB%'"));
            _usbInsertWatcher.EventArrived += UsbDeviceInserted;
            _usbInsertWatcher.Start();

            // Listen to any USB unplug event
            _usbRemoveWatcher = new ManagementEventWatcher(new WqlEventQuery("SELECT * FROM __InstanceDeletionEvent WITHIN 2 WHERE TargetInstance ISA 'Win32_PnPEntity' AND TargetInstance.PNPDeviceID LIKE 'USB%'"));
            _usbRemoveWatcher.EventArrived += UsbDeviceRemoved;
            _usbRemoveWatcher.Start();
        }
        catch (Exception ex)
        {
            Console.WriteLine("WMI Watcher failed: " + ex.Message);
        }
    }

    private void UsbDeviceInserted(object sender, EventArrivedEventArgs e)
    {
        if (IsSwappingDriver) return;

        try 
        {
            var instance = (ManagementBaseObject)e.NewEvent["TargetInstance"];
            string name = instance["Name"]?.ToString() ?? "Unknown USB Device";
            string pnpId = instance["PNPDeviceID"]?.ToString() ?? "";

            if (pnpId.Contains("VID_1430&PID_079B", StringComparison.OrdinalIgnoreCase) || 
                pnpId.Contains("VID_12BA&PID_074B", StringComparison.OrdinalIgnoreCase))
            {
                OnHotplugEvent?.Invoke("Dongle Plugged In", $"{name} recognized as GHLive dongle! Auto-connecting...", Microsoft.UI.Xaml.Controls.InfoBarSeverity.Success);
                
                // Tell the UI to trigger a debounced rescan
                OnHotplugRescanRequired?.Invoke();
            }
            else
            {
                OnHotplugEvent?.Invoke("USB Device Connected", $"{name} connected, but not recognized as a dongle.", Microsoft.UI.Xaml.Controls.InfoBarSeverity.Informational);
            }
        } 
        catch { }
    }

    private void UsbDeviceRemoved(object sender, EventArrivedEventArgs e)
    {
        if (IsSwappingDriver) return;

        try 
        {
            var instance = (ManagementBaseObject)e.NewEvent["TargetInstance"];
            string name = instance["Name"]?.ToString() ?? "Unknown USB Device";
            string pnpId = instance["PNPDeviceID"]?.ToString() ?? "";

            if (pnpId.Contains("VID_1430&PID_079B", StringComparison.OrdinalIgnoreCase) || 
                pnpId.Contains("VID_12BA&PID_074B", StringComparison.OrdinalIgnoreCase))
            {
                OnHotplugEvent?.Invoke("Dongle Unplugged", $"{name} was disconnected. Removing from hub.", Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning);
                
                // Tell the UI to trigger a debounced rescan
                OnHotplugRescanRequired?.Invoke();
            }
        } 
        catch { }
    }

    public void ClearDevices()
    {
        List<(IDeviceProvider Provider, ConcurrentDictionary<int, IVirtualController> VirtualPads, CancellationTokenSource Cts)> sessionsToClean;

        lock (_lock)
        {
            sessionsToClean = new List<(IDeviceProvider, ConcurrentDictionary<int, IVirtualController>, CancellationTokenSource)>(_runningSessions);
            _runningSessions.Clear();
            _activeProviders.Clear();
        }

        foreach (var session in sessionsToClean)
        {
            try
            {
                session.Cts.Cancel();
            }
            catch { }

            foreach (var vc in session.VirtualPads.Values)
            {
                try
                {
                    vc.Disconnect();
                }
                catch { }
            }
            session.VirtualPads.Clear();
        }

        OnDevicesCleared?.Invoke();
    }

    public (int count, string log) ScanForDevices()
    {
        int devicesFound = 0;
        var logBuilder = new System.Text.StringBuilder();
        logBuilder.AppendLine("--- USB DIAGNOSTIC SCAN ---");

        // 1. Sweep using Nefarius PnP Device Management
        logBuilder.AppendLine("[Nefarius PnP Enumeration]");
        try
        {
            var pnpDevices = USBDevice.GetDevices(DeviceInterfaceIds.UsbDevice);
            foreach (var usbDevice in pnpDevices)
            {
                string devPath = usbDevice.DevicePath;
                logBuilder.AppendLine($"PnP Device: {devPath}");
                
                try
                {
                    var pnpDevice = PnPDevice.GetDeviceByInterfaceId(devPath);
                    if (pnpDevice is null) continue;
                    var hwIds = pnpDevice.GetProperty<string[]>(DevicePropertyKey.Device_HardwareIds);
                    
                    if (hwIds != null && hwIds.Any(id => id.Contains("VID_1430&PID_079B", StringComparison.OrdinalIgnoreCase)))
                    {
                        var classGuid = pnpDevice.GetProperty<Guid>(DevicePropertyKey.Device_ClassGuid);
                        var winUsbGuid = Guid.Parse("88BAE032-5A81-49F0-BC3D-A4FF138216D6");

                        if (classGuid != winUsbGuid)
                        {
                            logBuilder.AppendLine($"Found Xbox One Dongle (1430:079B) using default Windows driver.");
                            lock (_lock)
                            {
                                var existing = _activeProviders.FirstOrDefault(p => string.Equals(p.DevicePath, devPath, StringComparison.OrdinalIgnoreCase));
                                if (existing is XboxOneGhlProvider)
                                {
                                    // It transitioned from WinUSB -> Default Driver
                                    StopAndRemoveProvider(existing);
                                    existing = null;
                                }

                                if (existing == null)
                                {
                                    var provider = new XboxOneDefaultDriverProvider(devPath, pnpDevice.InstanceId);
                                    _activeProviders.Add(provider);
                                    OnDeviceAdded?.Invoke(provider);
                                    StartProvider(provider);
                                    devicesFound++;
                                }
                            }
                        }
                        else
                        {
                            logBuilder.AppendLine($"Found Xbox One Dongle (1430:079B) using WinUSB.");
                            lock (_lock)
                            {
                                var existing = _activeProviders.FirstOrDefault(p => string.Equals(p.DevicePath, devPath, StringComparison.OrdinalIgnoreCase));
                                if (existing is XboxOneDefaultDriverProvider)
                                {
                                    // It transitioned from Default Driver -> WinUSB
                                    StopAndRemoveProvider(existing);
                                    existing = null;
                                }

                                if (existing == null)
                                {
                                    var provider = new XboxOneGhlProvider(devPath, "GHLive Guitar (Xbox One WinUSB)", pnpDevice.InstanceId);
                                    _activeProviders.Add(provider);
                                    OnDeviceAdded?.Invoke(provider);
                                    StartProvider(provider);
                                    devicesFound++;
                                }
                            }
                        }
                    }
                }
                catch (Exception innerEx)
                {
                    logBuilder.AppendLine($"Error inspecting PnP device: {innerEx.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            logBuilder.AppendLine($"PnP Enumeration failed: {ex.Message}");
        }

        // 2. Sweep using HidSharp
        logBuilder.AppendLine("\n[HidSharp Native HID Enumeration]");
        try
        {
            foreach (var hidDevice in HidSharp.DeviceList.Local.GetHidDevices())
            {
                try
                {
                    string devInfo = $"VID: 0x{hidDevice.VendorID:X4}, PID: 0x{hidDevice.ProductID:X4} | {hidDevice.DevicePath}";
                    logBuilder.AppendLine(devInfo);

                    if (hidDevice.VendorID == 0x12BA && hidDevice.ProductID == 0x074B)
                    {
                        lock (_lock)
                        {
                            bool alreadyAdded = _activeProviders.Any(p => string.Equals(p.DevicePath, hidDevice.DevicePath, StringComparison.OrdinalIgnoreCase));
                            if (!alreadyAdded)
                            {
                                var provider = new GHLiveHidProvider(hidDevice, "GHLive Guitar (Wii/PS3 Native HID)");
                                _activeProviders.Add(provider);
                                OnDeviceAdded?.Invoke(provider);
                                StartProvider(provider);
                                devicesFound++;
                            }
                        }
                    }
                }
                catch (Exception devEx)
                {
                    logBuilder.AppendLine($"Error inspecting HID device: {devEx.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            logBuilder.AppendLine($"HidSharp enumeration failed: {ex.Message}");
        }
        
        logBuilder.AppendLine("---------------------------");
        
        return (devicesFound, logBuilder.ToString());
    }

    public static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    public static async Task<bool> RunElevatedAsync(string args)
    {
        string location = Environment.ProcessPath ?? Environment.GetCommandLineArgs()[0];
        var processInfo = new ProcessStartInfo()
        {
            Verb = "runas",
            FileName = location,
            Arguments = args,
            UseShellExecute = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        try
        {
            var process = Process.Start(processInfo);
            if (process == null) return false;
            await process.WaitForExitAsync();
            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Elevation request was rejected or failed: {ex.Message}");
            return false;
        }
    }

    public void StopAndRemoveProvider(IDeviceProvider provider)
    {
        lock (_lock)
        {
            var session = _runningSessions.FirstOrDefault(s => s.Provider == provider);
            if (session.Provider != null)
            {
                try { session.Cts.Cancel(); } catch { }
                foreach (var vc in session.VirtualPads.Values)
                {
                    try { vc.Disconnect(); } catch { }
                }
                if (session.Provider is IDisposable disp)
                {
                    try { disp.Dispose(); } catch { }
                }
                _runningSessions.Remove(session);
            }

            _activeProviders.Remove(provider);
        }

        OnDeviceRemoved?.Invoke(provider);
    }

    public static bool ExecuteRevertPnp(string instanceId)
    {
        try
        {
            Console.WriteLine($"Reverting device {instanceId} to default Windows driver via pnputil...");
            var psiRemove = new ProcessStartInfo
            {
                FileName = "pnputil.exe",
                Arguments = $"/remove-device \"{instanceId}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };
            using (var procRemove = Process.Start(psiRemove))
            {
                procRemove?.WaitForExit(10000);
            }

            Thread.Sleep(1500);

            var psiScan = new ProcessStartInfo
            {
                FileName = "pnputil.exe",
                Arguments = "/scan-devices",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };
            using (var procScan = Process.Start(psiScan))
            {
                procScan?.WaitForExit(10000);
            }

            Thread.Sleep(2000);
            Devcon.Refresh();
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error executing revert via pnputil: {ex.Message}");
            try
            {
                var pnpDevice = PnPDevice.GetDeviceByInstanceId(instanceId).ToUsbPnPDevice();
                pnpDevice.InstallNullDriver(out bool reboot1);
                if (reboot1) pnpDevice.CyclePort();
                pnpDevice.Uninstall(out bool reboot2);
                if (reboot2) pnpDevice.CyclePort();
                Devcon.Refresh();
                return true;
            }
            catch (Exception ex2)
            {
                Console.WriteLine($"Fallback revert failed: {ex2.Message}");
                return false;
            }
        }
    }

    public static bool ExecuteSwitchToWinUsbPnp(string instanceId)
    {
        try
        {
            var pnpDevice = PnPDevice.GetDeviceByInstanceId(instanceId).ToUsbPnPDevice();
            pnpDevice.InstallNullDriver(out bool reboot1);
            if (reboot1)
            {
                pnpDevice.CyclePort();
                Thread.Sleep(1500);
                pnpDevice = PnPDevice.GetDeviceByInstanceId(instanceId).ToUsbPnPDevice();
            }

            pnpDevice.InstallCustomDriver("winusb.inf", out bool reboot2);
            if (reboot2)
            {
                pnpDevice.CyclePort();
                Thread.Sleep(1500);
            }

            Devcon.Refresh();
            Thread.Sleep(1000);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error executing switch to WinUSB: {ex.Message}");
            return false;
        }
    }

    public async Task<(bool success, string message)> RevertSpecificDeviceAsync(IDeviceProvider provider)
    {
        IsSwappingDriver = true;
        try
        {
            // 1. Stop and remove ONLY this specific provider from active sessions
            StopAndRemoveProvider(provider);
            await Task.Delay(500); // Allow OS to close device handles

            string? instanceId = provider.InstanceId;
            if (string.IsNullOrEmpty(instanceId))
            {
                var pnpDevices = USBDevice.GetDevices(DeviceInterfaceIds.UsbDevice);
                foreach (var usbDev in pnpDevices)
                {
                    try
                    {
                        var dev = PnPDevice.GetDeviceByInterfaceId(usbDev.DevicePath);
                        if (dev is null) continue;
                        var hwIds = dev.GetProperty<string[]>(DevicePropertyKey.Device_HardwareIds);
                        if (hwIds != null && hwIds.Any(id => id.Contains("VID_1430&PID_079B", StringComparison.OrdinalIgnoreCase)))
                        {
                            instanceId = dev.InstanceId;
                            break;
                        }
                    }
                    catch { }
                }
            }

            if (string.IsNullOrEmpty(instanceId))
            {
                return (false, "Could not find connected Xbox One dongle PnP device.");
            }

            bool success;
            if (IsElevated())
            {
                success = ExecuteRevertPnp(instanceId);
            }
            else
            {
                success = await RunElevatedAsync($"--revert \"{instanceId}\"");
            }

            if (success)
            {
                Devcon.Refresh();
                await Task.Delay(1500);
                Devcon.Refresh();
                return (true, "Xbox One dongle driver successfully reverted to default Windows Xbox driver! The dongle should now light up.");
            }
            else
            {
                return (false, "Failed to revert driver (UAC permission was rejected or driver uninstall failed).");
            }
        }
        catch (Exception ex)
        {
            return (false, $"Failed to revert driver: {ex.Message}");
        }
        finally
        {
            IsSwappingDriver = false;
        }
    }

    public async Task<(bool success, string message)> SwitchSpecificDeviceToWinUsbAsync(IDeviceProvider provider)
    {
        IsSwappingDriver = true;
        try
        {
            // 1. Stop and remove provider from active sessions
            StopAndRemoveProvider(provider);
            await Task.Delay(500); // Allow OS to close device handles

            string? instanceId = provider.InstanceId;
            if (string.IsNullOrEmpty(instanceId))
            {
                var pnpDevices = USBDevice.GetDevices(DeviceInterfaceIds.UsbDevice);
                foreach (var usbDev in pnpDevices)
                {
                    try
                    {
                        var dev = PnPDevice.GetDeviceByInterfaceId(usbDev.DevicePath);
                        if (dev is null) continue;
                        var hwIds = dev.GetProperty<string[]>(DevicePropertyKey.Device_HardwareIds);
                        if (hwIds != null && hwIds.Any(id => id.Contains("VID_1430&PID_079B", StringComparison.OrdinalIgnoreCase)))
                        {
                            instanceId = dev.InstanceId;
                            break;
                        }
                    }
                    catch { }
                }
            }

            if (string.IsNullOrEmpty(instanceId))
            {
                return (false, "Could not find connected Xbox One dongle PnP device.");
            }

            bool success;
            if (IsElevated())
            {
                success = ExecuteSwitchToWinUsbPnp(instanceId);
            }
            else
            {
                success = await RunElevatedAsync($"--winusb \"{instanceId}\"");
            }

            if (success)
            {
                Devcon.Refresh();
                await Task.Delay(1500);
                Devcon.Refresh();
                return (true, "Successfully installed WinUSB driver on Xbox One dongle!");
            }
            else
            {
                return (false, "Failed to install WinUSB driver (UAC permission was rejected or driver install failed).");
            }
        }
        catch (Exception ex)
        {
            return (false, $"Failed to swap driver: {ex.Message}");
        }
        finally
        {
            IsSwappingDriver = false;
        }
    }

    public void ConnectCustomDevice(int vid, int pid, string name)
    {
        Console.WriteLine($"ConnectCustomDevice called for {vid:X4}:{pid:X4} but is not currently supported in the UI for Xbox One dongles.");
    }

    private void StartProvider(IDeviceProvider provider)
    {
        CancellationTokenSource cts = new();
        var virtualControllers = new ConcurrentDictionary<int, IVirtualController>();
        
        lock (_lock)
        {
            _runningSessions.Add((provider, virtualControllers, cts));
        }

        // Virtual controllers are spawned dynamically on first input received for a client ID

        Task.Run(async () =>
        {
            try
            {
                await provider.StartListeningAsync((clientId, state) =>
                {
                    if (cts.IsCancellationRequested) return;

                    if (provider.RequiresVirtualController)
                    {
                        try
                        {
                            var vc = virtualControllers.GetOrAdd(clientId, id =>
                            {
                                var newVc = new VirtualPadEmulator();
                                newVc.Connect();
                                return newVc;
                            });

                            vc?.UpdateState(state);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Error dispatching controller state for client {clientId}: {ex.Message}");
                        }
                    }
                }, cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Expected when device session ends
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Session error for {provider.DeviceName}: {ex.Message}");
            }
            finally
            {
                foreach (var vc in virtualControllers.Values)
                {
                    try { vc.Disconnect(); } catch { }
                }
                virtualControllers.Clear();
            }
        });
    }

    public void Dispose()
    {
        try
        {
            _usbInsertWatcher?.Stop();
            _usbRemoveWatcher?.Stop();
            _usbInsertWatcher?.Dispose();
            _usbRemoveWatcher?.Dispose();
        }
        catch { }

        ClearDevices();
    }
}
