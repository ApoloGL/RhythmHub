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

    public string SyncInstruction => "Please press the Sync button on your guitar!";

    public string ProtocolName => Provider switch
    {
        Providers.XboxOneGhlProvider => "WinUSB Driver",
        Providers.XboxOneDefaultDriverProvider => "Default Driver",
        Providers.GHLiveHidProvider => "Native HID",
        _ => "Native HID"
    };

    public string IconGlyph => "\xE7FC"; // Segoe Fluent Icons Game controller glyph

    public Microsoft.UI.Xaml.Visibility SyncInstructionVisibility => 
        IsWaitingForSync ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    public bool CanRevertDriver => Provider is Providers.XboxOneGhlProvider;
    public bool CanSwitchToWinUsb => Provider is Providers.XboxOneDefaultDriverProvider;

    public Microsoft.UI.Xaml.Visibility RevertDriverVisibility =>
        CanRevertDriver ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    public Microsoft.UI.Xaml.Visibility SwitchToWinUsbVisibility =>
        CanSwitchToWinUsb ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    public Providers.IDeviceProvider Provider { get; }
    public CommunityToolkit.Mvvm.Input.IAsyncRelayCommand<DeviceViewModel> ResetDongleCommand { get; }
    public CommunityToolkit.Mvvm.Input.IAsyncRelayCommand<DeviceViewModel> RevertDriverCommand { get; }
    public CommunityToolkit.Mvvm.Input.IAsyncRelayCommand<DeviceViewModel> SwitchToWinUsbCommand { get; }

    public DeviceViewModel(
        Providers.IDeviceProvider provider, 
        CommunityToolkit.Mvvm.Input.IAsyncRelayCommand<DeviceViewModel> resetCmd,
        CommunityToolkit.Mvvm.Input.IAsyncRelayCommand<DeviceViewModel> revertCmd,
        CommunityToolkit.Mvvm.Input.IAsyncRelayCommand<DeviceViewModel> switchCmd)
    {
        Provider = provider;
        ResetDongleCommand = resetCmd;
        RevertDriverCommand = revertCmd;
        SwitchToWinUsbCommand = switchCmd;
        DeviceName = provider.DeviceName;
        UpdateStatus(provider.IsSynced);
    }

    private static readonly SolidColorBrush DeepSkyBlueBrush = new(Colors.DeepSkyBlue);
    private static readonly SolidColorBrush LimeGreenBrush = new(Colors.LimeGreen);
    private static readonly SolidColorBrush RedBrush = new(Colors.Red);
    private static readonly SolidColorBrush YellowBrush = new(Colors.Yellow);

    private bool? _lastSyncedState;
    private string? _lastErrorMessage;

    public void UpdateStatus(bool isSynced)
    {
        string currentError = Provider.ErrorMessage ?? "";
        if (_lastSyncedState == isSynced && _lastErrorMessage == currentError)
        {
            return;
        }

        _lastSyncedState = isSynced;
        _lastErrorMessage = currentError;

        if (Provider is Providers.XboxOneDefaultDriverProvider)
        {
            ConnectionStatus = "Default Windows Driver (Ready)";
            StatusColor = DeepSkyBlueBrush;
            IsWaitingForSync = false;
        }
        else if (isSynced)
        {
            ConnectionStatus = "Synced & Active";
            StatusColor = LimeGreenBrush;
            IsWaitingForSync = false;
        }
        else
        {
            if (!string.IsNullOrEmpty(currentError))
            {
                ConnectionStatus = $"Error: {currentError}";
                StatusColor = RedBrush;
            }
            else
            {
                ConnectionStatus = "Dongle Detected";
                StatusColor = YellowBrush;
            }
            IsWaitingForSync = true;
        }
        OnPropertyChanged(nameof(SyncInstructionVisibility));
        OnPropertyChanged(nameof(CanRevertDriver));
        OnPropertyChanged(nameof(CanSwitchToWinUsb));
        OnPropertyChanged(nameof(RevertDriverVisibility));
        OnPropertyChanged(nameof(SwitchToWinUsbVisibility));
    }
}
