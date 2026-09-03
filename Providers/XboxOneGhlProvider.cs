using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nefarius.Drivers.WinUSB;
using RhythmHub.Models;

namespace RhythmHub.Providers;

public class XboxOneGhlProvider : IDeviceProvider, IDisposable
{
    public string DeviceName { get; }
    public string DevicePath => _devicePath;
    public bool IsSynced { get; private set; }
    public string? ErrorMessage { get; private set; }
    public bool RequiresVirtualController => true;

    public string? InstanceId { get; }
    private readonly string _devicePath;
    private USBDevice? _winUsbDevice;
    private USBInterface? _mainInterface;
    private readonly HashSet<byte> _connectedClients = new();
    
    public XboxOneGhlProvider(string devicePath, string deviceName = "GHLive Guitar (Xbox One WinUSB)", string? instanceId = null)
    {
        _devicePath = devicePath;
        DeviceName = deviceName;
        InstanceId = instanceId;
    }

    public async Task StartListeningAsync(Action<int, InstrumentState> onStateChanged, CancellationToken token)
    {
        Thread? readThread = null;
        Task? pokeTask = null;
        bool keepReading = true;

        try
        {
            _winUsbDevice = USBDevice.GetSingleDeviceByPath(_devicePath);
            if (_winUsbDevice == null)
            {
                Console.WriteLine("Failed to open Xbox One dongle via WinUSB!");
                return;
            }

            // Find the main interface which uses interrupt transfers (typically 0xFF, 0x47, 0xD0)
            foreach (var iface in _winUsbDevice.Interfaces)
            {
                if (iface.ClassValue == 0xFF && iface.SubClass == 0x47 && iface.Protocol == 0xD0)
                {
                    if (iface.InPipe?.TransferType == USBTransferType.Interrupt &&
                        iface.OutPipe?.TransferType == USBTransferType.Interrupt)
                    {
                        _mainInterface = iface;
                        break;
                    }
                }
            }

            // Fallback if not specifically that class/subclass
            if (_mainInterface == null)
            {
                 _mainInterface = _winUsbDevice.Interfaces.FirstOrDefault(i => 
                     i.InPipe?.TransferType == USBTransferType.Interrupt && 
                     i.OutPipe?.TransferType == USBTransferType.Interrupt);
            }

            if (_mainInterface == null || _mainInterface.InPipe == null)
            {
                Console.WriteLine("Could not find the interrupt interface for the Xbox One dongle!");
                return;
            }

            Console.WriteLine("Started WinUSB capture on Xbox One dongle.");

            // Register cancellation callback to abort the in pipe immediately when token is canceled
            using var cancelReg = token.Register(() =>
            {
                keepReading = false;
                try
                {
                    _mainInterface?.InPipe?.Abort();
                }
                catch { }
            });

            // Start the heartbeat poke loop in the background
            pokeTask = PokeLoopAsync(token);

            // Run blocking read on a background thread so we can join it cleanly before disposing
            readThread = new Thread(() =>
            {
                try
                {
                    byte[] readBuffer = new byte[_mainInterface.InPipe.MaximumPacketSize];

                    while (keepReading && !token.IsCancellationRequested)
                    {
                        int bytesRead = -1;
                        try
                        {
                            bytesRead = _mainInterface.InPipe.Read(readBuffer);
                        }
                        catch
                        {
                            // Pipe aborted or device disconnected
                            break;
                        }

                        int offset = 0;
                        while (offset + 4 <= bytesRead)
                        {
                            byte cmdId = readBuffer[offset];
                            byte flagsClient = readBuffer[offset + 1];
                            byte seq = readBuffer[offset + 2];
                            byte payloadLen = readBuffer[offset + 3];
                            byte clientId = (byte)(flagsClient & 0x07);

                            int totalMsgLen = 4 + payloadLen;
                            if (offset + totalMsgLen > bytesRead)
                                break;

                            // If packet requires acknowledgement, send Ack immediately
                            if ((flagsClient & 0x10) != 0)
                            {
                                SendAck(cmdId, flagsClient, seq, totalMsgLen);
                            }

                            // Handle Arrival (0x02) - Guitar is connecting/syncing!
                            if (cmdId == 0x02)
                            {
                                File.AppendAllText(@"C:\Users\apolo\Desktop\rh_log.txt", $"[Arrival] Client {clientId}\n");
                                _connectedClients.Add(clientId);
                                Console.WriteLine($"Xbox One Dongle: Received Arrival from guitar client {clientId}! Completing sync handshake...");
                                IsSynced = true;

                                byte[] getDesc = new byte[] { 0x04, (byte)(0x20 | clientId), 0x01, 0x00 };
                                try { _mainInterface.OutPipe?.Write(getDesc); } catch { }
                            }
                            else if (cmdId == 0x04)
                            {
                                IsSynced = true;
                                Console.WriteLine($"Xbox One Dongle: Received Descriptor for client {clientId}. Sending PowerOn and Auth Success.");

                                byte[] powerOn = new byte[] { 0x05, (byte)(0x20 | clientId), 0x02, 0x01, 0x00 };
                                try { _mainInterface.OutPipe?.Write(powerOn); } catch { }

                                byte[] enableLed = new byte[] { 0x0A, (byte)(0x20 | clientId), 0x03, 0x02, 0x01, 0x14 };
                                try { _mainInterface.OutPipe?.Write(enableLed); } catch { }

                                byte[] authSuccess = new byte[] { 0x06, (byte)(0x20 | clientId), 0x04, 0x02, 0x01, 0x00 };
                                try { _mainInterface.OutPipe?.Write(authSuccess); } catch { }
                            }
                            else if (cmdId == 0x03)
                            {
                                if (payloadLen > 0)
                                {
                                    bool connected = readBuffer[offset + 4] != 0;
                                    IsSynced = connected;
                                    if (!connected)
                                    {
                                        _connectedClients.Remove(clientId);
                                    }
                                }
                            }
                            else if (cmdId == 0x20 || cmdId == 0x21)
                            {
                                IsSynced = true;
                                File.AppendAllText(@"C:\Users\apolo\Desktop\rh_log.txt", $"[Input] Cmd: {cmdId:X2}, Len: {totalMsgLen}\n");
                                var state = TranslatePacket(cmdId, readBuffer, offset, totalMsgLen);
                                try
                                {
                                    onStateChanged(clientId, state);
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine($"Error notifying state changed: {ex.Message}");
                                }
                            }

                            offset += totalMsgLen;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"ReadThread terminated: {ex.Message}");
                    ErrorMessage = $"Connection lost: {ex.Message}";
                }
                finally
                {
                    IsSynced = false;
                }
            })
            {
                IsBackground = true,
                Name = "XboxOneGhl_ReadThread"
            };

            readThread.Start();

            // Await cancellation or until the read thread finishes
            while (readThread.IsAlive && !token.IsCancellationRequested)
            {
                await Task.Delay(100, CancellationToken.None);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on unplug or stop
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error in XboxOneGhlProvider: {ex.Message}");
            ErrorMessage = $"Init Error: {ex.Message}";
        }
        finally
        {
            keepReading = false;
            try
            {
                _mainInterface?.InPipe?.Abort();
            }
            catch { }

            // Ensure read thread has completely stopped before touching _winUsbDevice
            if (readThread != null && readThread.IsAlive)
            {
                readThread.Join(1000);
            }

            if (pokeTask != null)
            {
                try
                {
                    await Task.WhenAny(pokeTask, Task.Delay(500));
                }
                catch { }
            }

            _mainInterface = null;

            try
            {
                _winUsbDevice?.Dispose();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error disposing WinUSB device: {ex.Message}");
            }
            _winUsbDevice = null;
            IsSynced = false;
            _connectedClients.Clear();
        }
    }

    public void Dispose()
    {
        try { _mainInterface?.InPipe?.Abort(); } catch { }
        try { _winUsbDevice?.Dispose(); } catch { }
        _winUsbDevice = null;
    }

    private async Task PokeLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (_mainInterface?.OutPipe != null)
                    {
                        var clients = _connectedClients.ToList();
                        if (clients.Count == 0)
                        {
                            // If no clients known, poke 1 and 2 just in case
                            clients.Add(1);
                            clients.Add(2);
                        }

                        foreach (var client in clients)
                        {
                            byte[] pokeData = { 0x22, client, 0x00, 0x08, 0x02, 0x08, 0x0A, 0x00, 0x00, 0x00, 0x00, 0x00 };
                            _mainInterface.OutPipe.Write(pokeData);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Poke write failed: {ex.Message}");
                    break;
                }

                // Poke every 10 seconds to keep it in 6-fret mode
                await Task.Delay(10000, token);
            }
        }
        catch (OperationCanceledException)
        {
            // Clean exit
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Xbox One Dongle Poke Exception: {ex.Message}");
        }
    }

