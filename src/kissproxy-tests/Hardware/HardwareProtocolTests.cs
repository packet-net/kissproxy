using System.Text;
using AwesomeAssertions;
using kissproxylib;
using Xunit.Abstractions;

namespace kissproxy_tests.Hardware;

/// <summary>
/// Tests basic KISS protocol operations against real NinoTNCs.
/// </summary>
public class HardwareProtocolTests : HardwareTestBase
{
    public HardwareProtocolTests(HardwareTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    [SkippableFact]
    public async Task RealTnc_ParameterCommand_TxDelay_Accepted()
    {
        SkipIfNoHardware();
        Output.WriteLine($"Testing TNC1 at {Fixture.Tnc1Port}");

        using var tnc = Fixture.OpenTnc1();
        tnc.Open();

        // Send TXDELAY command
        var frame = KissFrameBuilder.BuildParameterFrame(KissFrameBuilder.CMD_TXDELAY, 50);
        LogFrame("Sending TXDELAY", frame);
        tnc.Write(frame, 0, frame.Length);

        // No response expected for parameter commands, but should not error
        await Task.Delay(100);

        Output.WriteLine("TXDELAY command accepted");
    }

    [SkippableFact]
    public async Task RealTnc_ParameterCommand_Persistence_Accepted()
    {
        SkipIfNoHardware();

        using var tnc = Fixture.OpenTnc1();
        tnc.Open();

        var frame = KissFrameBuilder.BuildParameterFrame(KissFrameBuilder.CMD_PERSISTENCE, 63);
        LogFrame("Sending PERSISTENCE", frame);
        tnc.Write(frame, 0, frame.Length);

        await Task.Delay(100);
        Output.WriteLine("PERSISTENCE command accepted");
    }

    [SkippableFact]
    public async Task RealTnc_ParameterCommand_SlotTime_Accepted()
    {
        SkipIfNoHardware();

        using var tnc = Fixture.OpenTnc1();
        tnc.Open();

        var frame = KissFrameBuilder.BuildParameterFrame(KissFrameBuilder.CMD_SLOTTIME, 10);
        LogFrame("Sending SLOTTIME", frame);
        tnc.Write(frame, 0, frame.Length);

        await Task.Delay(100);
        Output.WriteLine("SLOTTIME command accepted");
    }

    [SkippableFact]
    public async Task RealTnc_AllParameterCommands_Accepted()
    {
        SkipIfNoHardware();

        using var tnc = Fixture.OpenTnc1();
        tnc.Open();

        // Send all parameter commands
        var commands = new[]
        {
            (KissFrameBuilder.CMD_TXDELAY, (byte)50, "TXDELAY"),
            (KissFrameBuilder.CMD_PERSISTENCE, (byte)63, "PERSISTENCE"),
            (KissFrameBuilder.CMD_SLOTTIME, (byte)10, "SLOTTIME"),
            (KissFrameBuilder.CMD_TXTAIL, (byte)5, "TXTAIL"),
            (KissFrameBuilder.CMD_FULLDUPLEX, (byte)0, "FULLDUPLEX")
        };

        foreach (var (cmd, value, name) in commands)
        {
            var frame = KissFrameBuilder.BuildParameterFrame(cmd, value);
            tnc.Write(frame, 0, frame.Length);
            Output.WriteLine($"Sent {name}={value}");
            await Task.Delay(50);
        }

        Output.WriteLine("All parameter commands accepted");
    }

    [SkippableFact]
    public async Task RealTnc_DataFrame_TransmitsAndReceives()
    {
        SkipIfNoHardware();
        Output.WriteLine($"Testing transmission from {Fixture.Tnc1Port} to {Fixture.Tnc2Port}");

        using var tnc1 = Fixture.OpenTnc1();
        using var tnc2 = Fixture.OpenTnc2();
        tnc1.Open();
        tnc2.Open();

        // Set both TNCs to mode 0 (9600 GFSK) for reliable communication
        await SetBothTncsToMode(tnc1, tnc2, 0);

        // Set low TXDELAY for faster test
        var txDelayFrame = KissFrameBuilder.BuildParameterFrame(KissFrameBuilder.CMD_TXDELAY, 25);
        tnc1.Write(txDelayFrame, 0, txDelayFrame.Length);
        await Task.Delay(100);

        // Build and send test frame
        var testId = Guid.NewGuid().ToString("N")[..8];
        var ax25Data = BuildTestAx25Frame(testId);
        var kissFrame = BuildDataFrame(ax25Data);

        LogFrame("Sending data frame", kissFrame);
        tnc1.Write(kissFrame, 0, kissFrame.Length);

        // Receive on TNC2
        var received = await ReadFrameWithTimeout(tnc2, TimeSpan.FromSeconds(5));

        received.Should().NotBeNull("TNC2 should receive the frame");
        LogFrame("Received", received!);

        // Verify command is data frame
        var cmdByte = received![1];
        (cmdByte & 0x0F).Should().Be(KissFrameBuilder.CMD_DATAFRAME);

        Output.WriteLine($"Frame transmitted and received successfully, test ID: {testId}");
    }

    [SkippableFact]
    public async Task RealTnc_MultipleDataFrames_AllReceived()
    {
        SkipIfNoHardware();

        using var tnc1 = Fixture.OpenTnc1();
        using var tnc2 = Fixture.OpenTnc2();
        tnc1.Open();
        tnc2.Open();

        await SetBothTncsToMode(tnc1, tnc2, 0);

        // Set low TXDELAY
        var txDelayFrame = KissFrameBuilder.BuildParameterFrame(KissFrameBuilder.CMD_TXDELAY, 25);
        tnc1.Write(txDelayFrame, 0, txDelayFrame.Length);
        await Task.Delay(100);

        // Send 3 frames
        var frameCount = 3;
        var testIds = new List<string>();

        for (int i = 0; i < frameCount; i++)
        {
            var testId = $"FRAME{i}:{Guid.NewGuid().ToString("N")[..4]}";
            testIds.Add(testId);
            var ax25Data = BuildTestAx25Frame(testId);
            var kissFrame = BuildDataFrame(ax25Data);

            Output.WriteLine($"Sending frame {i + 1}/{frameCount}");
            tnc1.Write(kissFrame, 0, kissFrame.Length);

            // Wait between frames to avoid buffer issues
            await Task.Delay(500);
        }

        // Receive frames
        var receivedCount = 0;
        for (int i = 0; i < frameCount; i++)
        {
            var received = await ReadFrameWithTimeout(tnc2, TimeSpan.FromSeconds(3));
            if (received != null)
            {
                receivedCount++;
                LogFrame($"Received frame {receivedCount}", received);
            }
        }

        receivedCount.Should().Be(frameCount, $"All {frameCount} frames should be received");
        Output.WriteLine($"All {frameCount} frames transmitted and received");
    }

    [SkippableFact]
    public async Task RealTnc_BidirectionalCommunication_Works()
    {
        SkipIfNoHardware();

        using var tnc1 = Fixture.OpenTnc1();
        using var tnc2 = Fixture.OpenTnc2();
        tnc1.Open();
        tnc2.Open();

        // Clear any residual data
        tnc1.DiscardInBuffer();
        tnc2.DiscardInBuffer();
        await Task.Delay(200);

        await SetBothTncsToMode(tnc1, tnc2, 0);

        // Set low TXDELAY on both
        var txDelayFrame = KissFrameBuilder.BuildParameterFrame(KissFrameBuilder.CMD_TXDELAY, 30);
        tnc1.Write(txDelayFrame, 0, txDelayFrame.Length);
        tnc2.Write(txDelayFrame, 0, txDelayFrame.Length);
        await Task.Delay(100);

        // Clear buffers before first direction
        tnc2.DiscardInBuffer();
        await Task.Delay(100);

        // Send from TNC1 to TNC2
        var testId1 = "TNC1TO2:" + Guid.NewGuid().ToString("N")[..4];
        var frame1 = BuildDataFrame(BuildTestAx25Frame(testId1));
        Output.WriteLine($"Sending from TNC1 to TNC2: {testId1}");
        tnc1.Write(frame1, 0, frame1.Length);

        var received1 = await ReadFrameWithTimeout(tnc2, TimeSpan.FromSeconds(5));
        received1.Should().NotBeNull("TNC2 should receive frame from TNC1");
        Output.WriteLine("TNC1 -> TNC2: OK");

        await Task.Delay(500);

        // Clear buffers before second direction
        tnc1.DiscardInBuffer();
        await Task.Delay(100);

        // Send from TNC2 to TNC1
        var testId2 = "TNC2TO1:" + Guid.NewGuid().ToString("N")[..4];
        var frame2 = BuildDataFrame(BuildTestAx25Frame(testId2));
        Output.WriteLine($"Sending from TNC2 to TNC1: {testId2}");
        tnc2.Write(frame2, 0, frame2.Length);

        var received2 = await ReadFrameWithTimeout(tnc1, TimeSpan.FromSeconds(5));
        received2.Should().NotBeNull("TNC1 should receive frame from TNC2");
        Output.WriteLine("TNC2 -> TNC1: OK");

        Output.WriteLine("Bidirectional communication verified");
    }
}
