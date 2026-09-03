using System;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;
using RhythmHub.Models;

namespace RhythmHub.Virtual;

public class VirtualPadEmulator : IVirtualController, IDisposable
{
    private ViGEmClient? _client;
    private IXbox360Controller? _controller;
    private readonly object _lock = new();

    public void Connect()
    {
        lock (_lock)
        {
            if (_client == null)
            {
                try
                {
                    _client = new ViGEmClient();
                    _controller = _client.CreateXbox360Controller();
                    _controller.AutoSubmitReport = false;
                    _controller.Connect();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to connect VirtualPad: {ex.Message}");
                    _controller = null;
                    _client?.Dispose();
                    _client = null;
                }
            }
        }
    }

    public void Disconnect()
    {
        lock (_lock)
        {
            try
            {
                if (_controller != null)
                {
                    try
                    {
                        _controller.ResetReport();
                        _controller.SubmitReport();
                    }
                    catch { }
                    _controller.Disconnect();
                    _controller = null;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error disconnecting controller: {ex.Message}");
            }

            try
            {
                if (_client != null)
                {
                    _client.Dispose();
                    _client = null;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error disposing ViGEm client: {ex.Message}");
            }
        }
    }

    public void UpdateState(InstrumentState state)
    {
        lock (_lock)
        {
            if (_controller == null)
            {
                return;
            }

            try
            {
                // Frets (GHLive 6-fret mapping on Xbox 360 controller)
                _controller.SetButtonState(Xbox360Button.A, state.Green);              // Black 1
                _controller.SetButtonState(Xbox360Button.B, state.Red);                // Black 2
                _controller.SetButtonState(Xbox360Button.Y, state.Yellow);             // Black 3
                _controller.SetButtonState(Xbox360Button.X, state.Blue);               // White 1
                _controller.SetButtonState(Xbox360Button.LeftShoulder, state.Orange);  // White 2
                _controller.SetButtonState(Xbox360Button.RightShoulder, state.White3); // White 3

                // D-Pad and Strum buttons (Combine Strum and D-Pad for full compatibility)
                _controller.SetButtonState(Xbox360Button.Up, state.StrumUp || state.DpadUp);
                _controller.SetButtonState(Xbox360Button.Down, state.StrumDown || state.DpadDown);
                _controller.SetButtonState(Xbox360Button.Left, state.DpadLeft);
                _controller.SetButtonState(Xbox360Button.Right, state.DpadRight);

                // Strumbar on Left Stick Y axis (Essential for Clone Hero & GHL simultaneous fret + strum gameplay)
                short strumValue = state.StrumUp ? short.MaxValue : (state.StrumDown ? short.MinValue : (short)0);
                _controller.SetAxisValue(Xbox360Axis.LeftThumbY, strumValue);

                // Menu buttons
                _controller.SetButtonState(Xbox360Button.Start, state.Start);
                _controller.SetButtonState(Xbox360Button.Back, state.Select);
                _controller.SetButtonState(Xbox360Button.LeftThumb, state.HeroPower);

                // Whammy on Right Stick Y (Scale -1.0 to 1.0 to -32767 to 32767)
                short whammyValue = (short)Math.Clamp(state.Whammy * 32767f, -32768f, 32767f);
                _controller.SetAxisValue(Xbox360Axis.RightThumbY, whammyValue);

                // Tilt on Right Stick X (Scale 0.0-1.0 to -32768 to 32767)
                short tiltValue = (short)((state.Tilt * 65535f) - 32768);
                _controller.SetAxisValue(Xbox360Axis.RightThumbX, tiltValue);

                // CRITICAL: Submit report to Windows / XInput subsystem
                _controller.SubmitReport();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error updating controller state: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        Disconnect();
    }
}
