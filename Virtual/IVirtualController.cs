using RhythmHub.Models;

namespace RhythmHub.Virtual;

public interface IVirtualController
{
    void Connect();
    void Disconnect();
    void UpdateState(InstrumentState state);
}
