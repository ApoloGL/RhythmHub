using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;

namespace RhythmHub.ViewModels;

public partial class DeviceViewModel : ObservableObject
{
    [ObservableProperty] private string _deviceName = "";
    [ObservableProperty] private string _connectionStatus = "";
    [ObservableProperty] private bool _isWaitingForSync;
    [ObservableProperty] private SolidColorBrush _statusColor = new SolidColorBrush(Colors.Transparent);

    public string SyncInstruction => Provider is Providers.XboxOneDefaultDriverProvider
        ? "Running default Microsoft Xbox driver (LEDs active). Plays via GameInput/RB4IM. Switch to WinUSB to use RhythmHub's built-in mapper."
        : "Please press the Sync button on your guitar!";

    public string ProtocolName => Provider is Providers.XboxOneGhlProvider ? "Xbox One WinUSB" : "Xbox One Native HID";
    public string IconGlyph => "\xE990"; // Segoe Fluent Icons Gamepad

    public Microsoft.UI.Xaml.Visibility SyncInstructionVisibility => 
        (IsWaitingForSync || Provider is Providers.XboxOneDefaultDriverProvider) 
            ? Microsoft.UI.Xaml.Visibility.Visible 
            : Microsoft.UI.Xaml.Visibility.Collapsed;

    public bool CanRevertDriver => Provider is Providers.XboxOneGhlProvider;
    public bool CanSwitchToWinUsb => Provider is Providers.XboxOneDefaultDriverProvider;

    public Microsoft.UI.Xaml.Visibility RevertDriverVisibility =>
        CanRevertDriver ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    public Microsoft.UI.Xaml.Visibility SwitchToWinUsbVisibility =>
        CanSwitchToWinUsb ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    public Providers.IDeviceProvider Provider { get; }

    public CommunityToolkit.Mvvm.Input.IAsyncRelayCommand<DeviceViewModel> RevertDriverCommand { get; }
    public CommunityToolkit.Mvvm.Input.IAsyncRelayCommand<DeviceViewModel> SwitchToWinUsbCommand { get; }

    public DeviceViewModel(
        Providers.IDeviceProvider provider, 
        CommunityToolkit.Mvvm.Input.IAsyncRelayCommand<DeviceViewModel> revertCmd, 
        CommunityToolkit.Mvvm.Input.IAsyncRelayCommand<DeviceViewModel> switchCmd)
    {
        Provider = provider;
        RevertDriverCommand = revertCmd;
        SwitchToWinUsbCommand = switchCmd;
        DeviceName = provider.DeviceName;
        UpdateStatus(provider.IsSynced);
    }

    public void UpdateStatus(bool isSynced)
    {
        if (Provider is Providers.XboxOneDefaultDriverProvider)
        {
            ConnectionStatus = "Default Windows Driver (Ready)";
            StatusColor = new SolidColorBrush(Colors.DeepSkyBlue);
            IsWaitingForSync = false;
        }
        else if (isSynced)
        {
            ConnectionStatus = "Synced & Active";
            StatusColor = new SolidColorBrush(Colors.LimeGreen);
            IsWaitingForSync = false;
        }
        else
        {
            if (!string.IsNullOrEmpty(Provider.ErrorMessage))
            {
                ConnectionStatus = $"Error: {Provider.ErrorMessage}";
                StatusColor = new SolidColorBrush(Colors.Red);
            }
            else
            {
                ConnectionStatus = "Dongle Detected";
                StatusColor = new SolidColorBrush(Colors.Yellow);
            }
            IsWaitingForSync = true;
        }
        OnPropertyChanged(nameof(SyncInstruction));
        OnPropertyChanged(nameof(SyncInstructionVisibility));
        OnPropertyChanged(nameof(CanRevertDriver));
        OnPropertyChanged(nameof(CanSwitchToWinUsb));
        OnPropertyChanged(nameof(RevertDriverVisibility));
        OnPropertyChanged(nameof(SwitchToWinUsbVisibility));
    }
}
