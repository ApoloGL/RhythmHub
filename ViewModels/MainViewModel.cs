using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RhythmHub.Managers;
using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppLifecycle;
using System.Threading.Tasks;

namespace RhythmHub.ViewModels;

public partial class MainViewModel : ObservableObject
{
    public ObservableCollection<DeviceViewModel> ConnectedDevices { get; } = new();
    
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDevices))]
    private bool _isEmptyState = true;
    
    public bool HasDevices => !IsEmptyState;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDeviceSelected))]
    private DeviceViewModel? _selectedDevice;

    public Microsoft.UI.Xaml.Visibility IsDeviceSelected => 
        SelectedDevice != null ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    private readonly DeviceManager _deviceManager;
    private readonly DispatcherQueue _dispatcherQueue;

    public MainViewModel()
    {
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _deviceManager = new DeviceManager();
        _deviceManager.OnDeviceAdded += DeviceManager_OnDeviceAdded;
        _deviceManager.OnDeviceRemoved += DeviceManager_OnDeviceRemoved;
        _deviceManager.OnDevicesCleared += DeviceManager_OnDevicesCleared;
        _deviceManager.OnHotplugEvent += DeviceManager_OnHotplugEvent;
        _deviceManager.OnHotplugRescanRequired += DeviceManager_OnHotplugRescanRequired;
        
        _ = PerformScan();
        
        // Background loop to poll IsSynced status for UI
        Task.Run(async () =>
        {
            while (true)
            {
                await Task.Delay(500);
                
                _dispatcherQueue.TryEnqueue(() =>
                {
                    try
                    {
                        var list = ConnectedDevices.ToList();
                        foreach (var device in list)
                        {
                            if (device.Provider != null)
                            {
                                device.UpdateStatus(device.Provider.IsSynced);
                            }
                        }
                    }
                    catch { }
                });
            }
        });
    }

    [ObservableProperty]
    private bool _isNotificationOpen;

    [ObservableProperty]
    private Microsoft.UI.Xaml.Controls.InfoBarSeverity _notificationSeverity;

    [ObservableProperty]
    private string _notificationTitle = "";

    [ObservableProperty]
    private string _notificationMessage = "";

    private CancellationTokenSource? _hotplugDebounceCts;
    private readonly object _hotplugLock = new();
    private CancellationTokenSource? _notificationCts;
    private bool _isScanning = false;
    private bool _isSwappingDriverMain = false;

    private void DeviceManager_OnHotplugEvent(string title, string message, Microsoft.UI.Xaml.Controls.InfoBarSeverity severity)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            NotificationTitle = title;
            NotificationMessage = message;
            NotificationSeverity = severity;
            IsNotificationOpen = true;

            _notificationCts?.Cancel();
            _notificationCts?.Dispose();
            _notificationCts = new CancellationTokenSource();
            var token = _notificationCts.Token;

            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(5000, token);
                    if (!token.IsCancellationRequested)
                    {
                        _dispatcherQueue.TryEnqueue(() =>
                        {
                            IsNotificationOpen = false;
                        });
                    }
                }
                catch (OperationCanceledException) { }
            }, token);
        });
    }

    private void DeviceManager_OnHotplugRescanRequired()
    {
        if (_deviceManager.IsSwappingDriver || _isSwappingDriverMain) return;

        lock (_hotplugLock)
        {
            _hotplugDebounceCts?.Cancel();
            _hotplugDebounceCts?.Dispose();
            _hotplugDebounceCts = new CancellationTokenSource();
            var token = _hotplugDebounceCts.Token;

            Task.Run(async () =>
            {
                try
                {
                    // Debounce rapid plug/unplug events (give Windows PnP 1.5s to settle)
                    await Task.Delay(1500, token);
                    if (!token.IsCancellationRequested && !_deviceManager.IsSwappingDriver && !_isSwappingDriverMain)
                    {
                        _dispatcherQueue.TryEnqueue(async () =>
                        {
                            await PerformScan();
                        });
                    }
                }
                catch (OperationCanceledException)
                {
                    // A newer hotplug event arrived, this one is debounced
                }
            }, token);
        }
    }

    private void DeviceManager_OnDevicesCleared()
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            ConnectedDevices.Clear();
            SelectedDevice = null;
            IsEmptyState = true;
        });
    }

    private void DeviceManager_OnDeviceRemoved(Providers.IDeviceProvider provider)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            var existingVm = ConnectedDevices.FirstOrDefault(vm => vm.Provider == provider);
            if (existingVm != null)
            {
                ConnectedDevices.Remove(existingVm);
                if (SelectedDevice == existingVm)
                    SelectedDevice = ConnectedDevices.FirstOrDefault();
                IsEmptyState = ConnectedDevices.Count == 0;
            }
        });
    }

    [ObservableProperty]
    private string _diagnosticLog = "";

    [RelayCommand]
    private async Task PerformScan()
    {
        if (_isScanning) return;
        _isScanning = true;

        try
        {
            _deviceManager.ClearDevices();
            // Short buffer for Windows to release any pending file/device handles
            await Task.Delay(200);

            var result = await Task.Run(() => _deviceManager.ScanForDevices());
            DiagnosticLog = result.log;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error during scan: {ex.Message}");
        }
        finally
        {
            _isScanning = false;
        }
    }

    [ObservableProperty]
    private string _customVid = "";

    [ObservableProperty]
    private string _customPid = "";

    [ObservableProperty]
    private string _customName = "";

    [RelayCommand]
    private void ConnectManual()
    {
        if (string.IsNullOrWhiteSpace(CustomVid) || string.IsNullOrWhiteSpace(CustomPid))
        {
            DeviceManager_OnHotplugEvent("Input Error", "Please provide both VID and PID (e.g., 1430 and 079B).", Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error);
            return;
        }

        try
        {
            int vid = Convert.ToInt32(CustomVid.Trim(), 16);
            int pid = Convert.ToInt32(CustomPid.Trim(), 16);
            string name = string.IsNullOrWhiteSpace(CustomName) ? "Custom Device" : CustomName.Trim();

            _deviceManager.ConnectCustomDevice(vid, pid, name);
            DeviceManager_OnHotplugEvent("Device Added", $"Attempted manual connection for VID: 0x{vid:X4}, PID: 0x{pid:X4}.", Microsoft.UI.Xaml.Controls.InfoBarSeverity.Informational);
            _ = PerformScan();
        }
        catch (Exception ex)
        {
            DeviceManager_OnHotplugEvent("Parse Error", $"Invalid hex format: {ex.Message}", Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error);
        }
    }

    [RelayCommand]
    private void RestartApp()
    {
        AppInstance.Restart("");
    }

    [RelayCommand]
    private async Task RevertXboxDriver()
    {
        if (SelectedDevice?.Provider == null) return;

        var targetDevice = SelectedDevice;
        DeviceManager_OnHotplugEvent("Driver Reversion", "Reverting Xbox One dongle to default Windows driver... Please wait.", Microsoft.UI.Xaml.Controls.InfoBarSeverity.Informational);

        _isSwappingDriverMain = true;
        try
        {
            var (success, resultMessage) = await _deviceManager.RevertSpecificDeviceAsync(targetDevice.Provider);

            if (success)
            {
                await Task.Delay(2000);
                await PerformScan();
            }

            DeviceManager_OnHotplugEvent(
                success ? "Xbox One Driver Reverted" : "Driver Revert Failed",
                resultMessage,
                success ? Microsoft.UI.Xaml.Controls.InfoBarSeverity.Success : Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error);
        }
        catch (Exception ex)
        {
            DeviceManager_OnHotplugEvent("Driver Revert Failed", ex.Message, Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error);
        }
        finally
        {
            _isSwappingDriverMain = false;
        }
    }

    [RelayCommand]
    private async Task SwitchToWinUsbDriver()
    {
        if (SelectedDevice?.Provider == null) return;

        var targetDevice = SelectedDevice;
        DeviceManager_OnHotplugEvent("Driver Installation", "Switching Xbox One dongle to WinUSB driver... Please wait.", Microsoft.UI.Xaml.Controls.InfoBarSeverity.Informational);

        _isSwappingDriverMain = true;
        try
        {
            var (success, resultMessage) = await _deviceManager.SwitchSpecificDeviceToWinUsbAsync(targetDevice.Provider);

            if (success)
            {
                await Task.Delay(2000);
                await PerformScan();
            }

            DeviceManager_OnHotplugEvent(
                success ? "Switched to WinUSB" : "Driver Swap Failed",
                resultMessage,
                success ? Microsoft.UI.Xaml.Controls.InfoBarSeverity.Success : Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error);
        }
        catch (Exception ex)
        {
            DeviceManager_OnHotplugEvent("Driver Swap Failed", ex.Message, Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error);
        }
        finally
        {
            _isSwappingDriverMain = false;
        }
    }

    private void DeviceManager_OnDeviceAdded(Providers.IDeviceProvider provider)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            // Remove any obsolete VM with the same device path or instance ID
            var existingVm = ConnectedDevices.FirstOrDefault(vm => 
                string.Equals(vm.Provider.DevicePath, provider.DevicePath, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrEmpty(provider.InstanceId) && string.Equals(vm.Provider.InstanceId, provider.InstanceId, StringComparison.OrdinalIgnoreCase)));

            if (existingVm != null)
            {
                ConnectedDevices.Remove(existingVm);
            }

            var vm = new DeviceViewModel(provider);
            ConnectedDevices.Add(vm);
            IsEmptyState = false;
            
            if (SelectedDevice == null || SelectedDevice == existingVm)
                SelectedDevice = vm;
        });
    }
}
