using System.Text;
using AwesomeAssertions;
using kissproxylib;
using Xunit.Abstractions;

namespace kissproxy_tests.Hardware;

/// <summary>
/// Tests AX.25 UI frame transmission via real NinoTNCs.
/// Validates the same frame construction path used by the web UI test transmission feature.
/// </summary>
public class HardwareTransmitTests : HardwareTestBase
{
    public HardwareTransmitTests(HardwareTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    [SkippableFact]
    public async Task RealTnc_UiFrame_TransmitsAndReceives()
    {
        SkipIfNoHardware();
        Output.WriteLine($"Testing UI frame transmission from {Fixture.Tnc1Port} to {Fixture.Tnc2Port}");

        using var tnc1 = Fixture.OpenTnc1();
        using var tnc2 = Fixture.OpenTnc2();
        tnc1.Open();
        tnc2.Open();

        // Set both TNCs to mode 0 (9600 GFSK AX.25)
        await SetBothTncsToMode(tnc1, tnc2, 0);

        // Set low TXDELAY for faster test
        var txDelayFrame = KissFrameBuilder.BuildParameterFrame(KissFrameBuilder.CMD_TXDELAY, 25);
        tnc1.Write(txDelayFrame, 0, txDelayFrame.Length);
        await Task.Delay(100);

        // Build AX.25 UI frame using production KissFrameBuilder (same path as web UI transmit)
        var testId = Guid.NewGuid().ToString("N")[..8];
        var message = $"TEST:{testId}";
        var infoBytes = Encoding.ASCII.GetBytes(message);
        var ax25Frame = KissFrameBuilder.BuildAx25UiFrame("TEST-1", "CQ", infoBytes);
        var kissFrame = KissFrameBuilder.BuildKissDataFrame(ax25Frame);

        Output.WriteLine($"Source: TEST-1, Dest: CQ, Message: {message}");
        LogFrame("Sending UI frame (KISS)", kissFrame);

        // Send from TNC1
        tnc1.Write(kissFrame, 0, kissFrame.Length);

        // Receive on TNC2
        var received = await ReadFrameWithTimeout(tnc2, TimeSpan.FromSeconds(5));

        received.Should().NotBeNull("TNC2 should receive the UI frame");
        LogFrame("Received", received!);

        // Verify it's a data frame
        var cmdByte = received![1];
        (cmdByte & 0x0F).Should().Be(KissFrameBuilder.CMD_DATAFRAME);

        // Verify the payload contains the test message
        var payload = Encoding.ASCII.GetString(received, 2, received.Length - 3); // skip FEND+cmd and trailing FEND
        payload.Should().Contain(message, "Received frame should contain the test message");

        Output.WriteLine($"UI frame transmitted and received successfully, test ID: {testId}");
    }

    [SkippableFact]
    public async Task RealTnc_UiFrame_WithCallsignAndSsid_TransmitsAndReceives()
    {
        SkipIfNoHardware();
        Output.WriteLine($"Testing UI frame with SSIDs from {Fixture.Tnc1Port} to {Fixture.Tnc2Port}");

        using var tnc1 = Fixture.OpenTnc1();
        using var tnc2 = Fixture.OpenTnc2();
        tnc1.Open();
        tnc2.Open();

        // Clear any residual data
        tnc1.DiscardInBuffer();
        tnc2.DiscardInBuffer();
        await Task.Delay(200);

        await SetBothTncsToMode(tnc1, tnc2, 0);

        var txDelayFrame = KissFrameBuilder.BuildParameterFrame(KissFrameBuilder.CMD_TXDELAY, 25);
        tnc1.Write(txDelayFrame, 0, txDelayFrame.Length);
        await Task.Delay(100);

        // Build frame with SSID-bearing callsigns
        var testId = Guid.NewGuid().ToString("N")[..8];
        var message = $"SSID:{testId}";
        var infoBytes = Encoding.ASCII.GetBytes(message);
        var ax25Frame = KissFrameBuilder.BuildAx25UiFrame("VK2TST-5", "VK2RX-1", infoBytes);
        var kissFrame = KissFrameBuilder.BuildKissDataFrame(ax25Frame);

        Output.WriteLine($"Source: VK2TST-5, Dest: VK2RX-1, Message: {message}");
        LogFrame("Sending UI frame with SSIDs (KISS)", kissFrame);

        // Send from TNC1
        tnc1.Write(kissFrame, 0, kissFrame.Length);

        // Receive on TNC2
        var received = await ReadFrameWithTimeout(tnc2, TimeSpan.FromSeconds(5));

        received.Should().NotBeNull("TNC2 should receive the UI frame with SSIDs");
        LogFrame("Received", received!);

        // Verify it's a data frame
        var cmdByte = received![1];
        (cmdByte & 0x0F).Should().Be(KissFrameBuilder.CMD_DATAFRAME);

        // Verify the payload contains the test message
        var payload = Encoding.ASCII.GetString(received, 2, received.Length - 3);
        payload.Should().Contain(message, "Received frame should contain the SSID test message");

        Output.WriteLine($"UI frame with SSIDs transmitted and received successfully, test ID: {testId}");
    }
}
