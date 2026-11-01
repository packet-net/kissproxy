using AwesomeAssertions;
using kissproxy;
using kissproxylib;

namespace kissproxy_tests;

public class KissFrameBuilderTests
{
    private const byte FEND = 0xC0;

    [Fact]
    public void BuildCommandByte_Port0Command1_ReturnsCorrectByte()
    {
        var result = KissFrameBuilder.BuildCommandByte(0x01, 0);
        result.Should().Be(0x01);
    }

    [Fact]
    public void BuildCommandByte_Port2Command3_ReturnsCorrectByte()
    {
        var result = KissFrameBuilder.BuildCommandByte(0x03, 2);
        result.Should().Be(0x23); // (2 << 4) | 3 = 32 + 3 = 35 = 0x23
    }

    [Fact]
    public void ParseCommandByte_ReturnsCorrectCommandAndPort()
    {
        var (command, port) = KissFrameBuilder.ParseCommandByte(0x23);
        command.Should().Be(0x03);
        port.Should().Be(2);
    }

    [Fact]
    public void ParseCommandByte_Port0_ReturnsCorrectValues()
    {
        var (command, port) = KissFrameBuilder.ParseCommandByte(0x01);
        command.Should().Be(0x01);
        port.Should().Be(0);
    }

    [Fact]
    public void BuildParameterFrame_TxDelay_ReturnsCorrectFrame()
    {
        var frame = KissFrameBuilder.BuildParameterFrame(KissFrameBuilder.CMD_TXDELAY, 50);
        frame.Should().Equal(FEND, 0x01, 50, FEND);
    }

    [Fact]
    public void BuildParameterFrame_WithPort_ReturnsCorrectFrame()
    {
        var frame = KissFrameBuilder.BuildParameterFrame(KissFrameBuilder.CMD_PERSISTENCE, 63, port: 1);
        frame.Should().Equal(FEND, 0x12, 63, FEND); // (1 << 4) | 2 = 0x12
    }

    [Fact]
    public void BuildSetHwFrame_PersistMode_ReturnsCorrectFrame()
    {
        var frame = KissFrameBuilder.BuildSetHwFrame(mode: 5, persist: true);
        // SETHW is command 6, mode 5 with persist = no +16
        frame.Should().Equal(FEND, 0x06, 5, FEND);
    }

    [Fact]
    public void BuildSetHwFrame_TemporaryMode_ReturnsCorrectFrame()
    {
        var frame = KissFrameBuilder.BuildSetHwFrame(mode: 5, persist: false);
        // SETHW is command 6, mode 5 + 16 (temporary) = 21
        frame.Should().Equal(FEND, 0x06, 21, FEND);
    }

    [Fact]
    public void GetCommandByteFromFrame_ValidFrame_ReturnsCommandByte()
    {
        var frame = new byte[] { FEND, 0x23, 0x45, FEND };
        var result = KissFrameBuilder.GetCommandByteFromFrame(frame);
        result.Should().Be(0x23);
    }

    [Fact]
    public void GetCommandByteFromFrame_ShortFrame_ReturnsNull()
    {
        var frame = new byte[] { FEND, 0x01 };
        var result = KissFrameBuilder.GetCommandByteFromFrame(frame);
        result.Should().BeNull();
    }

    [Fact]
    public void GetCommandName_ReturnsCorrectNames()
    {
        KissFrameBuilder.GetCommandName(0x00).Should().Be("DataFrame");
        KissFrameBuilder.GetCommandName(0x01).Should().Be("TxDelay");
        KissFrameBuilder.GetCommandName(0x02).Should().Be("Persistence");
        KissFrameBuilder.GetCommandName(0x03).Should().Be("SlotTime");
        KissFrameBuilder.GetCommandName(0x04).Should().Be("TxTail");
        KissFrameBuilder.GetCommandName(0x05).Should().Be("FullDuplex");
        KissFrameBuilder.GetCommandName(0x06).Should().Be("SetHardware");
        KissFrameBuilder.GetCommandName(0x0C).Should().Be("AckMode");
        KissFrameBuilder.GetCommandName(0xFF).Should().Be("Return");
        KissFrameBuilder.GetCommandName(0x99).Should().Be("Unknown(153)");
    }

