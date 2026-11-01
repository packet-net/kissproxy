using kissproxylib;

namespace kissproxy_tests.Hardware;

/// <summary>
/// Fixture for hardware tests. Reads TNC configuration from environment variables.
/// Tests will be skipped if hardware is not available.
/// </summary>
public class HardwareTestFixture : IDisposable
{
    public string Tnc1Port { get; }
    public string Tnc2Port { get; }
    public int Tnc1Baud { get; } = 57600;
    public int Tnc2Baud { get; } = 57600;

    public bool HardwareAvailable { get; private set; }
    public string? UnavailableReason { get; private set; }

    public HardwareTestFixture()
    {
        // Read from environment variables
        Tnc1Port = Environment.GetEnvironmentVariable("KISSPROXY_TEST_TNC1") ?? "";
        Tnc2Port = Environment.GetEnvironmentVariable("KISSPROXY_TEST_TNC2") ?? "";

        // Allow baud rate override
        if (int.TryParse(Environment.GetEnvironmentVariable("KISSPROXY_TEST_BAUD"), out var baud))
        {
            Tnc1Baud = baud;
            Tnc2Baud = baud;
        }

        // Check hardware availability
        ValidateHardware();
    }

    private void ValidateHardware()
    {
        if (string.IsNullOrEmpty(Tnc1Port))
        {
            HardwareAvailable = false;
            UnavailableReason = "KISSPROXY_TEST_TNC1 environment variable not set";
            return;
        }

        if (string.IsNullOrEmpty(Tnc2Port))
        {
            HardwareAvailable = false;
            UnavailableReason = "KISSPROXY_TEST_TNC2 environment variable not set";
            return;
        }

        // On Windows, just check if it looks like a COM port
        if (OperatingSystem.IsWindows())
        {
            HardwareAvailable = Tnc1Port.StartsWith("COM", StringComparison.OrdinalIgnoreCase) &&
                               Tnc2Port.StartsWith("COM", StringComparison.OrdinalIgnoreCase);
            if (!HardwareAvailable)
            {
                UnavailableReason = $"Invalid COM port format: {Tnc1Port}, {Tnc2Port}";
            }
            return;
        }

        // On Linux, check if device files exist
        if (!File.Exists(Tnc1Port))
        {
            HardwareAvailable = false;
            UnavailableReason = $"TNC1 device not found: {Tnc1Port}";
            return;
        }

        if (!File.Exists(Tnc2Port))
        {
            HardwareAvailable = false;
            UnavailableReason = $"TNC2 device not found: {Tnc2Port}";
            return;
        }

        HardwareAvailable = true;
    }

    /// <summary>
    /// Open TNC1 serial port.
    /// </summary>
    public ISerialPort OpenTnc1()
    {
        if (!HardwareAvailable)
            throw new InvalidOperationException($"Hardware not available: {UnavailableReason}");

        return new RealSerialPort(Tnc1Port, Tnc1Baud);
    }

    /// <summary>
    /// Open TNC2 serial port.
    /// </summary>
    public ISerialPort OpenTnc2()
    {
        if (!HardwareAvailable)
            throw new InvalidOperationException($"Hardware not available: {UnavailableReason}");

        return new RealSerialPort(Tnc2Port, Tnc2Baud);
    }

    public void Dispose()
    {
        // No cleanup needed for fixture itself
    }
}
