using kissproxylib;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace kissproxy_tests.Harness;

public static class KissTestHelpers
{
    private const byte FEND = 0xC0;

    /// <summary>
    /// Get the command code from a KISS frame.
    /// </summary>
    public static byte GetCommand(byte[] frame)
    {
        if (frame.Length < 2) return 0xFF;
        var cmdByte = frame[0] == FEND ? frame[1] : frame[0];
        return (byte)(cmdByte & 0x0F);
    }

    /// <summary>
    /// Get the port number from a KISS frame.
    /// </summary>
    public static int GetPort(byte[] frame)
    {
        if (frame.Length < 2) return 0;
        var cmdByte = frame[0] == FEND ? frame[1] : frame[0];
        return (cmdByte >> 4) & 0x0F;
    }

    /// <summary>
    /// Get the payload (without command byte) from a KISS frame.
    /// </summary>
    public static byte[] GetPayload(byte[] frame)
    {
        if (frame.Length < 3) return Array.Empty<byte>();

        int start = frame[0] == FEND ? 2 : 1;
        int end = frame[^1] == FEND ? frame.Length - 1 : frame.Length;

        return frame[start..end];
    }

    /// <summary>
    /// Get a parameter value from a KISS parameter frame.
    /// </summary>
    public static byte? GetParameterValue(byte[] frame)
    {
        var payload = GetPayload(frame);
        return payload.Length >= 1 ? payload[0] : null;
    }

    /// <summary>
    /// Build a simple AX.25 UI frame header (source, dest, control).
    /// This is a minimal representation for testing purposes.
    /// </summary>
    public static byte[] BuildAx25UiFrame(string source, string dest, byte[] info)
    {
        // Simplified AX.25 address encoding (not fully spec-compliant)
        var frame = new List<byte>();

        // Destination address (7 bytes: 6 char callsign + SSID)
        frame.AddRange(EncodeCallsign(dest, false));

        // Source address (7 bytes: 6 char callsign + SSID, with end-of-address bit)
        frame.AddRange(EncodeCallsign(source, true));

        // Control field: UI frame (0x03)
        frame.Add(0x03);

        // PID: No layer 3 (0xF0)
        frame.Add(0xF0);

        // Info field
        frame.AddRange(info);

        return frame.ToArray();
    }

    private static byte[] EncodeCallsign(string callsign, bool lastAddress)
    {
        var parts = callsign.Split('-');
        var call = parts[0].ToUpper().PadRight(6);
        int ssid = parts.Length > 1 ? int.Parse(parts[1]) : 0;

        var encoded = new byte[7];
        for (int i = 0; i < 6; i++)
        {
            encoded[i] = (byte)(call[i] << 1);
        }
        // SSID byte: 011SSSS0 or 011SSSS1 for last address
        encoded[6] = (byte)(0x60 | ((ssid & 0x0F) << 1) | (lastAddress ? 1 : 0));

        return encoded;
    }

    /// <summary>
    /// Build a KISS data frame from AX.25 data.
    /// </summary>
    public static byte[] BuildDataFrame(byte[] ax25Data, int port = 0)
    {
        var frame = new List<byte> { FEND, (byte)(port << 4) };
        frame.AddRange(ax25Data);
        frame.Add(FEND);
        return frame.ToArray();
    }

    /// <summary>
    /// Build an ACKMODE frame.
    /// </summary>
    public static byte[] BuildAckModeFrame(byte seqHi, byte seqLo, byte[] ax25Data, int port = 0)
    {
        var cmdByte = (byte)((port << 4) | KissFrameBuilder.CMD_ACKMODE);
        var frame = new List<byte> { FEND, cmdByte, seqHi, seqLo };
        frame.AddRange(ax25Data);
        frame.Add(FEND);
        return frame.ToArray();
    }
}

/// <summary>
/// Test logger that writes to xUnit output.
/// </summary>
public class TestLogger : ILogger
{
    private readonly ITestOutputHelper _output;

    public TestLogger(ITestOutputHelper output)
    {
        _output = output;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        try
        {
            _output.WriteLine($"[{logLevel}] {formatter(state, exception)}");
        }
        catch
        {
            // xUnit output can throw if test has ended
        }
    }
}

/// <summary>
/// Serial port factory that returns SimulatedTnc instances.
/// </summary>
public class SimulatedSerialPortFactory : ISerialPortFactory
{
    private readonly SimulatedTnc _tnc;

    public SimulatedSerialPortFactory(SimulatedTnc tnc)
    {
        _tnc = tnc;
    }

    public ISerialPort Create(string comPort, int baud) => _tnc;
}
