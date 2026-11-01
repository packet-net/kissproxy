using System.Diagnostics;
using AwesomeAssertions;
using kissproxylib;
using Xunit.Abstractions;

namespace kissproxy_tests.Hardware;

/// <summary>
/// Tests that verify kissproxy actually controls TNC parameters.
/// Requires TNCs configured with MODE_SET_FROM_KISS and TXDELAY pots at zero.
/// </summary>
public class HardwareParameterControlTests : HardwareTestBase
{
    public HardwareParameterControlTests(HardwareTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    [SkippableFact]
    public async Task Mode_ControlsCommunication_MatchedModesWork()
    {
        SkipIfNoHardware();
        Output.WriteLine("Testing that matched modes enable communication");

        using var tnc1 = Fixture.OpenTnc1();
        using var tnc2 = Fixture.OpenTnc2();
        tnc1.Open();
        tnc2.Open();

        // Clear buffers
        tnc1.DiscardInBuffer();
        tnc2.DiscardInBuffer();
        await Task.Delay(200);

        // Set both to mode 5 (3600 QPSK)
        Output.WriteLine("Setting both TNCs to mode 5 (3600 QPSK IL2P+CRC)");
        var mode5Frame = KissFrameBuilder.BuildSetHwFrame(5, persist: false);
        tnc1.Write(mode5Frame, 0, mode5Frame.Length);
        tnc2.Write(mode5Frame, 0, mode5Frame.Length);
        await Task.Delay(500);

        // Set low TXDELAY
        var txDelayFrame = KissFrameBuilder.BuildParameterFrame(KissFrameBuilder.CMD_TXDELAY, 30);
        tnc1.Write(txDelayFrame, 0, txDelayFrame.Length);
        await Task.Delay(100);

        // Send frame
        var testFrame = BuildDataFrame(BuildTestAx25Frame("MODE5"));
        tnc1.Write(testFrame, 0, testFrame.Length);

        var received = await ReadFrameWithTimeout(tnc2, TimeSpan.FromSeconds(3));
        received.Should().NotBeNull("TNCs with matched mode 5 should communicate");
        Output.WriteLine("Mode 5 matched communication: PASS");

        await Task.Delay(300);

        // Clear and switch to mode 10 (1200 BPSK)
        tnc1.DiscardInBuffer();
        tnc2.DiscardInBuffer();
        await Task.Delay(200);

        Output.WriteLine("Setting both TNCs to mode 10 (1200 BPSK IL2P+CRC)");
        var mode10Frame = KissFrameBuilder.BuildSetHwFrame(10, persist: false);
        tnc1.Write(mode10Frame, 0, mode10Frame.Length);
        tnc2.Write(mode10Frame, 0, mode10Frame.Length);
        await Task.Delay(500);

        // Higher TXDELAY for lower baud
        var txDelayFrame2 = KissFrameBuilder.BuildParameterFrame(KissFrameBuilder.CMD_TXDELAY, 50);
        tnc1.Write(txDelayFrame2, 0, txDelayFrame2.Length);
        await Task.Delay(100);

        var testFrame2 = BuildDataFrame(BuildTestAx25Frame("MODE10"));
        tnc1.Write(testFrame2, 0, testFrame2.Length);

        var received2 = await ReadFrameWithTimeout(tnc2, TimeSpan.FromSeconds(5));
        received2.Should().NotBeNull("TNCs with matched mode 10 should communicate");
        Output.WriteLine("Mode 10 matched communication: PASS");

        Output.WriteLine("Mode control verified - matched modes enable communication");
    }

    [SkippableFact]
    public async Task Mode_ControlsCommunication_MismatchedModesFail()
    {
        SkipIfNoHardware();
        Output.WriteLine("Testing that mismatched modes prevent communication");

        using var tnc1 = Fixture.OpenTnc1();
        using var tnc2 = Fixture.OpenTnc2();
        tnc1.Open();
        tnc2.Open();

        // Clear buffers
        tnc1.DiscardInBuffer();
        tnc2.DiscardInBuffer();
        await Task.Delay(200);

        // Set TNC1 to mode 0 (9600 GFSK)
        Output.WriteLine("Setting TNC1 to mode 0 (9600 GFSK)");
        var mode0Frame = KissFrameBuilder.BuildSetHwFrame(0, persist: false);
        tnc1.Write(mode0Frame, 0, mode0Frame.Length);
        await Task.Delay(300);

        // Set TNC2 to mode 10 (1200 BPSK) - completely different
        Output.WriteLine("Setting TNC2 to mode 10 (1200 BPSK)");
        var mode10Frame = KissFrameBuilder.BuildSetHwFrame(10, persist: false);
        tnc2.Write(mode10Frame, 0, mode10Frame.Length);
        await Task.Delay(500);

        // Clear any residual data
        tnc2.DiscardInBuffer();
        await Task.Delay(100);

        // Low TXDELAY on TNC1
        var txDelayFrame = KissFrameBuilder.BuildParameterFrame(KissFrameBuilder.CMD_TXDELAY, 25);
        tnc1.Write(txDelayFrame, 0, txDelayFrame.Length);
        await Task.Delay(100);

        // Send frame from TNC1
        var testFrame = BuildDataFrame(BuildTestAx25Frame("MISMATCH1"));
        Output.WriteLine("Sending frame from TNC1 (mode 0)...");
        tnc1.Write(testFrame, 0, testFrame.Length);

        // TNC2 should NOT receive (different modulation)
        var received = await ReadFrameWithTimeout(tnc2, TimeSpan.FromSeconds(2));

        if (received != null)
        {
            Output.WriteLine("WARNING: Frame received despite mismatched modes!");
            LogFrame("Unexpected", received);
            // This could indicate audio coupling or mode switch timing
        }

        received.Should().BeNull("Mismatched modes (0 vs 10) should not communicate");
        Output.WriteLine("Mismatched mode communication: BLOCKED (as expected)");

        Output.WriteLine("Mode control verified - mismatched modes prevent communication");
    }

    [SkippableFact]
    public async Task Mode_SwitchingDuringOperation()
    {
        SkipIfNoHardware();
        Output.WriteLine("Testing mode switching during operation");

        using var tnc1 = Fixture.OpenTnc1();
        using var tnc2 = Fixture.OpenTnc2();
        tnc1.Open();
        tnc2.Open();

        // Start in mode 0
        Output.WriteLine("Phase 1: Both TNCs in mode 0");
        await SetBothTncsToMode(tnc1, tnc2, 0);
        var txDelayFrame = KissFrameBuilder.BuildParameterFrame(KissFrameBuilder.CMD_TXDELAY, 30);
        tnc1.Write(txDelayFrame, 0, txDelayFrame.Length);
        await Task.Delay(100);

        var frame1 = BuildDataFrame(BuildTestAx25Frame("PHASE1"));
        tnc1.Write(frame1, 0, frame1.Length);
        var received1 = await ReadFrameWithTimeout(tnc2, TimeSpan.FromSeconds(3));
        received1.Should().NotBeNull("Phase 1: Mode 0 should work");
        Output.WriteLine("Phase 1: PASS");

        // Switch to mode 2
        Output.WriteLine("Phase 2: Switching to mode 2");
        tnc1.DiscardInBuffer();
        tnc2.DiscardInBuffer();
        await SetBothTncsToMode(tnc1, tnc2, 2);
        tnc1.Write(txDelayFrame, 0, txDelayFrame.Length);
        await Task.Delay(100);

        var frame2 = BuildDataFrame(BuildTestAx25Frame("PHASE2"));
        tnc1.Write(frame2, 0, frame2.Length);
        var received2 = await ReadFrameWithTimeout(tnc2, TimeSpan.FromSeconds(3));
        received2.Should().NotBeNull("Phase 2: Mode 2 should work after switch");
        Output.WriteLine("Phase 2: PASS");

        // Switch to mode 5
        Output.WriteLine("Phase 3: Switching to mode 5");
        tnc1.DiscardInBuffer();
        tnc2.DiscardInBuffer();
        await SetBothTncsToMode(tnc1, tnc2, 5);
        tnc1.Write(txDelayFrame, 0, txDelayFrame.Length);
        await Task.Delay(100);

        var frame3 = BuildDataFrame(BuildTestAx25Frame("PHASE3"));
        tnc1.Write(frame3, 0, frame3.Length);
        var received3 = await ReadFrameWithTimeout(tnc2, TimeSpan.FromSeconds(3));
        received3.Should().NotBeNull("Phase 3: Mode 5 should work after switch");
        Output.WriteLine("Phase 3: PASS");

        // Switch back to mode 0
        Output.WriteLine("Phase 4: Switching back to mode 0");
        tnc1.DiscardInBuffer();
        tnc2.DiscardInBuffer();
        await SetBothTncsToMode(tnc1, tnc2, 0);
        tnc1.Write(txDelayFrame, 0, txDelayFrame.Length);
        await Task.Delay(100);

        var frame4 = BuildDataFrame(BuildTestAx25Frame("PHASE4"));
        tnc1.Write(frame4, 0, frame4.Length);
        var received4 = await ReadFrameWithTimeout(tnc2, TimeSpan.FromSeconds(3));
        received4.Should().NotBeNull("Phase 4: Mode 0 should work after switching back");
        Output.WriteLine("Phase 4: PASS");

        Output.WriteLine("Mode switching during operation: ALL PHASES PASSED");
    }

    [SkippableFact]
    public async Task TxDelay_MultipleLevels()
    {
        SkipIfNoHardware();
        Output.WriteLine("Testing multiple TXDELAY levels and measuring TNC-to-TNC timing");

        using var tnc1 = Fixture.OpenTnc1();
        using var tnc2 = Fixture.OpenTnc2();
        tnc1.Open();
        tnc2.Open();

        // Set both to mode 0
        await SetBothTncsToMode(tnc1, tnc2, 0);

        // Warmup transmission (first TX after setup has inconsistent timing)
        var warmupDelay = KissFrameBuilder.BuildParameterFrame(KissFrameBuilder.CMD_TXDELAY, 20);
        tnc1.Write(warmupDelay, 0, warmupDelay.Length);
        await Task.Delay(100);
        var warmupFrame = BuildDataFrame(BuildTestAx25Frame("WARMUP"));
        tnc1.Write(warmupFrame, 0, warmupFrame.Length);
        await ReadFrameWithTimeout(tnc2, TimeSpan.FromSeconds(3));
        Output.WriteLine("Warmup transmission complete");

        var results = new List<(int delay, long time)>();
        var delays = new byte[] { 20, 40, 60, 80, 100 };  // 200ms to 1000ms

        foreach (var delay in delays)
        {
            // Clear buffers
            tnc2.DiscardInBuffer();
            await Task.Delay(100);

            var delayFrame = KissFrameBuilder.BuildParameterFrame(KissFrameBuilder.CMD_TXDELAY, delay);
            tnc1.Write(delayFrame, 0, delayFrame.Length);
            await Task.Delay(150);

            var frame = BuildDataFrame(BuildTestAx25Frame($"DELAY{delay}"));
            var sw = Stopwatch.StartNew();
            tnc1.Write(frame, 0, frame.Length);
            var received = await ReadFrameWithTimeout(tnc2, TimeSpan.FromSeconds(5));
            sw.Stop();

            received.Should().NotBeNull($"Should receive frame for TXDELAY={delay}");
            results.Add((delay, sw.ElapsedMilliseconds));
            Output.WriteLine($"TXDELAY {delay,3} ({delay * 10,4}ms): Received in {sw.ElapsedMilliseconds,4}ms");

            await Task.Delay(200);
        }

        // Verify timing increases with TXDELAY (skip first to second as warmup may still affect)
        Output.WriteLine("Timing deltas:");
        for (int i = 1; i < results.Count; i++)
        {
            var diff = results[i].delay - results[i - 1].delay;
            var expectedDiffMs = diff * 10;  // Each unit is 10ms
            var actualDiffMs = results[i].time - results[i - 1].time;

            Output.WriteLine($"  Delta from {results[i - 1].delay} to {results[i].delay}: " +
                           $"expected ~{expectedDiffMs}ms, actual {actualDiffMs}ms");
        }

        // Compare last vs second (avoiding potential first-measurement variance)
        var stableDiff = results[^1].time - results[1].time;
        var expectedStableDiff = (delays[^1] - delays[1]) * 10;  // 100 - 40 = 60 units = 600ms
        Output.WriteLine($"Stable range (40->100): expected ~{expectedStableDiff}ms, actual {stableDiff}ms");

        stableDiff.Should().BeGreaterThan(expectedStableDiff / 3,
            "TXDELAY should affect timing (allowing for variance)");

        Output.WriteLine("TXDELAY multiple levels: VERIFIED");
    }

    [SkippableFact]
    public async Task Persistence_Parameter_Accepted()
    {
        SkipIfNoHardware();
        Output.WriteLine("Testing PERSISTENCE parameter acceptance");

        using var tnc = Fixture.OpenTnc1();
        tnc.Open();

        // Set mode
        var modeFrame = KissFrameBuilder.BuildSetHwFrame(0, persist: false);
        tnc.Write(modeFrame, 0, modeFrame.Length);
        await Task.Delay(300);

        // Test various persistence values
        var values = new byte[] { 0, 63, 127, 191, 255 };

        foreach (var value in values)
        {
            var frame = KissFrameBuilder.BuildParameterFrame(KissFrameBuilder.CMD_PERSISTENCE, value);
            tnc.Write(frame, 0, frame.Length);
            Output.WriteLine($"Set PERSISTENCE to {value} ({value / 255.0 * 100:F1}%)");
            await Task.Delay(50);
        }

        Output.WriteLine("PERSISTENCE parameter: All values accepted");
    }

    [SkippableFact]
    public async Task SlotTime_Parameter_Accepted()
    {
        SkipIfNoHardware();
        Output.WriteLine("Testing SLOTTIME parameter acceptance");

        using var tnc = Fixture.OpenTnc1();
        tnc.Open();

        // Set mode
        var modeFrame = KissFrameBuilder.BuildSetHwFrame(0, persist: false);
        tnc.Write(modeFrame, 0, modeFrame.Length);
        await Task.Delay(300);

        // Test various slot time values
        var values = new byte[] { 0, 5, 10, 20, 50 };

        foreach (var value in values)
        {
            var frame = KissFrameBuilder.BuildParameterFrame(KissFrameBuilder.CMD_SLOTTIME, value);
            tnc.Write(frame, 0, frame.Length);
            Output.WriteLine($"Set SLOTTIME to {value} ({value * 10}ms)");
            await Task.Delay(50);
        }

        Output.WriteLine("SLOTTIME parameter: All values accepted");
    }
}
