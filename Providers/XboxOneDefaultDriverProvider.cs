using RhythmHub.Models;

namespace RhythmHub.Providers;

public class XboxOneDefaultDriverProvider : IDeviceProvider
{
    public string DeviceName => "Xbox One GHL Dongle (Default Driver)";
    public string DevicePath { get; }
    public string? InstanceId { get; }
    public bool IsSynced => true;
    public string? ErrorMessage => null;
    public bool RequiresVirtualController => false;

    public XboxOneDefaultDriverProvider(string devicePath, string? instanceId = null)
    {
        DevicePath = devicePath;
        InstanceId = instanceId;
    }

    public Task StartListeningAsync(Action<int, InstrumentState> onStateChanged, CancellationToken token)
    {
        // On the default Microsoft Windows driver, the dongle lights up and operates natively
        // via GameInput/xusb22 (e.g. in Clone Hero or RB4InstrumentMapper).
        // This provider keeps the device visible in RhythmHub so the user can inspect it and switch drivers at will.
        return Task.Delay(-1, token);
    }
}
