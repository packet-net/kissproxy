using System.Diagnostics;
using System.Text;
using kissproxylib;
using Xunit.Abstractions;

namespace kissproxy_tests.Hardware;

/// <summary>
/// Base class for hardware tests. Provides helper methods for working with real TNCs.
/// </summary>
[Collection("Hardware")]
public abstract class HardwareTestBase : IClassFixture<HardwareTestFixture>
{
    private const byte FEND = 0xC0;

    protected readonly HardwareTestFixture Fixture;
    protected readonly ITestOutputHelper Output;

    protected HardwareTestBase(HardwareTestFixture fixture, ITestOutputHelper output)
    {
        Fixture = fixture;
        Output = output;
    }

    /// <summary>
    /// Skip the test if hardware is not available.
    /// </summary>
    protected void SkipIfNoHardware()
    {
        Skip.If(!Fixture.HardwareAvailable, Fixture.UnavailableReason ?? "Hardware not available");
    }

    /// <summary>
    /// Send a KISS frame and wait for a response.
    /// </summary>
    protected async Task<byte[]?> SendAndReceive(ISerialPort tnc, byte[] frame, TimeSpan timeout)
    {
        tnc.Write(frame, 0, frame.Length);
        return await ReadFrameWithTimeout(tnc, timeout);
    }

    /// <summary>
    /// Read a KISS frame with timeout.
    /// </summary>
    protected async Task<byte[]?> ReadFrameWithTimeout(ISerialPort tnc, TimeSpan timeout)
    {
        var buffer = new List<byte>();
        var stopwatch = Stopwatch.StartNew();
        bool inFrame = false;

        // Set a short read timeout on the serial port
        tnc.ReadTimeout = 100;

        while (stopwatch.Elapsed < timeout)
        {
            try
            {
                // Check if data is available
                if (tnc.BytesToRead > 0)
                {
                    var b = tnc.ReadByte();

                    if (b == FEND)
                    {
                        if (inFrame && buffer.Count > 0)
                        {
                            // Complete frame
                            var frame = new byte[buffer.Count + 2];
                            frame[0] = FEND;
                            for (int i = 0; i < buffer.Count; i++)
                                frame[i + 1] = buffer[i];
                            frame[^1] = FEND;
                            return frame;
                        }
                        inFrame = true;
                        buffer.Clear();
                    }
                    else if (inFrame)
                    {
                        buffer.Add((byte)b);
                    }
                }
                else
                {
                    // No data available, wait a bit
                    await Task.Delay(50);
                }
            }
            catch (TimeoutException)
            {
                // Continue waiting
            }
            catch (Exception ex)
            {
                Output.WriteLine($"Read error: {ex.Message}");
                await Task.Delay(50);
            }
        }

        return null;
    }

    /// <summary>
    /// Build a data frame for transmission.
    /// </summary>
    protected byte[] BuildDataFrame(byte[] ax25Data, int port = 0)
    {
        var frame = new List<byte> { FEND, (byte)(port << 4) };
        frame.AddRange(ax25Data);
        frame.Add(FEND);
        return frame.ToArray();
    }

    /// <summary>
    /// Build an ACKMODE frame.
    /// </summary>
    protected byte[] BuildAckModeFrame(byte seqHi, byte seqLo, byte[] ax25Data, int port = 0)
    {
        var cmdByte = (byte)((port << 4) | KissFrameBuilder.CMD_ACKMODE);
        var frame = new List<byte> { FEND, cmdByte, seqHi, seqLo };
        frame.AddRange(ax25Data);
        frame.Add(FEND);
        return frame.ToArray();
    }

    /// <summary>
    /// Build a simple AX.25 UI frame for testing.
    /// </summary>
    protected byte[] BuildTestAx25Frame(string id)
    {
        // Simple frame with test identifier
        var info = Encoding.ASCII.GetBytes($"TEST:{id}:{DateTime.UtcNow.Ticks}");
        return BuildAx25UiFrame("TEST-1", "TEST-2", info);
    }

    /// <summary>
    /// Build a minimal AX.25 UI frame.
    /// </summary>
    protected byte[] BuildAx25UiFrame(string source, string dest, byte[] info)
    {
        var frame = new List<byte>();

        // Destination address (7 bytes)
        frame.AddRange(EncodeCallsign(dest, false));

        // Source address (7 bytes, with end-of-address bit)
        frame.AddRange(EncodeCallsign(source, true));

        // Control: UI frame (0x03)
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
        encoded[6] = (byte)(0x60 | ((ssid & 0x0F) << 1) | (lastAddress ? 1 : 0));

        return encoded;
    }

    /// <summary>
    /// Set both TNCs to the same mode for testing.
    /// </summary>
    protected async Task SetBothTncsToMode(ISerialPort tnc1, ISerialPort tnc2, int mode)
    {
        var modeFrame = KissFrameBuilder.BuildSetHwFrame(mode, persist: false);
        tnc1.Write(modeFrame, 0, modeFrame.Length);
        tnc2.Write(modeFrame, 0, modeFrame.Length);

        // Wait for mode switch to complete
        await Task.Delay(500);

        Output.WriteLine($"Both TNCs set to mode {mode}");
    }

    /// <summary>
    /// Log a hex dump of a frame.
    /// </summary>
    protected void LogFrame(string label, byte[] frame)
    {
        var hex = BitConverter.ToString(frame).Replace("-", " ");
        Output.WriteLine($"{label}: [{frame.Length} bytes] {hex}");
    }
}

/// <summary>
/// Test collection to prevent parallel execution of hardware tests.
/// </summary>
[CollectionDefinition("Hardware")]
public class HardwareTestCollection : ICollectionFixture<HardwareTestFixture>
{
    // This class has no code, and is never created. Its purpose is to be the place
    // to apply [CollectionDefinition] and all the ICollectionFixture<> interfaces.
}
