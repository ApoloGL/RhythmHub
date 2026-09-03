using System;
using System.Threading;
using System.Threading.Tasks;
using RhythmHub.Models;

namespace RhythmHub.Providers;

public interface IDeviceProvider
{
    string DeviceName { get; }
    string DevicePath { get; }
    string? InstanceId { get; }
    bool IsSynced { get; }
    string? ErrorMessage { get; }
    bool RequiresVirtualController { get; }
    
    /// <summary>
    /// Starts a high-priority loop listening to the physical device.
    /// </summary>
    Task StartListeningAsync(Action<int, InstrumentState> onStateChanged, CancellationToken token);
}
