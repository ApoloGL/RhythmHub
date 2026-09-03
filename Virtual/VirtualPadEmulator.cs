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

    // Previous state cache for delta-only submissions
    private bool _hasPrevState;
    private ushort _prevButtons;
    private short _prevStrum;
    private short _prevWhammy;
    private short _prevTilt;

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
                    _hasPrevState = false;
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
            _hasPrevState = false;
            _prevButtons = 0;
            _prevStrum = 0;
            _prevWhammy = 0;
            _prevTilt = 0;

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
                // 1. Pack discrete button states into a single 16-bit integer for fast delta checking
                ushort buttons = 0;
                if (state.Green) buttons |= (1 << 0);
                if (state.Red) buttons |= (1 << 1);
                if (state.Yellow) buttons |= (1 << 2);
                if (state.Blue) buttons |= (1 << 3);
                if (state.Orange) buttons |= (1 << 4);
                if (state.White3) buttons |= (1 << 5);
                if (state.StrumUp || state.DpadUp) buttons |= (1 << 6);
                if (state.StrumDown || state.DpadDown) buttons |= (1 << 7);
                if (state.DpadLeft) buttons |= (1 << 8);
                if (state.DpadRight) buttons |= (1 << 9);
                if (state.Start) buttons |= (1 << 10);
                if (state.Select) buttons |= (1 << 11);
                if (state.HeroPower) buttons |= (1 << 12);

                // 2. Compute target discrete axis values
                short strumValue = state.StrumUp ? short.MaxValue : (state.StrumDown ? short.MinValue : (short)0);
                short whammyValue = (short)Math.Clamp(state.Whammy * 32767f, -32768f, 32767f);
                short tiltValue = (short)((state.Tilt * 65535f) - 32768);

                // 3. Delta-Only check: If inputs and axes are identical to previous frame, skip kernel IOCTL
                if (_hasPrevState &&
                    buttons == _prevButtons &&
                    strumValue == _prevStrum &&
                    whammyValue == _prevWhammy &&
                    tiltValue == _prevTilt)
                {
                    return;
                }

                _prevButtons = buttons;
                _prevStrum = strumValue;
                _prevWhammy = whammyValue;
                _prevTilt = tiltValue;
                _hasPrevState = true;

                // 4. Update ViGEm controller state only when a delta is detected
                // Frets (GHLive 6-fret mapping on Xbox 360 controller)
                _controller.SetButtonState(Xbox360Button.A, (buttons & (1 << 0)) != 0);              // Black 1
                _controller.SetButtonState(Xbox360Button.B, (buttons & (1 << 1)) != 0);              // Black 2
                _controller.SetButtonState(Xbox360Button.Y, (buttons & (1 << 2)) != 0);              // Black 3
                _controller.SetButtonState(Xbox360Button.X, (buttons & (1 << 3)) != 0);              // White 1
                _controller.SetButtonState(Xbox360Button.LeftShoulder, (buttons & (1 << 4)) != 0);   // White 2
                _controller.SetButtonState(Xbox360Button.RightShoulder, (buttons & (1 << 5)) != 0);  // White 3

                // D-Pad and Strum buttons (Combine Strum and D-Pad for full compatibility)
                _controller.SetButtonState(Xbox360Button.Up, (buttons & (1 << 6)) != 0);
                _controller.SetButtonState(Xbox360Button.Down, (buttons & (1 << 7)) != 0);
                _controller.SetButtonState(Xbox360Button.Left, (buttons & (1 << 8)) != 0);
                _controller.SetButtonState(Xbox360Button.Right, (buttons & (1 << 9)) != 0);

                // Strumbar on Left Stick Y axis (Essential for Clone Hero & GHL simultaneous fret + strum gameplay)
                _controller.SetAxisValue(Xbox360Axis.LeftThumbY, strumValue);

                // Menu buttons
                _controller.SetButtonState(Xbox360Button.Start, (buttons & (1 << 10)) != 0);
                _controller.SetButtonState(Xbox360Button.Back, (buttons & (1 << 11)) != 0);
                _controller.SetButtonState(Xbox360Button.LeftThumb, (buttons & (1 << 12)) != 0);

                // Whammy on Right Stick Y
                _controller.SetAxisValue(Xbox360Axis.RightThumbY, whammyValue);

                // Tilt on Right Stick X
                _controller.SetAxisValue(Xbox360Axis.RightThumbX, tiltValue);

                // 5. CRITICAL: Submit report to Windows / ViGEm kernel driver ONLY on actual state change
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