    private void SendAck(byte commandId, byte flagsClient, byte sequence, int bytesReceived)
    {
        byte clientId = (byte)(flagsClient & 0x07);
        byte[] ackPacket = new byte[15];
        ackPacket[0] = 0x01; // CommandId: Protocol Control
        ackPacket[1] = (byte)(0x20 | clientId); // System Command | ClientId
        ackPacket[2] = sequence;
        ackPacket[3] = 0x0B; // Payload length = 11 bytes

        ackPacket[4] = 0x00; // ControlCode: Ack
        ackPacket[5] = commandId; // RefMessageType
        ackPacket[6] = (byte)((flagsClient & 0x20) | clientId); // RefFlags | Client
        int payloadLength = Math.Max(0, bytesReceived - 4);
        ackPacket[7] = (byte)(payloadLength & 0xFF);
        ackPacket[8] = (byte)((payloadLength >> 8) & 0xFF);
        ackPacket[9] = (byte)((payloadLength >> 16) & 0xFF);
        ackPacket[10] = (byte)((payloadLength >> 24) & 0xFF);
        ackPacket[11] = 0x00; // Remaining buffer (4 bytes)
        ackPacket[12] = 0x00;
        ackPacket[13] = 0x00;
        ackPacket[14] = 0x00;

        try
        {
            _mainInterface?.OutPipe?.Write(ackPacket);
        }
        catch { }
    }