    [Fact]
    public void CMD_ACKMODE_HasCorrectValue()
    {
        KissFrameBuilder.CMD_ACKMODE.Should().Be(0x0C);
    }

    [Fact]
    public void ShouldFilter_DataFrame_ReturnsFalse()
    {
        var config = new Config { Id = "test", ComPort = "/dev/ttyACM0", FilterTxDelay = true };
        var result = KissFrameBuilder.ShouldFilter(KissFrameBuilder.CMD_DATAFRAME, config);
        result.Should().BeFalse();
    }

    [Fact]
    public void ShouldFilter_FilteredCommand_ReturnsTrue()
    {
        var config = new Config { Id = "test", ComPort = "/dev/ttyACM0", FilterTxDelay = true };
        var result = KissFrameBuilder.ShouldFilter(KissFrameBuilder.CMD_TXDELAY, config);
        result.Should().BeTrue();
    }

    [Fact]
    public void ShouldFilter_UnfilteredCommand_ReturnsFalse()
    {
        var config = new Config { Id = "test", ComPort = "/dev/ttyACM0", FilterTxDelay = false };
        var result = KissFrameBuilder.ShouldFilter(KissFrameBuilder.CMD_TXDELAY, config);
        result.Should().BeFalse();
    }

    [Fact]
    public void BuildAllParameterFrames_WithAllValues_ReturnsAllFrames()
    {
        var config = new Config
        {
            Id = "test",
            ComPort = "/dev/ttyACM0",
            FilterTxDelay = true,
            FilterPersistence = true,
            FilterSlotTime = true,
            FilterTxTail = true,
            FilterFullDuplex = true,
            TxDelayValue = 500,
            PersistenceValue = 63,
            SlotTimeValue = 100,
            TxTailValue = 0,
            FullDuplexValue = false,
            NinoMode = 5,
            PersistNinoMode = false
        };

        var frames = KissFrameBuilder.BuildAllParameterFrames(config);
        frames.Should().HaveCount(6);
    }

    [Fact]
    public void BuildAllParameterFrames_ValueWithToggleOff_IsNotSent()
    {
        // Turning a toggle off in the UI leaves the value in the config; it must stop being forced
        var config = new Config { Id = "t", ComPort = "x", FilterTxDelay = false, TxDelayValue = 500, PersistenceValue = 63 };
        KissFrameBuilder.BuildAllParameterFrames(config).Should().BeEmpty();
    }

    [Theory]
    [InlineData((byte)0xC0, new byte[] { 0xC0, 0x02, 0xDB, 0xDC, 0xC0 })]
    [InlineData((byte)0xDB, new byte[] { 0xC0, 0x02, 0xDB, 0xDD, 0xC0 })]
    [InlineData((byte)0x3F, new byte[] { 0xC0, 0x02, 0x3F, 0xC0 })]
    public void BuildParameterFrame_EscapesFendAndFesc(byte value, byte[] expected)
    {
        KissFrameBuilder.BuildParameterFrame(KissFrameBuilder.CMD_PERSISTENCE, value).Should().Equal(expected);
    }

    [Fact]
    public void BuildAllParameterFrames_WithNoValues_ReturnsEmptyArray()
    {
        var config = new Config { Id = "test", ComPort = "/dev/ttyACM0" };
        var frames = KissFrameBuilder.BuildAllParameterFrames(config);
        frames.Should().BeEmpty();
    }

    // --- KISS spec compliance tests (https://github.com/packethacking/ax25spec/blob/main/doc/kiss-tnc-protocol.md#4-control-of-the-kiss-tnc) ---

    /// <summary>
    /// Per KISS spec: TxDelay is in 10ms units. Config stores ms, wire format is ms/10.
    /// Default start-up value per spec: 50 (= 500ms).
    /// </summary>
    [Theory]
    [InlineData(0, 0)]        // 0ms → wire byte 0
    [InlineData(100, 10)]     // 100ms → wire byte 10
    [InlineData(500, 50)]     // 500ms (spec default) → wire byte 50
    [InlineData(2550, 255)]   // max → wire byte 255
    [InlineData(2560, 255)]   // clamped to 255
    public void BuildAllParameterFrames_TxDelay_ConvertsMsTo10msUnits(int configMs, byte expectedWireByte)
    {
        var config = new Config { Id = "t", ComPort = "x", FilterTxDelay = true, TxDelayValue = configMs };
        var frames = KissFrameBuilder.BuildAllParameterFrames(config);
        frames.Should().HaveCount(1);
        frames[0].Should().Equal(FEND, 0x01, expectedWireByte, FEND);
    }

