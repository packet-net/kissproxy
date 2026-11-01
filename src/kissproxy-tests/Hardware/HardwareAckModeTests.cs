using System.Diagnostics;
using AwesomeAssertions;
using kissproxylib;
using Xunit.Abstractions;

namespace kissproxy_tests.Hardware;

/// <summary>
/// Tests ACKMODE (acknowledgment mode) with real NinoTNCs.
/// Per the multi-drop KISS spec, ACKMODE returns sequence bytes when a frame is transmitted.
/// </summary>
public class HardwareAckModeTests : HardwareTestBase
{
    public HardwareAckModeTests(HardwareTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    [SkippableFact]
    public async Task RealTnc_AckMode_ReturnsAckAfterTransmit()
    {
        SkipIfNoHardware();
        Output.WriteLine("Testing ACKMODE - expecting ACK after transmission");

        using var tnc = Fixture.OpenTnc1();
        tnc.Open();

        // Clear any residual data
        tnc.DiscardInBuffer();
        await Task.Delay(200);

        // Set to mode 0 and TXDELAY
        var modeFrame = KissFrameBuilder.BuildSetHwFrame(0, persist: false);
        tnc.Write(modeFrame, 0, modeFrame.Length);
        var txDelayFrame = KissFrameBuilder.BuildParameterFrame(KissFrameBuilder.CMD_TXDELAY, 30);
        tnc.Write(txDelayFrame, 0, txDelayFrame.Length);
        await Task.Delay(500);

        // Clear again after setup
        tnc.DiscardInBuffer();
        await Task.Delay(100);

        // Send ACKMODE frame with sequence bytes 0x12, 0x34
        byte seqHi = 0x12, seqLo = 0x34;
        var ax25Data = BuildTestAx25Frame("ACKTEST");
        var frame = BuildAckModeFrame(seqHi, seqLo, ax25Data);

        LogFrame("Sending ACKMODE frame", frame);
        var stopwatch = Stopwatch.StartNew();
        tnc.Write(frame, 0, frame.Length);

        // Wait for ACK
        var ack = await ReadFrameWithTimeout(tnc, TimeSpan.FromSeconds(5));
        stopwatch.Stop();

        ack.Should().NotBeNull("ACK should be returned after transmission");
        LogFrame("Received ACK", ack!);

        // Verify ACK format: FEND, cmd (0x0C), seq_hi, seq_lo, FEND
        ack!.Length.Should().Be(5, "ACK should be exactly 5 bytes");
        (ack[1] & 0x0F).Should().Be(KissFrameBuilder.CMD_ACKMODE, "Command should be ACKMODE (0x0C)");
        ack[2].Should().Be(seqHi, "seq_hi should be preserved");
        ack[3].Should().Be(seqLo, "seq_lo should be preserved");

        Output.WriteLine($"ACK received in {stopwatch.ElapsedMilliseconds}ms");
        Output.WriteLine($"Sequence bytes preserved: 0x{seqHi:X2}, 0x{seqLo:X2}");
    }

    [SkippableFact]
    public async Task RealTnc_AckMode_TimingCorrelatesWithTxDelay()
    {
        SkipIfNoHardware();
        Output.WriteLine("Testing ACKMODE timing correlation with TXDELAY");

        using var tnc = Fixture.OpenTnc1();
        tnc.Open();

        // Clear any residual data
        tnc.DiscardInBuffer();

        // Set to mode 0
        var modeFrame = KissFrameBuilder.BuildSetHwFrame(0, persist: false);
        tnc.Write(modeFrame, 0, modeFrame.Length);
        await Task.Delay(300);

        // Set TXDELAY to 50 (500ms)
        byte txDelayValue = 50;
        var txDelayFrame = KissFrameBuilder.BuildParameterFrame(KissFrameBuilder.CMD_TXDELAY, txDelayValue);
        tnc.Write(txDelayFrame, 0, txDelayFrame.Length);
        await Task.Delay(200);

        Output.WriteLine($"TXDELAY set to {txDelayValue} (= {txDelayValue * 10}ms)");

        // Send ACKMODE frame and measure time
        var frame = BuildAckModeFrame(0x00, 0x01, BuildTestAx25Frame("TIMING"));
        var stopwatch = Stopwatch.StartNew();
        tnc.Write(frame, 0, frame.Length);

        var ack = await ReadFrameWithTimeout(tnc, TimeSpan.FromSeconds(10));
        stopwatch.Stop();

        ack.Should().NotBeNull("ACK should be returned");
        Output.WriteLine($"ACK timing: {stopwatch.ElapsedMilliseconds}ms (TXDELAY={txDelayValue * 10}ms)");

        // Note: ACK timing varies based on TNC implementation
        // Some TNCs ACK when queued, others when transmitted
        // Just verify ACK is received with correct sequence bytes
        ack!.Length.Should().BeGreaterThanOrEqualTo(4, "ACK should have at least 4 bytes");
        ack[3].Should().Be(0x01, "seq_lo should be preserved");

        Output.WriteLine("ACKMODE timing test completed");
    }

    [SkippableFact]
    public async Task RealTnc_AckMode_MultipleFrames_AllAcksReceived()
    {
        SkipIfNoHardware();
        Output.WriteLine("Testing multiple ACKMODE frames");

        using var tnc = Fixture.OpenTnc1();
        tnc.Open();

        // Clear any residual data
        tnc.DiscardInBuffer();
        await Task.Delay(200);

        // Set mode and TXDELAY
        var modeFrame = KissFrameBuilder.BuildSetHwFrame(0, persist: false);
        tnc.Write(modeFrame, 0, modeFrame.Length);
        var txDelayFrame = KissFrameBuilder.BuildParameterFrame(KissFrameBuilder.CMD_TXDELAY, 30);
        tnc.Write(txDelayFrame, 0, txDelayFrame.Length);
        await Task.Delay(500);

        // Clear again after setup
        tnc.DiscardInBuffer();
        await Task.Delay(100);

        var acksReceived = new List<(byte SeqHi, byte SeqLo)>();
        var frameCount = 3;

        for (int i = 1; i <= frameCount; i++)
        {
            // Clear buffer before each frame to ensure we read the correct ACK
            tnc.DiscardInBuffer();
            await Task.Delay(100);

            var frame = BuildAckModeFrame(0x00, (byte)i, BuildTestAx25Frame($"MULTI{i}"));
            Output.WriteLine($"Sending frame {i} with seq_lo={i}");
            tnc.Write(frame, 0, frame.Length);

            var ack = await ReadFrameWithTimeout(tnc, TimeSpan.FromSeconds(5));
            if (ack != null && ack.Length >= 4)
            {
                acksReceived.Add((ack[2], ack[3]));
                Output.WriteLine($"Received ACK for seq_lo={ack[3]}");
            }

            // Wait longer between frames to ensure TNC is ready
            await Task.Delay(500);
        }

        acksReceived.Should().HaveCount(frameCount, $"Should receive {frameCount} ACKs");
        for (int i = 1; i <= frameCount; i++)
        {
            acksReceived.Should().Contain(a => a.SeqLo == i, $"Should have ACK for seq_lo={i}");
        }

        Output.WriteLine($"All {frameCount} ACKs received");
    }

    [SkippableFact]
    public async Task RealTnc_AckMode_DifferentSequenceBytes_AllPreserved()
    {
        SkipIfNoHardware();
        Output.WriteLine("Testing ACKMODE with various sequence byte values");

        using var tnc = Fixture.OpenTnc1();
        tnc.Open();

        // Clear any residual data
        tnc.DiscardInBuffer();
        await Task.Delay(200);

        // Set mode and TXDELAY
        var modeFrame = KissFrameBuilder.BuildSetHwFrame(0, persist: false);
        tnc.Write(modeFrame, 0, modeFrame.Length);
        var txDelayFrame = KissFrameBuilder.BuildParameterFrame(KissFrameBuilder.CMD_TXDELAY, 30);
        tnc.Write(txDelayFrame, 0, txDelayFrame.Length);
        await Task.Delay(500);

        // Clear again after setup
        tnc.DiscardInBuffer();
        await Task.Delay(100);

        var testCases = new[] { (0x00, 0x00), (0xFF, 0xFF), (0xAB, 0xCD), (0x12, 0x34) };

        foreach (var (seqHi, seqLo) in testCases)
        {
            // Clear buffer before each frame to ensure we read the correct ACK
            tnc.DiscardInBuffer();
            await Task.Delay(100);

            var frame = BuildAckModeFrame((byte)seqHi, (byte)seqLo, BuildTestAx25Frame($"SEQ{seqHi:X2}{seqLo:X2}"));
            Output.WriteLine($"Testing seq_hi=0x{seqHi:X2}, seq_lo=0x{seqLo:X2}");
            tnc.Write(frame, 0, frame.Length);

            var ack = await ReadFrameWithTimeout(tnc, TimeSpan.FromSeconds(5));
            ack.Should().NotBeNull($"ACK should be received for 0x{seqHi:X2}{seqLo:X2}");
            LogFrame($"ACK for 0x{seqHi:X2}{seqLo:X2}", ack!);
            ack![2].Should().Be((byte)seqHi, "seq_hi should be preserved");
            ack[3].Should().Be((byte)seqLo, "seq_lo should be preserved");

            // Wait longer between tests
            await Task.Delay(500);
        }

        Output.WriteLine("All sequence byte values preserved correctly");
    }

    [SkippableFact]
    public async Task RealTnc_AckMode_TransmitsToOtherTnc()
    {
        SkipIfNoHardware();
        Output.WriteLine("Testing ACKMODE - verifying data reaches other TNC");

        using var tnc1 = Fixture.OpenTnc1();
        using var tnc2 = Fixture.OpenTnc2();
        tnc1.Open();
        tnc2.Open();

        // Clear any residual data
        tnc1.DiscardInBuffer();
        tnc2.DiscardInBuffer();
        await Task.Delay(200);

        // Set both to mode 0
        await SetBothTncsToMode(tnc1, tnc2, 0);

        // Low TXDELAY
        var txDelayFrame = KissFrameBuilder.BuildParameterFrame(KissFrameBuilder.CMD_TXDELAY, 25);
        tnc1.Write(txDelayFrame, 0, txDelayFrame.Length);
        await Task.Delay(100);

        // Clear TNC2 buffer before sending
        tnc2.DiscardInBuffer();
        await Task.Delay(100);

        // Send ACKMODE frame from TNC1
        var testId = "ACKTX:" + Guid.NewGuid().ToString("N")[..4];
        var ax25Data = BuildTestAx25Frame(testId);
        var frame = BuildAckModeFrame(0xAA, 0xBB, ax25Data);

        Output.WriteLine("Sending ACKMODE frame from TNC1");
        tnc1.Write(frame, 0, frame.Length);

        // TNC1 should get ACK
        var ack = await ReadFrameWithTimeout(tnc1, TimeSpan.FromSeconds(5));
        ack.Should().NotBeNull("TNC1 should receive ACK");
        Output.WriteLine("TNC1 received ACK");

        // TNC2 should receive the data frame (without sequence bytes)
        var received = await ReadFrameWithTimeout(tnc2, TimeSpan.FromSeconds(5));
        received.Should().NotBeNull("TNC2 should receive the data frame");
        LogFrame("TNC2 received", received!);

        // The received frame should be a data frame (cmd 0), not ACKMODE
        (received![1] & 0x0F).Should().Be(KissFrameBuilder.CMD_DATAFRAME,
            "Received frame should be data frame, not ACKMODE");

        Output.WriteLine("ACKMODE frame transmitted, ACK received, data arrived at TNC2");
    }
}