    private int FindGipOffset(byte[] data, int length, out byte clientId)
    {
        clientId = 0;
        // In direct WinUSB reads, we don't have USBPcap headers! 
        // The packet IS the GIP packet directly.
        // GIP signature:
        // Byte 0: 0x20 or 0x21
        // Byte 1: Flags_Client (Bottom 3 bits are Client ID)
        // Byte 3: Length (Payload length, 0x0A or 0x1B for GHLive)
        if (length >= 4 && (data[0] == 0x20 || data[0] == 0x21))
        {
            // Verify we have enough data
            int payloadLength = data[3];
            if (length >= 4 + payloadLength)
            {
                clientId = (byte)(data[1] & 0x07);
                return 0; // Offset is 0
            }
        }
        return -1;
    }

    private InstrumentState TranslatePacket(byte cmdId, byte[] data, int offset, int length)
    {
        var state = new InstrumentState();
        
        // Ensure we have at least 14 bytes of payload (length = 4 bytes header + 14 bytes payload = 18 bytes total)
        if (length < 18) return state;

        if (cmdId == 0x21)
        {
            // Parse GHLive-specific 0x21 packets
            ushort buttons = BitConverter.ToUInt16(data, offset + 4);
            
            // White1 = 0x0001, Black1 = 0x0002, Black2 = 0x0004, Black3 = 0x0008, White2 = 0x0010, White3 = 0x0020
            state.Blue = (buttons & 0x0001) != 0;    // White 1
            state.Green = (buttons & 0x0002) != 0;   // Black 1
            state.Red = (buttons & 0x0004) != 0;     // Black 2
            state.Yellow = (buttons & 0x0008) != 0;  // Black 3
            state.Orange = (buttons & 0x0010) != 0;  // White 2
            state.White3 = (buttons & 0x0020) != 0;  // White 3

            state.HeroPower = (buttons & 0x0100) != 0; // Select
            state.Start = (buttons & 0x0200) != 0; // Start
            state.Select = (buttons & 0x0400) != 0; // GHTV button -> Select

            byte dpad = data[offset + 6]; // offset 2 in payload
            bool dpadUp = dpad == 0 || dpad == 1 || dpad == 7;
            bool dpadDown = dpad == 3 || dpad == 4 || dpad == 5;
            state.DpadLeft = dpad == 5 || dpad == 6 || dpad == 7;
            state.DpadRight = dpad == 1 || dpad == 2 || dpad == 3;

            byte strum = data[offset + 8]; // offset 4 in payload
            state.StrumUp = strum < 0x80 || dpadUp;
            state.StrumDown = strum > 0x80 || dpadDown;

            byte whammy = data[offset + 10]; // offset 6 in payload
            state.Whammy = (whammy - 128f) / 127f; // basic scaling

            var payloadBytes = new byte[length - 4];
            Array.Copy(data, offset + 4, payloadBytes, 0, length - 4);
            string hexPayload = BitConverter.ToString(payloadBytes);
            File.AppendAllText(@"C:\Users\apolo\Desktop\rh_log.txt", $"[Raw Input] Cmd21 Payload: {hexPayload}\n");

            if (length >= 24)
            {
                byte tilt = data[offset + 23]; // offset 19 in payload
                state.Tilt = tilt / 255f;
            }
        }
        else // cmdId == 0x20
        {
            // Standard Xbox One GIP 0x20 packets
            ushort buttons = BitConverter.ToUInt16(data, offset + 4);

            state.Green = (buttons & 0x0010) != 0;  // A
            state.Red = (buttons & 0x0020) != 0;    // B
            state.Yellow = (buttons & 0x0080) != 0; // Y
            state.Blue = (buttons & 0x0040) != 0;   // X
            state.Orange = (buttons & 0x1000) != 0; // LB
            state.White3 = (buttons & 0x2000) != 0; // RB

            // Menu buttons
            state.Select = (buttons & 0x0008) != 0; // View
            state.Start = (buttons & 0x0004) != 0;  // Menu
            state.HeroPower = (buttons & 0x0008) != 0 || (buttons & 0x0001) != 0; // View or Sync

            // D-Pad and Strum (Guitars usually map Strum to D-Pad Up/Down)
            state.DpadLeft = (buttons & 0x0400) != 0;
            state.DpadRight = (buttons & 0x0800) != 0;
            state.StrumUp = (buttons & 0x0100) != 0;
            state.StrumDown = (buttons & 0x0200) != 0;

            short leftStickY = BitConverter.ToInt16(data, offset + 12);
            short rightStickX = BitConverter.ToInt16(data, offset + 14);
            short rightStickY = BitConverter.ToInt16(data, offset + 16);

            if (leftStickY > 16384) state.StrumUp = true;
            if (leftStickY < -16384) state.StrumDown = true;

            state.Whammy = (rightStickX + 32768) / 65535f;
            state.Tilt = (rightStickY + 32768) / 65535f;
        }

        return state;
    }
}