    /// <summary>
    /// Per KISS spec: SlotTime is in 10ms units. Default: 10 (= 100ms).
    /// </summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(100, 10)]     // spec default → wire byte 10
    [InlineData(500, 50)]
    [InlineData(2550, 255)]
    public void BuildAllParameterFrames_SlotTime_ConvertsMsTo10msUnits(int configMs, byte expectedWireByte)
    {
        var config = new Config { Id = "t", ComPort = "x", FilterSlotTime = true, SlotTimeValue = configMs };
        var frames = KissFrameBuilder.BuildAllParameterFrames(config);
        frames.Should().HaveCount(1);
        frames[0].Should().Equal(FEND, 0x03, expectedWireByte, FEND);
    }

    /// <summary>
    /// Per KISS spec: TxTail is in 10ms units.
    /// </summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(300, 30)]
    [InlineData(2550, 255)]
    public void BuildAllParameterFrames_TxTail_ConvertsMsTo10msUnits(int configMs, byte expectedWireByte)
    {
        var config = new Config { Id = "t", ComPort = "x", FilterTxTail = true, TxTailValue = configMs };
        var frames = KissFrameBuilder.BuildAllParameterFrames(config);
        frames.Should().HaveCount(1);
        frames[0].Should().Equal(FEND, 0x04, expectedWireByte, FEND);
    }

    /// <summary>
    /// Per KISS spec: Persistence (P) is a raw byte 0-255. p = (P+1)/256.
    /// Default: P=63 (p ≈ 0.25).
    /// The config value IS the raw byte — no conversion on the wire.
    /// </summary>
    [Theory]
    [InlineData(0, 0)]        // p = 1/256 ≈ 0.004
    [InlineData(63, 63)]      // spec default: p = 64/256 = 0.25
    [InlineData(127, 127)]    // p = 128/256 = 0.5
    [InlineData(255, 255)]    // p = 256/256 = 1.0 (always transmit)
    public void BuildAllParameterFrames_Persistence_RawByteNoConversion(int configValue, byte expectedWireByte)
    {
        var config = new Config { Id = "t", ComPort = "x", FilterPersistence = true, PersistenceValue = configValue };
        var frames = KissFrameBuilder.BuildAllParameterFrames(config);
        frames.Should().HaveCount(1);
        frames[0].Should().Equal(FEND, 0x02, expectedWireByte, FEND);
    }

    /// <summary>
    /// Per KISS spec: FullDuplex is 0 for half duplex, nonzero for full duplex.
    /// </summary>
    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public void BuildAllParameterFrames_FullDuplex_BooleanToWireByte(bool configValue, byte expectedWireByte)
    {
        var config = new Config { Id = "t", ComPort = "x", FilterFullDuplex = true, FullDuplexValue = configValue };
        var frames = KissFrameBuilder.BuildAllParameterFrames(config);
        frames.Should().HaveCount(1);
        frames[0].Should().Equal(FEND, 0x05, expectedWireByte, FEND);
    }

    /// <summary>
    /// Per KISS spec: TxDelay, SlotTime, TxTail are in 10ms units.
    /// Verifies the FormatParameterValue method shows correct human-readable interpretation.
    /// </summary>
    [Theory]
    [InlineData(KissFrameBuilder.CMD_TXDELAY, 0, "0 (= 0ms, spec: value × 10ms)")]
    [InlineData(KissFrameBuilder.CMD_TXDELAY, 10, "10 (= 100ms, spec: value × 10ms)")]
    [InlineData(KissFrameBuilder.CMD_TXDELAY, 50, "50 (= 500ms, spec: value × 10ms)")]
    [InlineData(KissFrameBuilder.CMD_SLOTTIME, 10, "10 (= 100ms, spec: value × 10ms)")]
    [InlineData(KissFrameBuilder.CMD_TXTAIL, 30, "30 (= 300ms, spec: value × 10ms)")]
    public void FormatParameterValue_TimeParams_ShowsMilliseconds(byte command, int value, string expected)
    {
        KissFrameBuilder.FormatParameterValue(command, value).Should().Be(expected);
    }

