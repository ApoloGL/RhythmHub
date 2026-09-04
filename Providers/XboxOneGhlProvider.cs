using System;
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
    private readonly System.Collections.Concurrent.ConcurrentQueue<(byte cmdId, byte flagsClient, byte seq, int totalMsgLen)> _ackQueue = new();
    private readonly SemaphoreSlim _ackSignal = new(0);

    // Reusable buffers for zero-allocation hot loop
    private readonly InstrumentState[] _clientStates = new InstrumentState[8];
    private readonly byte[] _ackPacket = new byte[15]
    {
        0x01, 0x20, 0x00, 0x0B, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
    };
    private readonly byte[] _pokePacket = new byte[12]
    {
        0x22, 0x00, 0x00, 0x08, 0x02, 0x08, 0x0A, 0x00, 0x00, 0x00, 0x00, 0x00
    };
    
    public XboxOneGhlProvider(string devicePath, string deviceName = "GHLive Dongle (Xbox One)", string? instanceId = null)
    {
        _devicePath = devicePath;
        DeviceName = deviceName;
        InstanceId = instanceId;

        for (int i = 0; i < _clientStates.Length; i++)
        {
            _clientStates[i] = new InstrumentState();
        }
    }

    public async Task StartListeningAsync(Action<int, InstrumentState> onStateChanged, CancellationToken token)
    {
        Thread? readThread = null;
        Task? pokeTask = null;
        Task? ackTask = null;
        bool keepReading = true;
        var readThreadTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

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
                readThreadTcs.TrySetCanceled(token);
            });

            // Start heartbeat poke loop and async non-blocking ACK queue worker
            pokeTask = PokeLoopAsync(token);
            ackTask = AckWorkerLoopAsync(token);

            // Run blocking read on a background thread so we can join it cleanly before disposing
            readThread = new Thread(() =>
            {
                try
                {
                    int bufferCapacity = Math.Max(512, _mainInterface.InPipe.MaximumPacketSize);
                    byte[] readBuffer = new byte[bufferCapacity];
                    int residualBytes = 0;

                    while (keepReading && !token.IsCancellationRequested)
                    {
                        int bytesRead = -1;
                        try
                        {
                            bytesRead = _mainInterface.InPipe.Read(readBuffer, residualBytes, bufferCapacity - residualBytes);
                        }
                        catch
                        {
                            // Pipe aborted or device disconnected
                            break;
                        }

                        if (bytesRead <= 0) break;

                        int totalAvailable = residualBytes + bytesRead;
                        int offset = 0;

                        while (offset + 4 <= totalAvailable)
                        {
                            byte cmdId = readBuffer[offset];
                            byte flagsClient = readBuffer[offset + 1];
                            byte seq = readBuffer[offset + 2];
                            byte payloadLen = readBuffer[offset + 3];
                            byte clientId = (byte)(flagsClient & 0x07);

                            int totalMsgLen = 4 + payloadLen;
                            if (offset + totalMsgLen > totalAvailable)
                            {
                                // Unparsed partial GIP frame at end of buffer
                                break;
                            }

                            // If packet requires acknowledgement, enqueue Ack asynchronously without stalling read loop
                            if ((flagsClient & 0x10) != 0)
                            {
                                EnqueueAck(cmdId, flagsClient, seq, totalMsgLen);
                            }

                            // Mark device as synced & active upon receiving valid communication
                            IsSynced = true;

                            // Handle Arrival (0x02) - Guitar is connecting/syncing!
                            if (cmdId == 0x02)
                            {
                                lock (_connectedClients)
                                {
                                    _connectedClients.Add(clientId);
                                }
                                Console.WriteLine($"Xbox One Dongle: Received Arrival from guitar client {clientId}! Completing sync handshake...");

                                byte[] getDesc = new byte[] { 0x04, (byte)(0x20 | clientId), 0x01, 0x00 };
                                try { _mainInterface.OutPipe?.Write(getDesc); } catch { }
                            }
                            else if (cmdId == 0x04)
                            {
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
                                    if (!connected)
                                    {
                                        lock (_connectedClients)
                                        {
                                            _connectedClients.Remove(clientId);
                                        }
                                    }
                                }
                            }
                            else if (cmdId == 0x21)
                            {
                                var state = _clientStates[clientId];
                                ParseGhlPacket(readBuffer, offset, totalMsgLen, state);
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

                        // Shift trailing unparsed bytes to index 0 for the next USB read transaction
                        residualBytes = totalAvailable - offset;
                        if (residualBytes > 0 && offset > 0)
                        {
                            Buffer.BlockCopy(readBuffer, offset, readBuffer, 0, residualBytes);
                        }
                        else if (residualBytes == 0)
                        {
                            residualBytes = 0;
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
                    readThreadTcs.TrySetResult();
                }
            })
            {
                IsBackground = true,
                Priority = ThreadPriority.Highest,
                Name = "XboxOneGhl_ReadThread"
            };

            readThread.Start();

            // Await completion via TaskCompletionSource instead of polling
            try
            {
                await readThreadTcs.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
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

    private void EnqueueAck(byte commandId, byte flagsClient, byte sequence, int bytesReceived)
    {
        _ackQueue.Enqueue((commandId, flagsClient, sequence, bytesReceived));
        try { _ackSignal.Release(); } catch { }
    }

    private async Task AckWorkerLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await _ackSignal.WaitAsync(token).ConfigureAwait(false);
                while (_ackQueue.TryDequeue(out var ackInfo))
                {
                    SendAck(ackInfo.cmdId, ackInfo.flagsClient, ackInfo.seq, ackInfo.totalMsgLen);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task PokeLoopAsync(CancellationToken token)
    {
        byte[] clientBuf = new byte[8];
        try
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (_mainInterface?.OutPipe != null)
                    {
                        int clientCount = 0;
                        lock (_connectedClients)
                        {
                            foreach (var client in _connectedClients)
                            {
                                if (clientCount < clientBuf.Length)
                                {
                                    clientBuf[clientCount++] = client;
                                }
                            }
                        }

                        if (clientCount == 0)
                        {
                            clientBuf[0] = 0;
                            clientBuf[1] = 1;
                            clientBuf[2] = 2;
                            clientCount = 3;
                        }

                        for (int i = 0; i < clientCount; i++)
                        {
                            byte client = clientBuf[i];
                            lock (_pokePacket)
                            {
                                _pokePacket[1] = client;
                                _mainInterface.OutPipe.Write(_pokePacket);
                            }
                        }
                        IsSynced = true;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Poke write failed: {ex.Message}");
                    break;
                }

                // GHL Keep-Alive interval is strictly 8 seconds (GHL_GUITAR_POKE_INTERVAL)
                await Task.Delay(8000, token).ConfigureAwait(false);
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
        int payloadLength = bytesReceived > 4 ? bytesReceived - 4 : 0;

        lock (_ackPacket)
        {
            _ackPacket[1] = (byte)(0x20 | clientId);
            _ackPacket[2] = sequence;
            _ackPacket[5] = commandId;
            _ackPacket[6] = (byte)((flagsClient & 0x20) | clientId);
            _ackPacket[7] = (byte)(payloadLength & 0xFF);
            _ackPacket[8] = (byte)((payloadLength >> 8) & 0xFF);
            _ackPacket[9] = (byte)((payloadLength >> 16) & 0xFF);
            _ackPacket[10] = (byte)((payloadLength >> 24) & 0xFF);

            try
            {
                _mainInterface?.OutPipe?.Write(_ackPacket);
            }
            catch { }
        }
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

    private static void ParseGhlPacket(byte[] data, int offset, int length, InstrumentState state)
    {
        // Ensure we have at least 14 bytes of payload (length = 4 bytes header + 14 bytes payload = 18 bytes total)
        if (length < 18) return;

        // Fast bitwise extraction of 16-bit button mask (little-endian)
        int btnLo = data[offset + 4];
        int btnHi = data[offset + 5];
        int buttons = btnLo | (btnHi << 8);

        // White1 = 0x0001, Black1 = 0x0002, Black2 = 0x0004, Black3 = 0x0008, White2 = 0x0010, White3 = 0x0020
        state.Blue = (buttons & 0x0001) != 0;    // White 1
        state.Green = (buttons & 0x0002) != 0;   // Black 1
        state.Red = (buttons & 0x0004) != 0;     // Black 2
        state.Yellow = (buttons & 0x0008) != 0;  // Black 3
        state.Orange = (buttons & 0x0010) != 0;  // White 2
        state.White3 = (buttons & 0x0020) != 0;  // White 3

        state.HeroPower = (buttons & 0x0100) != 0; // Select
        state.Start = (buttons & 0x0200) != 0;     // Start
        state.Select = (buttons & 0x0400) != 0;    // GHTV button -> Select

        byte dpad = data[offset + 6]; // offset 2 in payload
        state.DpadUp = dpad == 0 || dpad == 1 || dpad == 7;
        state.DpadDown = dpad >= 3 && dpad <= 5;
        state.DpadLeft = dpad >= 5 && dpad <= 7;
        state.DpadRight = dpad >= 1 && dpad <= 3;

        byte strum = data[offset + 8]; // offset 4 in payload
        // Strum bar rests at 128 (0x80), 0 is Strum Up, 255 is Strum Down.
        // Deadzone of 20 prevents mechanical jitter
        state.StrumUp = strum < 108;   // 128 - 20
        state.StrumDown = strum > 148; // 128 + 20

        byte whammy = data[offset + 10]; // offset 6 in payload
        // Whammy rests at 128, goes to 255. Add deadzone to prevent resting jitter.
        if (whammy > 140)
        {
            state.Whammy = (whammy - 128) * (1.0f / 127.0f);
        }
        else
        {
            state.Whammy = 0.0f;
        }

        if (length >= 24)
        {
            state.Tilt = data[offset + 23] * (1.0f / 255.0f);
        }
        else
        {
            state.Tilt = 0.0f;
        }
    }
}
