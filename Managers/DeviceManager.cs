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
        // Initial sweep to find already connected dongles
        ScanForDevices();
    }

    public IEnumerable<IDeviceProvider> GetActiveProviders()
    {
        lock (_lock)
        {
            return _activeProviders.ToList();
        }
    }

    public event Action? OnHotplugRescanRequired;
    public volatile bool IsSwappingDriver = false;

    private void SetupWmiHotplugWatcher()
    {
        Task.Run(() =>
        {
            try
            {
                // Specifically filter for GHLive dongle hardware IDs (Xbox One 1430:079B and PS3/WiiU 12BA:074B)
                // This avoids waking up on every unrelated USB event (mice, keyboards, hubs)
                _usbInsertWatcher = new ManagementEventWatcher(new WqlEventQuery(
                    "SELECT * FROM __InstanceCreationEvent WITHIN 2 WHERE TargetInstance ISA 'Win32_PnPEntity' AND " +
                    "(TargetInstance.PNPDeviceID LIKE '%VID_1430&PID_079B%' OR TargetInstance.PNPDeviceID LIKE '%VID_12BA&PID_074B%')"));
                _usbInsertWatcher.EventArrived += UsbDeviceInserted;
                _usbInsertWatcher.Start();

                _usbRemoveWatcher = new ManagementEventWatcher(new WqlEventQuery(
                    "SELECT * FROM __InstanceDeletionEvent WITHIN 2 WHERE TargetInstance ISA 'Win32_PnPEntity' AND " +
                    "(TargetInstance.PNPDeviceID LIKE '%VID_1430&PID_079B%' OR TargetInstance.PNPDeviceID LIKE '%VID_12BA&PID_074B%')"));
                _usbRemoveWatcher.EventArrived += UsbDeviceRemoved;
                _usbRemoveWatcher.Start();
            }
            catch (Exception ex)
            {
                Console.WriteLine("WMI Watcher failed: " + ex.Message);
            }
        });
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

        var foundPnpInstanceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var foundHidDevicePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Sweep Xbox One Dongles (1430:079B) via WMI PnP
        logBuilder.AppendLine("[WMI PnP Enumeration]");
        try
        {
            var query = new System.Management.WqlObjectQuery("SELECT DeviceID, PNPDeviceID FROM Win32_PnPEntity WHERE PNPDeviceID LIKE 'USB\\\\VID_1430&PID_079B%'");
            using (var searcher = new System.Management.ManagementObjectSearcher(query))
            {
                // Pre-fetch WinUSB device interface paths once
                List<USBDeviceInfo> pnpDevices = new();
                try
                {
                    pnpDevices.AddRange(USBDevice.GetDevices(DeviceInterfaceIds.UsbDevice));
                    pnpDevices.AddRange(USBDevice.GetDevices(Guid.Parse("d5ff2009-46be-4b95-bdc7-e322cd81f57d")));
                }
                catch { }

                foreach (var mbo in searcher.Get())
                {
                    string pnpId = mbo["PNPDeviceID"]?.ToString() ?? "";
                    string instanceId = mbo["DeviceID"]?.ToString() ?? "";
                    if (string.IsNullOrEmpty(instanceId)) continue;

                    foundPnpInstanceIds.Add(instanceId);
                    logBuilder.AppendLine($"Found WMI Device: {pnpId}");

                    try
                    {
                        // Match with WinUSB interfaces
                        string winUsbDevPath = "";
                        foreach (var p in pnpDevices)
                        {
                            string normPath = p.DevicePath.Replace('#', '\\');
                            if (normPath.Contains(instanceId, StringComparison.OrdinalIgnoreCase))
                            {
                                winUsbDevPath = p.DevicePath;
                                break;
                            }
                            try
                            {
                                var pnp = PnPDevice.GetDeviceByInterfaceId(p.DevicePath);
                                if (pnp is not null && string.Equals(pnp.InstanceId, instanceId, StringComparison.OrdinalIgnoreCase))
                                {
                                    winUsbDevPath = p.DevicePath;
                                    break;
                                }
                            }
                            catch { }
                        }

                        bool hasWinUsb = !string.IsNullOrEmpty(winUsbDevPath);

                        lock (_lock)
                        {
                            IDeviceProvider? existing = _activeProviders.FirstOrDefault(p => string.Equals(p.InstanceId, instanceId, StringComparison.OrdinalIgnoreCase));

                            if (!hasWinUsb)
                            {
                                logBuilder.AppendLine($"Found Xbox One Dongle (1430:079B) using default Windows driver.");
                                if (existing is XboxOneGhlProvider)
                                {
                                    StopAndRemoveProvider(existing);
                                    existing = null;
                                }

                                if (existing == null)
                                {
                                    var provider = new XboxOneDefaultDriverProvider(instanceId, instanceId);
                                    _activeProviders.Add(provider);
                                    OnDeviceAdded?.Invoke(provider);
                                    StartProvider(provider);
                                }
                            }
                            else
                            {
                                logBuilder.AppendLine($"Found Xbox One Dongle (1430:079B) using WinUSB.");
                                if (existing is XboxOneDefaultDriverProvider)
                                {
                                    StopAndRemoveProvider(existing);
                                    existing = null;
                                }

                                if (existing == null)
                                {
                                    var provider = new XboxOneGhlProvider(winUsbDevPath, "GHLive Guitar (Xbox One WinUSB)", instanceId);
                                    _activeProviders.Add(provider);
                                    OnDeviceAdded?.Invoke(provider);
                                    StartProvider(provider);
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
        }
        catch (Exception ex)
        {
            logBuilder.AppendLine($"WMI Enumeration failed: {ex.Message}");
        }

        // 2. Sweep PS3/Wii U Dongles (12BA:074B) using HidSharp
        logBuilder.AppendLine("\n[HidSharp Native HID Enumeration]");
        try
        {
            foreach (var hidDevice in HidSharp.DeviceList.Local.GetHidDevices())
            {
                try
                {
                    if (hidDevice.VendorID == 0x12BA && hidDevice.ProductID == 0x074B)
                    {
                        foundHidDevicePaths.Add(hidDevice.DevicePath);
                        logBuilder.AppendLine($"VID: 0x12BA, PID: 0x074B | {hidDevice.DevicePath}");

                        lock (_lock)
                        {
                            bool alreadyAdded = _activeProviders.Any(p => string.Equals(p.DevicePath, hidDevice.DevicePath, StringComparison.OrdinalIgnoreCase));
                            if (!alreadyAdded)
                            {
                                var provider = new GHLiveHidProvider(hidDevice, "GHLive Guitar (Wii/PS3 Native HID)");
                                _activeProviders.Add(provider);
                                OnDeviceAdded?.Invoke(provider);
                                StartProvider(provider);
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

        // 3. Reconcile removals: Stop only providers whose hardware was disconnected
        var providersToRemove = new List<IDeviceProvider>();
        lock (_lock)
        {
            foreach (var provider in _activeProviders)
            {
                if (provider is XboxOneGhlProvider or XboxOneDefaultDriverProvider)
                {
                    if (!string.IsNullOrEmpty(provider.InstanceId) && !foundPnpInstanceIds.Contains(provider.InstanceId))
                    {
                        providersToRemove.Add(provider);
                    }
                }
                else if (provider is GHLiveHidProvider)
                {
                    if (!foundHidDevicePaths.Contains(provider.DevicePath))
                    {
                        providersToRemove.Add(provider);
                    }
                }
            }
        }

        foreach (var p in providersToRemove)
        {
            logBuilder.AppendLine($"Removing disconnected device: {p.DeviceName} ({p.InstanceId ?? p.DevicePath})");
            StopAndRemoveProvider(p);
        }

        lock (_lock)
        {
            devicesFound = _activeProviders.Count;
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