    /// <summary>
    /// Per KISS spec: Persistence P is 0-255, real probability p = (P+1)/256.
    /// </summary>
    [Theory]
    [InlineData(0, "0 (= p 0.004, spec: p = (P+1)/256)")]
    [InlineData(63, "63 (= p 0.250, spec: p = (P+1)/256)")]       // spec default
    [InlineData(127, "127 (= p 0.500, spec: p = (P+1)/256)")]
    [InlineData(255, "255 (= p 1.000, spec: p = (P+1)/256)")]
    public void FormatParameterValue_Persistence_ShowsProbability(int value, string expected)
    {
        KissFrameBuilder.FormatParameterValue(KissFrameBuilder.CMD_PERSISTENCE, value).Should().Be(expected);
    }

    [Theory]
    [InlineData(0, "0 (= half duplex)")]
    [InlineData(1, "1 (= full duplex)")]
    [InlineData(255, "255 (= full duplex)")]
    public void FormatParameterValue_FullDuplex_ShowsMode(int value, string expected)
    {
        KissFrameBuilder.FormatParameterValue(KissFrameBuilder.CMD_FULLDUPLEX, value).Should().Be(expected);
    }

    /// <summary>
    /// Per KISS spec: command is in low nibble, port in high nibble of command byte.
    /// Verifies all 5 mandatory commands + optional ones are correctly encoded across ports.
    /// </summary>
    [Theory]
    [InlineData(0x01, 0, 0x01)]  // TxDelay, port 0
    [InlineData(0x02, 0, 0x02)]  // Persistence, port 0
    [InlineData(0x03, 0, 0x03)]  // SlotTime, port 0
    [InlineData(0x04, 0, 0x04)]  // TxTail, port 0
    [InlineData(0x05, 0, 0x05)]  // FullDuplex, port 0
    [InlineData(0x01, 1, 0x11)]  // TxDelay, port 1
    [InlineData(0x02, 2, 0x22)]  // Persistence, port 2
    [InlineData(0x03, 4, 0x43)]  // SlotTime, port 4
    [InlineData(0x01, 15, 0xF1)] // TxDelay, port 15 (max)
    public void BuildParameterFrame_AllPorts_EncodesCorrectCommandByte(byte command, int port, byte expectedCmdByte)
    {
        var frame = KissFrameBuilder.BuildParameterFrame(command, 42, port);
        frame.Should().Equal(FEND, expectedCmdByte, 42, FEND);
    }

    [Fact]
    public void NinoModes_ContainsExpectedModes()
    {
        KissFrameBuilder.NinoModes.Should().ContainKey(0);
        KissFrameBuilder.NinoModes.Should().ContainKey(5);
        KissFrameBuilder.NinoModes.Should().ContainKey(10);
        KissFrameBuilder.NinoModes.Should().ContainKey(14);

        KissFrameBuilder.NinoModes[0].Should().Contain("9600");
        KissFrameBuilder.NinoModes[10].Should().Contain("1200");
    }

    // --- AX.25 UI frame and KISS data frame tests ---

    [Fact]
    public void EncodeCallsign_SimpleCall_ReturnsCorrectBytes()
    {
        var result = KissFrameBuilder.EncodeCallsign("CQ", false);
        result.Should().HaveCount(7);
        // 'C' = 0x43, shifted left = 0x86
        result[0].Should().Be(0x86);
        // 'Q' = 0x51, shifted left = 0xA2
        result[1].Should().Be(0xA2);
        // Remaining positions are space (0x20), shifted left = 0x40
        result[2].Should().Be(0x40);
        result[3].Should().Be(0x40);
        result[4].Should().Be(0x40);
        result[5].Should().Be(0x40);
        // SSID byte: 0x60 | (0 << 1) | 0 = 0x60
        result[6].Should().Be(0x60);
    }

    [Fact]
    public void EncodeCallsign_WithSsid_ReturnsCorrectSsidByte()
    {
        var result = KissFrameBuilder.EncodeCallsign("VK2ABC-5", false);
        result.Should().HaveCount(7);
        // SSID byte: 0x60 | (5 << 1) | 0 = 0x60 | 0x0A = 0x6A
        result[6].Should().Be(0x6A);
    }

