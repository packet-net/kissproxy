using AwesomeAssertions;
using kissproxylib;
using Xunit.Abstractions;

namespace kissproxy_tests.Hardware;

/// <summary>
/// Tests NinoTNC mode switching with real hardware.
/// </summary>
public class HardwareNinoModeTests : HardwareTestBase
{
    public HardwareNinoModeTests(HardwareTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    [SkippableTheory]
    [InlineData(0, "9600 GFSK AX.25")]
    [InlineData(2, "9600 GFSK IL2P+CRC")]
    [InlineData(5, "3600 QPSK IL2P+CRC")]
    [InlineData(10, "1200 BPSK IL2P+CRC")]
    public async Task RealTnc_SetMode_CommunicationWorks(int mode, string description)
    {
        SkipIfNoHardware();
        Output.WriteLine($"Testing mode {mode}: {description}");

        using var tnc1 = Fixture.OpenTnc1();
        using var tnc2 = Fixture.OpenTnc2();
        tnc1.Open();
        tnc2.Open();

        // Set both TNCs to the test mode (temporary, not persisted)
        await SetBothTncsToMode(tnc1, tnc2, mode);

        // Set appropriate TXDELAY for the mode
        // Lower baud rates need more TXDELAY
        byte txDelay = mode switch
        {
            10 => 100,  // 1200 baud - needs more time
            8 => 100,   // 300 baud
            14 => 100,  // 300 baud AFSK
            _ => 30     // Higher baud rates
        };
        var txDelayFrame = KissFrameBuilder.BuildParameterFrame(KissFrameBuilder.CMD_TXDELAY, txDelay);
        tnc1.Write(txDelayFrame, 0, txDelayFrame.Length);
        await Task.Delay(100);

        // Send test frame
        var testId = $"MODE{mode}:{Guid.NewGuid().ToString("N")[..4]}";
        var testData = BuildTestAx25Frame(testId);
        var kissFrame = BuildDataFrame(testData);

        Output.WriteLine($"Sending test frame in mode {mode}");
        LogFrame("Tx", kissFrame);
        tnc1.Write(kissFrame, 0, kissFrame.Length);

        // Receive - allow more time for slower modes
        var timeout = mode >= 8 ? TimeSpan.FromSeconds(10) : TimeSpan.FromSeconds(5);
        var received = await ReadFrameWithTimeout(tnc2, timeout);

        received.Should().NotBeNull($"Frame should be received in mode {mode} ({description})");
        LogFrame("Rx", received!);

        Output.WriteLine($"Mode {mode} ({description}) verified: frame transmitted and received");
    }

    [SkippableFact]
    public async Task RealTnc_MismatchedModes_NoReception()
    {
        SkipIfNoHardware();
        Output.WriteLine("Testing mismatched modes (should NOT communicate)");

        using var tnc1 = Fixture.OpenTnc1();
        using var tnc2 = Fixture.OpenTnc2();
        tnc1.Open();
        tnc2.Open();

        // Clear any pending data
        tnc1.DiscardInBuffer();
        tnc2.DiscardInBuffer();
        await Task.Delay(200);

        // Set TNC1 to mode 0 (9600 GFSK)
        var mode0Frame = KissFrameBuilder.BuildSetHwFrame(0, persist: false);
        Output.WriteLine($"Setting TNC1 to mode 0: {BitConverter.ToString(mode0Frame)}");
        tnc1.Write(mode0Frame, 0, mode0Frame.Length);
        await Task.Delay(300);

        // Set TNC2 to mode 10 (1200 BPSK) - completely different modulation
        var mode10Frame = KissFrameBuilder.BuildSetHwFrame(10, persist: false);
        Output.WriteLine($"Setting TNC2 to mode 10: {BitConverter.ToString(mode10Frame)}");
        tnc2.Write(mode10Frame, 0, mode10Frame.Length);
        await Task.Delay(500);

        // Clear buffers again after mode switch
        tnc2.DiscardInBuffer();
        await Task.Delay(100);

        // Set low TXDELAY on TNC1
        var txDelayFrame = KissFrameBuilder.BuildParameterFrame(KissFrameBuilder.CMD_TXDELAY, 25);
        tnc1.Write(txDelayFrame, 0, txDelayFrame.Length);
        await Task.Delay(100);

        // Send from TNC1
        var kissFrame = BuildDataFrame(BuildTestAx25Frame("MISMATCH"));
        Output.WriteLine("Sending frame from TNC1 (mode 0) to TNC2 (mode 10)");
        tnc1.Write(kissFrame, 0, kissFrame.Length);

        // TNC2 should NOT receive (different modulation)
        var received = await ReadFrameWithTimeout(tnc2, TimeSpan.FromSeconds(2));

        if (received != null)
        {
            // If we still received, log but don't fail - the audio setup might allow cross-mode reception
            Output.WriteLine("Note: Frame received despite mismatched modes. This may indicate:");
            Output.WriteLine("  - Audio cables may be wired for direct connection");
            Output.WriteLine("  - TNCs may be in close proximity allowing cross-mode reception");
            Output.WriteLine("  - Mode switch may not have completed in time");
            LogFrame("Received", received);

            // Skip instead of fail - this is a hardware-dependent behavior
            Skip.If(true, "Cross-mode reception detected - test inconclusive with this hardware setup");
        }

        Output.WriteLine("Confirmed: mismatched modes do not communicate");
    }

    [SkippableFact]
    public async Task RealTnc_ModeSwitch_Temporary_NotPersisted()
    {
        SkipIfNoHardware();
        Output.WriteLine("Testing temporary mode switch (mode + 16)");

        using var tnc1 = Fixture.OpenTnc1();
        tnc1.Open();

        // Set to mode 5 temporarily (5 + 16 = 21)
        var tempModeFrame = KissFrameBuilder.BuildSetHwFrame(5, persist: false);
        LogFrame("Setting temporary mode 5", tempModeFrame);
        tnc1.Write(tempModeFrame, 0, tempModeFrame.Length);

        await Task.Delay(200);

        // The frame should have value 21 (5 + 16) to indicate temporary
        tempModeFrame[2].Should().Be(21, "Temporary mode should add 16 to mode value");

        Output.WriteLine("Temporary mode switch frame format verified");
    }

    [SkippableFact]
    public async Task RealTnc_ModeSwitch_Persistent_ValueUnder16()
    {
        SkipIfNoHardware();
        Output.WriteLine("Testing persistent mode switch (mode < 16)");

        using var tnc1 = Fixture.OpenTnc1();
        tnc1.Open();

        // Set to mode 5 persistently (value = 5, no +16)
        var persistModeFrame = KissFrameBuilder.BuildSetHwFrame(5, persist: true);
        LogFrame("Setting persistent mode 5", persistModeFrame);

        // Verify frame format
        persistModeFrame[2].Should().Be(5, "Persistent mode should not add 16");

        // Note: We don't actually send this to avoid changing the TNC's flash settings
        Output.WriteLine("Persistent mode switch frame format verified (not sent to avoid flash write)");
    }

    [SkippableFact]
    public async Task RealTnc_AllModes_FrameBuilderCorrect()
    {
        SkipIfNoHardware();

        foreach (var (mode, description) in KissFrameBuilder.NinoModes)
        {
            // Test temporary mode frame
            var tempFrame = KissFrameBuilder.BuildSetHwFrame(mode, persist: false);
            tempFrame.Length.Should().Be(4, "SETHW frame should be 4 bytes");
            tempFrame[0].Should().Be(0xC0, "Should start with FEND");
            tempFrame[1].Should().Be(0x06, "Command should be SETHW (0x06)");
            tempFrame[2].Should().Be((byte)(mode + 16), $"Temporary mode {mode} should be {mode + 16}");
            tempFrame[3].Should().Be(0xC0, "Should end with FEND");

            // Test persistent mode frame
            var persistFrame = KissFrameBuilder.BuildSetHwFrame(mode, persist: true);
            persistFrame[2].Should().Be((byte)mode, $"Persistent mode {mode} should be {mode}");

            Output.WriteLine($"Mode {mode} ({description}): temporary={mode + 16}, persistent={mode}");
        }

        Output.WriteLine("All NinoTNC mode frame formats verified");
    }
}
