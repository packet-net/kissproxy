using System.Net.Sockets;
using kissproxylib;

namespace kissproxy_tests.Harness;

/// <summary>
/// Simulates a node (like LinBPQ/Direwolf) connecting to kissproxy via TCP.
/// </summary>
public class SimulatedNode : IDisposable
{
    private const byte FEND = 0xC0;
    private const byte FESC = 0xDB;
    private const byte TFEND = 0xDC;
    private const byte TFESC = 0xDD;

    private TcpClient? _client;
    private NetworkStream? _stream;
    private Task? _receiveTask;
    private CancellationTokenSource _cts = new();

    // Frame capture
    public List<byte[]> ReceivedFrames { get; } = new();
    public List<byte[]> SentFrames { get; } = new();
    public List<(byte SeqHi, byte SeqLo)> AcksReceived { get; } = new();

    public bool IsConnected => _client?.Connected ?? false;

    public async Task ConnectAsync(int port, string host = "localhost", int timeoutMs = 5000)
    {
        _client = new TcpClient();
        using var cts = new CancellationTokenSource(timeoutMs);
        await _client.ConnectAsync(host, port, cts.Token);
        _stream = _client.GetStream();
        _receiveTask = Task.Run(ReceiveLoop);
    }

    public void Disconnect()
    {
        _cts.Cancel();
        _stream?.Close();
        _client?.Close();
    }

    public void SendDataFrame(byte[] ax25Data, int port = 0)
    {
        var cmdByte = (byte)(port << 4 | KissFrameBuilder.CMD_DATAFRAME);
        var frame = BuildFrame(cmdByte, ax25Data);
        SendRaw(frame);
    }

    public void SendParameterCommand(byte command, byte value, int port = 0)
    {
        var cmdByte = (byte)((port << 4) | command);
        var frame = new byte[] { FEND, cmdByte, value, FEND };
        SendRaw(frame);
    }

    public void SendAckModeFrame(byte seqHi, byte seqLo, byte[] ax25Data, int port = 0)
    {
        var cmdByte = (byte)((port << 4) | KissFrameBuilder.CMD_ACKMODE);
        var payload = new byte[ax25Data.Length + 2];
        payload[0] = seqHi;
        payload[1] = seqLo;
        Array.Copy(ax25Data, 0, payload, 2, ax25Data.Length);
        var frame = BuildFrame(cmdByte, payload);
        SendRaw(frame);
    }

    public void SendRaw(byte[] frame)
    {
        if (_stream == null)
            throw new InvalidOperationException("Not connected");

        _stream.Write(frame, 0, frame.Length);
        _stream.Flush();
        SentFrames.Add(frame);
    }

    public async Task<byte[]?> WaitForFrame(TimeSpan timeout)
    {
        var startCount = ReceivedFrames.Count;
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (ReceivedFrames.Count > startCount)
            {
                return ReceivedFrames[^1];
            }
            await Task.Delay(10);
        }

        return null;
    }

    public async Task<(byte SeqHi, byte SeqLo)?> WaitForAck(TimeSpan timeout)
    {
        var startCount = AcksReceived.Count;
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (AcksReceived.Count > startCount)
            {
                return AcksReceived[^1];
            }
            await Task.Delay(10);
        }

        return null;
    }

    private async Task ReceiveLoop()
    {
        if (_stream == null) return;

        var buffer = new byte[1024];
        var frameBuffer = new List<byte>();
        bool inFrame = false;
        bool escaped = false;

        while (!_cts.Token.IsCancellationRequested && _client?.Connected == true)
        {
            try
            {
                var readTask = _stream.ReadAsync(buffer, 0, buffer.Length, _cts.Token);
                var bytesRead = await readTask;
                if (bytesRead == 0) break;

                for (int i = 0; i < bytesRead; i++)
                {
                    var b = buffer[i];

                    if (b == FEND)
                    {
                        if (inFrame && frameBuffer.Count > 0)
                        {
                            // Complete frame
                            var frame = new byte[frameBuffer.Count + 2];
                            frame[0] = FEND;
                            for (int j = 0; j < frameBuffer.Count; j++)
                                frame[j + 1] = frameBuffer[j];
                            frame[^1] = FEND;

                            ReceivedFrames.Add(frame);

                            // Check if it's an ACKMODE response
                            if (frameBuffer.Count >= 3)
                            {
                                var cmd = (byte)(frameBuffer[0] & 0x0F);
                                if (cmd == KissFrameBuilder.CMD_ACKMODE && frameBuffer.Count == 3)
                                {
                                    AcksReceived.Add((frameBuffer[1], frameBuffer[2]));
                                }
                            }

                            frameBuffer.Clear();
                        }
                        inFrame = true;
                        continue;
                    }

                    if (!inFrame) continue;

                    if (escaped)
                    {
                        escaped = false;
                        if (b == TFEND)
                            frameBuffer.Add(FEND);
                        else if (b == TFESC)
                            frameBuffer.Add(FESC);
                        else
                            frameBuffer.Add(b);
                    }
                    else if (b == FESC)
                    {
                        escaped = true;
                    }
                    else
                    {
                        frameBuffer.Add(b);
                    }
                }
            }
            catch (Exception) when (_cts.Token.IsCancellationRequested)
            {
                break;
            }
            catch (IOException)
            {
                break;
            }
        }
    }

    private static byte[] BuildFrame(byte cmdByte, byte[] data)
    {
        var frame = new List<byte> { FEND, cmdByte };

        foreach (var b in data)
        {
            if (b == FEND)
            {
                frame.Add(FESC);
                frame.Add(TFEND);
            }
            else if (b == FESC)
            {
                frame.Add(FESC);
                frame.Add(TFESC);
            }
            else
            {
                frame.Add(b);
            }
        }

        frame.Add(FEND);
        return frame.ToArray();
    }

    public void Dispose()
    {
        Disconnect();
        _cts.Dispose();
    }
}