    [Fact]
    public void EncodeCallsign_LastAddress_SetsEndBit()
    {
        var result = KissFrameBuilder.EncodeCallsign("CQ", true);
        // SSID byte with end bit: 0x60 | (0 << 1) | 1 = 0x61
        result[6].Should().Be(0x61);
    }

    [Fact]
    public void EncodeCallsign_LastAddressWithSsid_SetsEndBit()
    {
        var result = KissFrameBuilder.EncodeCallsign("TEST-15", true);
        // SSID byte: 0x60 | (15 << 1) | 1 = 0x60 | 0x1E | 0x01 = 0x7F
        result[6].Should().Be(0x7F);
    }

    [Fact]
    public void EncodeCallsign_EmptyCallsign_Throws()
    {
        var action = () => KissFrameBuilder.EncodeCallsign("", false);
        action.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void EncodeCallsign_TooLongCallsign_Throws()
    {
        var action = () => KissFrameBuilder.EncodeCallsign("TOOLONG1", false);
        action.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void EncodeCallsign_InvalidSsid_Throws()
    {
        var action = () => KissFrameBuilder.EncodeCallsign("TEST-16", false);
        action.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void BuildAx25UiFrame_ProducesCorrectFrame()
    {
        var info = new byte[] { 0x48, 0x65, 0x6C, 0x6C, 0x6F }; // "Hello"
        var frame = KissFrameBuilder.BuildAx25UiFrame("SRC", "DST", info);

        // Dest (7) + Src (7) + Control (1) + PID (1) + Info (5) = 21 bytes
        frame.Should().HaveCount(21);

        // Control field at offset 14 should be 0x03 (UI)
        frame[14].Should().Be(0x03);

        // PID at offset 15 should be 0xF0 (No layer 3)
        frame[15].Should().Be(0xF0);

        // Info field
        frame[16].Should().Be(0x48); // 'H'
        frame[17].Should().Be(0x65); // 'e'

        // Source address (last) should have end-of-address bit set
        (frame[13] & 0x01).Should().Be(1);
        // Dest address (not last) should not have end-of-address bit
        (frame[6] & 0x01).Should().Be(0);
    }

    [Fact]
    public void BuildKissDataFrame_WrapsCorrectly()
    {
        var ax25Data = new byte[] { 0x01, 0x02, 0x03 };
        var frame = KissFrameBuilder.BuildKissDataFrame(ax25Data);

        // FEND + cmd + data + FEND = 6 bytes
        frame.Should().HaveCount(6);
        frame[0].Should().Be(FEND);
        frame[1].Should().Be(0x00); // CMD_DATAFRAME on port 0
        frame[2].Should().Be(0x01);
        frame[3].Should().Be(0x02);
        frame[4].Should().Be(0x03);
        frame[5].Should().Be(FEND);
    }

    [Fact]
    public void BuildKissDataFrame_WithPort_SetsCorrectCommandByte()
    {
        var ax25Data = new byte[] { 0x01 };
        var frame = KissFrameBuilder.BuildKissDataFrame(ax25Data, port: 3);

        frame[1].Should().Be(0x30); // (3 << 4) | 0x00
    }

    [Fact]
    public void BuildKissDataFrame_EscapesFendInPayload()
    {
        var ax25Data = new byte[] { 0x01, FEND, 0x03 };
        var frame = KissFrameBuilder.BuildKissDataFrame(ax25Data);

        // FEND + cmd + 0x01 + FESC + TFEND + 0x03 + FEND = 7 bytes
        frame.Should().HaveCount(7);
        frame[0].Should().Be(FEND);
        frame[1].Should().Be(0x00);
        frame[2].Should().Be(0x01);
        frame[3].Should().Be(KissFrameBuilder.FESC);
        frame[4].Should().Be(KissFrameBuilder.TFEND);
        frame[5].Should().Be(0x03);
        frame[6].Should().Be(FEND);
    }

    [Fact]
    public void BuildKissDataFrame_EscapesFescInPayload()
    {
        var ax25Data = new byte[] { KissFrameBuilder.FESC };
        var frame = KissFrameBuilder.BuildKissDataFrame(ax25Data);

        // FEND + cmd + FESC + TFESC + FEND = 5 bytes
        frame.Should().HaveCount(5);
        frame[2].Should().Be(KissFrameBuilder.FESC);
        frame[3].Should().Be(KissFrameBuilder.TFESC);
    }
}
