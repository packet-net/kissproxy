using System.Collections.Concurrent;
using kissproxylib;

namespace kissproxy_tests.Harness;

/// <summary>
/// Simulates a NinoTNC for testing. Implements ISerialPort with bidirectional
/// byte queues and tracks state changes from KISS commands.
/// </summary>
public class SimulatedTnc : ISerialPort
{
    private const byte FEND = 0xC0;
    private const byte FESC = 0xDB;
    private const byte TFEND = 0xDC;
    private const byte TFESC = 0xDD;

    private readonly BlockingCollection<byte> _toTnc = new();
    private readonly BlockingCollection<byte> _fromTnc = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _processingTask;

    // State tracking
    public int TxDelay { get; private set; }
    public int Persistence { get; private set; }
    public int SlotTime { get; private set; }
    public int TxTail { get; private set; }
    public bool FullDuplex { get; private set; }
    public int NinoMode { get; private set; }
    public bool NinoModePersisted { get; private set; }

    // ACKMODE support
    public bool AckModeEnabled { get; set; } = true;
    public TimeSpan AckDelay { get; set; } = TimeSpan.FromMilliseconds(50);

    // Frame capture for assertions
    public List<byte[]> ReceivedFrames { get; } = new();
    public List<(byte SeqHi, byte SeqLo)> AcksSent { get; } = new();

    // Event for when a complete frame is received
    public event Action<byte[]>? FrameReceived;

    public void Open()
    {
        _processingTask = Task.Run(ProcessIncomingFrames);
    }

    public void Close()
    {
        _cts.Cancel();
        _toTnc.CompleteAdding();
    }

    public int ReadByte()
    {
        try
        {
            return _fromTnc.Take(_cts.Token);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException("Read cancelled");
        }
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        if (count <= 0)
            return 0;

        try
        {
            buffer[offset] = _fromTnc.Take(_cts.Token);
            int read = 1;

            while (read < count && _fromTnc.TryTake(out var nextByte))
            {
                buffer[offset + read] = nextByte;
                read++;
            }

            return read;
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException("Read cancelled");
        }
    }

    public void Write(byte[] buffer, int offset, int count)
    {
        for (int i = offset; i < offset + count; i++)
        {
            _toTnc.Add(buffer[i]);
        }
    }

    public void DiscardInBuffer()
    {
        while (_toTnc.TryTake(out _)) { }
    }

    public int BytesToRead => _fromTnc.Count;

    public int ReadTimeout { get; set; } = -1;

    public void Dispose()
    {
        Close();
        _cts.Dispose();
        _toTnc.Dispose();
        _fromTnc.Dispose();
    }

    /// <summary>
    /// Inject a data frame as if received from radio.
    /// </summary>
    public void SendDataFrame(byte[] ax25Data, int port = 0)
    {
        var frame = BuildFrame((byte)(port << 4), ax25Data);
        SendRawFrame(frame);
    }

    /// <summary>
    /// Send a raw KISS frame to the host.
    /// </summary>
    public void SendRawFrame(byte[] frame)
    {
        foreach (var b in frame)
        {
            _fromTnc.Add(b);
        }
    }

    /// <summary>
    /// Wait for a frame to be received from the host.
    /// </summary>
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

    private async Task ProcessIncomingFrames()
    {
        var buffer = new List<byte>();
        bool inFrame = false;
        bool escaped = false;

        while (!_cts.Token.IsCancellationRequested)
        {
            byte b;
            try
            {
                if (!_toTnc.TryTake(out b, 10, _cts.Token))
                    continue;
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (b == FEND)
            {
                if (inFrame && buffer.Count > 0)
                {
                    // Complete frame received
                    var frame = buffer.ToArray();
                    buffer.Clear();
                    await HandleFrame(frame);
                }
                inFrame = true;
                continue;
            }

            if (!inFrame)
                continue;

            if (escaped)
            {
                escaped = false;
                if (b == TFEND)
                    buffer.Add(FEND);
                else if (b == TFESC)
                    buffer.Add(FESC);
                else
                    buffer.Add(b);
            }
            else if (b == FESC)
            {
                escaped = true;
            }
            else
            {
                buffer.Add(b);
            }
        }
    }

    private async Task HandleFrame(byte[] frameData)
    {
        if (frameData.Length < 1)
            return;

        var cmdByte = frameData[0];
        var command = (byte)(cmdByte & 0x0F);
        var port = (cmdByte >> 4) & 0x0F;

        // Rebuild full frame with FEND markers for storage
        var fullFrame = new byte[frameData.Length + 2];
        fullFrame[0] = FEND;
        Array.Copy(frameData, 0, fullFrame, 1, frameData.Length);
        fullFrame[^1] = FEND;
        ReceivedFrames.Add(fullFrame);
        FrameReceived?.Invoke(fullFrame);

        // Handle commands
        switch (command)
        {
            case KissFrameBuilder.CMD_DATAFRAME:
                // Data frame - would transmit over radio
                break;

            case KissFrameBuilder.CMD_TXDELAY:
                if (frameData.Length >= 2)
                    TxDelay = frameData[1];
                break;

            case KissFrameBuilder.CMD_PERSISTENCE:
                if (frameData.Length >= 2)
                    Persistence = frameData[1];
                break;

            case KissFrameBuilder.CMD_SLOTTIME:
                if (frameData.Length >= 2)
                    SlotTime = frameData[1];
                break;

            case KissFrameBuilder.CMD_TXTAIL:
                if (frameData.Length >= 2)
                    TxTail = frameData[1];
                break;

            case KissFrameBuilder.CMD_FULLDUPLEX:
                if (frameData.Length >= 2)
                    FullDuplex = frameData[1] != 0;
                break;

            case KissFrameBuilder.CMD_SETHW:
                if (frameData.Length >= 2)
                {
                    var modeValue = frameData[1];
                    if (modeValue >= 16)
                    {
                        NinoMode = modeValue - 16;
                        NinoModePersisted = false;
                    }
                    else
                    {
                        NinoMode = modeValue;
                        NinoModePersisted = true;
                    }
                }
                break;

            case KissFrameBuilder.CMD_ACKMODE:
                // ACKMODE frame - extract seq bytes and payload
                if (frameData.Length >= 3)
                {
                    var seqHi = frameData[1];
                    var seqLo = frameData[2];
                    var payload = new byte[frameData.Length - 3];
                    Array.Copy(frameData, 3, payload, 0, payload.Length);

                    if (AckModeEnabled)
                    {
                        // Simulate transmission delay, then send ACK
                        await Task.Delay(AckDelay);
                        var ack = new byte[] { FEND, cmdByte, seqHi, seqLo, FEND };
                        foreach (var ab in ack)
                            _fromTnc.Add(ab);
                        AcksSent.Add((seqHi, seqLo));
                    }
                }
                break;
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
}
