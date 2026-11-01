using kissproxy;

namespace kissproxylib;

/// <summary>
/// Information about a NinoTNC mode including bit rate for timing calculations.
/// </summary>
public record NinoModeInfo(int Mode, string Name, int BitRateHz)
{
    /// <summary>
    /// Calculate transmission duration for a frame of the given size.
    /// </summary>
    /// <param name="frameBytes">Size of the frame in bytes (AX.25 payload, not KISS framing)</param>
    /// <returns>Transmission duration in milliseconds</returns>
    public double CalculateTransmissionMs(int frameBytes)
    {
        return (frameBytes * 8.0 / BitRateHz) * 1000.0;
    }
}

/// <summary>
/// Utilities for building and parsing KISS protocol frames.
/// </summary>
public static class KissFrameBuilder
{
    public const byte FEND = 0xC0;
    public const byte FESC = 0xDB;
    public const byte TFEND = 0xDC;
    public const byte TFESC = 0xDD;

    // KISS command codes (low nibble of command byte)
    public const byte CMD_DATAFRAME = 0x00;
    public const byte CMD_TXDELAY = 0x01;
    public const byte CMD_PERSISTENCE = 0x02;
    public const byte CMD_SLOTTIME = 0x03;
    public const byte CMD_TXTAIL = 0x04;
    public const byte CMD_FULLDUPLEX = 0x05;
    public const byte CMD_SETHW = 0x06;
    public const byte CMD_ACKMODE = 0x0C;    // Multi-drop KISS acknowledgment mode
    public const byte CMD_RETURN = 0xFF;

    /// <summary>
    /// NinoTNC mode definitions with human-readable names (by DIP switch position).
    /// </summary>
    public static readonly IReadOnlyDictionary<int, string> NinoModes = new Dictionary<int, string>
    {
        { 0, "9600 GFSK AX.25" },
        { 1, "19200 4FSK" },
        { 2, "9600 GFSK IL2P+CRC" },
        { 3, "9600 4FSK" },
        { 4, "4800 GFSK IL2P+CRC" },
        { 5, "3600 QPSK IL2P+CRC" },
        { 6, "1200 AFSK AX.25" },
        { 7, "1200 AFSK IL2P+CRC" },
        { 8, "300 BPSK IL2P+CRC" },
        { 9, "600 QPSK IL2P+CRC" },
        { 10, "1200 BPSK IL2P+CRC" },
        { 11, "2400 QPSK IL2P+CRC" },
        { 12, "300 AFSK AX.25" },
        { 13, "300 AFSKPLL IL2P" },
        { 14, "300 AFSKPLL IL2P+CRC" },
        { 15, "Set from KISS" }
    };

    /// <summary>
    /// NinoTNC internal firmware mode values (from BrdSwchMod ZZZZ bytes) to mode names.
    /// Used when DIP switches are set to 1111 (mode 15, "Set from KISS") to decode the actual operating mode.
    /// These values are firmware version specific (v3.44).
    /// </summary>
    public static readonly IReadOnlyDictionary<int, string> NinoFirmwareModes = new Dictionary<int, string>
    {
        { 0x00, "9600 GFSK AX.25" },        // Switch 0
        { 0x41, "19200 4FSK" },             // Switch 1
        { 0xB0, "9600 GFSK IL2P+CRC" },     // Switch 2
        { 0x40, "9600 4FSK" },              // Switch 3
        { 0xA3, "4800 GFSK IL2P+CRC" },     // Switch 4
        { 0xF1, "3600 QPSK IL2P+CRC" },     // Switch 5
        { 0x02, "1200 AFSK AX.25" },        // Switch 6
        { 0x93, "1200 AFSK IL2P+CRC" },     // Switch 7
        { 0x91, "300 BPSK IL2P+CRC" },      // Switch 8
        { 0x92, "600 QPSK IL2P+CRC" },      // Switch 9
        { 0xA0, "1200 BPSK IL2P+CRC" },     // Switch 10
        { 0xA2, "2400 QPSK IL2P+CRC" },     // Switch 11
        { 0x31, "300 AFSK AX.25" },         // Switch 12
        { 0x22, "300 AFSKPLL IL2P" },       // Switch 13
        { 0x23, "300 AFSKPLL IL2P+CRC" },   // Switch 14
        { 0xF3, "Set from KISS" }           // Switch 15 (shouldn't normally see this)
    };

    /// <summary>
    /// NinoTNC mode information including bit rates for timing calculations.
    /// Bit rates are the raw data rate (symbol rate × bits per symbol).
    /// </summary>
    public static readonly IReadOnlyDictionary<int, NinoModeInfo> NinoModeInfos = new Dictionary<int, NinoModeInfo>
    {
        { 0, new(0, "9600 GFSK AX.25", 9600) },
        { 1, new(1, "19200 4FSK", 19200) },
        { 2, new(2, "9600 GFSK IL2P+CRC", 9600) },
        { 3, new(3, "9600 4FSK", 9600) },
        { 4, new(4, "4800 GFSK IL2P+CRC", 4800) },
        { 5, new(5, "3600 QPSK IL2P+CRC", 3600) },
        { 6, new(6, "1200 AFSK AX.25", 1200) },
        { 7, new(7, "1200 AFSK IL2P+CRC", 1200) },
        { 8, new(8, "300 BPSK IL2P+CRC", 300) },
        { 9, new(9, "600 QPSK IL2P+CRC", 600) },
        { 10, new(10, "1200 BPSK IL2P+CRC", 1200) },
        { 11, new(11, "2400 QPSK IL2P+CRC", 2400) },
        { 12, new(12, "300 AFSK AX.25", 300) },
        { 13, new(13, "300 AFSKPLL IL2P", 300) },
        { 14, new(14, "300 AFSKPLL IL2P+CRC", 300) },
        { 15, new(15, "Set from KISS", 0) }  // Variable rate, set via KISS command
    };

    /// <summary>
    /// Gets mode info for a given mode number. Returns null if mode is unknown.
    /// </summary>
    public static NinoModeInfo? GetModeInfo(int mode)
    {
        return NinoModeInfos.TryGetValue(mode, out var info) ? info : null;
    }

    /// <summary>
    /// Builds a KISS command byte from port and command.
    /// </summary>
    public static byte BuildCommandByte(byte command, int port = 0)
    {
        return (byte)((port << 4) | (command & 0x0F));
    }

    /// <summary>
    /// Parses a KISS command byte to extract command code and port.
    /// </summary>
    public static (byte command, int port) ParseCommandByte(byte cmdByte)
    {
        return ((byte)(cmdByte & 0x0F), cmdByte >> 4);
    }

    /// <summary>
    /// Gets the human-readable name for a KISS command code.
    /// </summary>
    public static string GetCommandName(byte command)
    {
        return command switch
        {
            CMD_DATAFRAME => "DataFrame",
            CMD_TXDELAY => "TxDelay",
            CMD_PERSISTENCE => "Persistence",
            CMD_SLOTTIME => "SlotTime",
            CMD_TXTAIL => "TxTail",
            CMD_FULLDUPLEX => "FullDuplex",
            CMD_SETHW => "SetHardware",
            CMD_ACKMODE => "AckMode",
            CMD_RETURN => "Return",
            _ => $"Unknown({command})"
        };
    }

    /// <summary>
    /// Formats a KISS parameter value for human-readable logging, per the KISS TNC protocol spec.
    /// TxDelay/SlotTime/TxTail are in 10ms units; Persistence is a raw 0-255 value
    /// where p = (P+1)/256; FullDuplex is 0=half, nonzero=full.
    /// </summary>
    public static string FormatParameterValue(byte command, int value)
    {
        return command switch
        {
            CMD_TXDELAY => $"{value} (= {value * 10}ms, spec: value × 10ms)",
            CMD_PERSISTENCE => $"{value} (= p {(value + 1) / 256.0:F3}, spec: p = (P+1)/256)",
            CMD_SLOTTIME => $"{value} (= {value * 10}ms, spec: value × 10ms)",
            CMD_TXTAIL => $"{value} (= {value * 10}ms, spec: value × 10ms)",
            CMD_FULLDUPLEX => $"{value} (= {(value != 0 ? "full duplex" : "half duplex")})",
            _ => $"{value}"
        };
    }

    /// <summary>
    /// Builds a complete KISS frame for a single-byte parameter command.
    /// </summary>
    public static byte[] BuildParameterFrame(byte command, byte value, int port = 0)
    {
        var cmdByte = BuildCommandByte(command, port);
        return value switch
        {
            FEND => [FEND, cmdByte, FESC, TFEND, FEND],
            FESC => [FEND, cmdByte, FESC, TFESC, FEND],
            _ => [FEND, cmdByte, value, FEND],
        };
    }

    /// <summary>
    /// Builds a KISS SETHW frame for NinoTNC mode setting.
    /// </summary>
    /// <param name="mode">NinoTNC mode (0-14)</param>
    /// <param name="persist">If true, persist to flash memory. If false, temporary only.</param>
    /// <param name="port">KISS port (0-15)</param>
    public static byte[] BuildSetHwFrame(int mode, bool persist, int port = 0)
    {
        // NinoTNC: add 16 to mode to prevent flash write
        byte modeValue = (byte)(mode + (persist ? 0 : 16));
        return BuildParameterFrame(CMD_SETHW, modeValue, port);
    }

    /// <summary>
    /// Checks if a command should be filtered based on config settings.
    /// </summary>
    public static bool ShouldFilter(byte command, Config config)
    {
        return command switch
        {
            CMD_TXDELAY => config.FilterTxDelay,
            CMD_PERSISTENCE => config.FilterPersistence,
            CMD_SLOTTIME => config.FilterSlotTime,
            CMD_TXTAIL => config.FilterTxTail,
            CMD_FULLDUPLEX => config.FilterFullDuplex,
            CMD_SETHW => config.FilterSetHardware,
            _ => false  // DataFrame and unknown commands pass through
        };
    }

    /// <summary>
    /// Extracts the command byte from a complete KISS frame.
    /// Returns null if frame is invalid.
    /// </summary>
    public static byte? GetCommandByteFromFrame(byte[] frame)
    {
        if (frame == null || frame.Length < 3)
            return null;

        // Frame format: [FEND] [cmd] [...payload...] [FEND]
        int startIdx = 0;
        if (frame[0] == FEND)
            startIdx = 1;

        if (startIdx >= frame.Length)
            return null;

        return frame[startIdx];
    }

    /// <summary>
    /// Extracts ACKMODE frame details: sequence bytes and payload length.
    /// Returns null if frame is not a valid ACKMODE frame.
    /// </summary>
    public static (byte seqHi, byte seqLo, int payloadBytes)? ParseAckModeFrame(byte[] frame)
    {
        if (frame == null || frame.Length < 5)
            return null;

        var cmdByte = GetCommandByteFromFrame(frame);
        if (!cmdByte.HasValue)
            return null;

        var (command, _) = ParseCommandByte(cmdByte.Value);
        if (command != CMD_ACKMODE)
            return null;

        // Frame format: [FEND] [cmd] [seqHi] [seqLo] [payload...] [FEND]
        int startIdx = frame[0] == FEND ? 1 : 0;

        // Need at least cmd + seqHi + seqLo
        if (startIdx + 3 > frame.Length)
            return null;

        byte seqHi = frame[startIdx + 1];
        byte seqLo = frame[startIdx + 2];

        // Payload is everything between seqLo and final FEND
        int payloadStart = startIdx + 3;
        int payloadEnd = frame[^1] == FEND ? frame.Length - 1 : frame.Length;
        int payloadBytes = payloadEnd - payloadStart;

        return (seqHi, seqLo, payloadBytes);
    }

    /// <summary>
    /// Extracts sequence bytes from an ACKMODE ACK frame.
    /// ACK format: [FEND] [cmd] [seqHi] [seqLo] [FEND]
    /// Returns null if frame is not a valid ACK.
    /// </summary>
    public static (byte seqHi, byte seqLo)? ParseAckModeAck(byte[] frame)
    {
        if (frame == null || frame.Length < 4)
            return null;

        var cmdByte = GetCommandByteFromFrame(frame);
        if (!cmdByte.HasValue)
            return null;

        var (command, _) = ParseCommandByte(cmdByte.Value);
        if (command != CMD_ACKMODE)
            return null;

        // ACK is exactly 5 bytes: FEND cmd seqHi seqLo FEND
        // Or 4 bytes without leading FEND: cmd seqHi seqLo FEND
        int startIdx = frame[0] == FEND ? 1 : 0;

        if (startIdx + 3 > frame.Length)
            return null;

        return (frame[startIdx + 1], frame[startIdx + 2]);
    }

    /// <summary>
    /// Builds all configured parameter frames based on config settings.
    /// Returns empty array if no parameters are configured.
    /// </summary>
    /// <param name="includeSetHardware">
    /// When false, the SETHW (NinoTNC mode) frame is omitted.
    /// Pass false for periodic timer resends when PersistNinoMode is enabled,
    /// to avoid unnecessary flash write cycles on the TNC.
    /// </param>
    public static byte[][] BuildAllParameterFrames(Config config, int port = 0, bool includeSetHardware = true)
    {
        var frames = new List<byte[]>();

        // A value is only ours to send while its toggle (the filter) is on; turning the
        // toggle off in the UI hands the parameter back to the node.
        if (config.FilterTxDelay && config.TxDelayValue.HasValue)
            frames.Add(BuildParameterFrame(CMD_TXDELAY, (byte)Math.Clamp(config.TxDelayValue.Value / 10, 0, 255), port));

        if (config.FilterPersistence && config.PersistenceValue.HasValue)
            frames.Add(BuildParameterFrame(CMD_PERSISTENCE, (byte)Math.Clamp(config.PersistenceValue.Value, 0, 255), port));

        if (config.FilterSlotTime && config.SlotTimeValue.HasValue)
            frames.Add(BuildParameterFrame(CMD_SLOTTIME, (byte)Math.Clamp(config.SlotTimeValue.Value / 10, 0, 255), port));

        if (config.FilterTxTail && config.TxTailValue.HasValue)
            frames.Add(BuildParameterFrame(CMD_TXTAIL, (byte)Math.Clamp(config.TxTailValue.Value / 10, 0, 255), port));

        if (config.FilterFullDuplex && config.FullDuplexValue.HasValue)
            frames.Add(BuildParameterFrame(CMD_FULLDUPLEX, (byte)(config.FullDuplexValue.Value ? 1 : 0), port));

        if (config.NinoMode.HasValue && includeSetHardware)
            frames.Add(BuildSetHwFrame(config.NinoMode.Value, config.PersistNinoMode, port));

        return frames.ToArray();
    }

    // --- AX.25 UI frame construction ---

    /// <summary>
    /// AX.25 control field for Unnumbered Information (UI) frames.
    /// </summary>
    public const byte AX25_CONTROL_UI = 0x03;

    /// <summary>
    /// AX.25 Protocol ID for "No layer 3 protocol".
    /// </summary>
    public const byte AX25_PID_NO_LAYER3 = 0xF0;

    /// <summary>
    /// Encodes a callsign (with optional SSID, e.g. "VK2ABC-5") into a 7-byte AX.25 address field.
    /// Characters are shifted left by 1 bit per the AX.25 specification.
    /// </summary>
    /// <param name="callsign">Callsign with optional SSID (e.g. "CQ", "VK2ABC", "VK2ABC-5")</param>
    /// <param name="lastAddress">True if this is the last address in the address field (sets the end-of-address bit)</param>
    /// <returns>7-byte encoded address field</returns>
    public static byte[] EncodeCallsign(string callsign, bool lastAddress)
    {
        if (string.IsNullOrWhiteSpace(callsign))
            throw new ArgumentException("Callsign cannot be empty", nameof(callsign));

        var parts = callsign.Split('-');
        var call = parts[0].ToUpper().Trim();

        if (call.Length == 0 || call.Length > 6)
            throw new ArgumentException($"Callsign base must be 1-6 characters, got '{call}'", nameof(callsign));

        int ssid = 0;
        if (parts.Length > 1)
        {
            if (!int.TryParse(parts[1], out ssid) || ssid < 0 || ssid > 15)
                throw new ArgumentException($"SSID must be 0-15, got '{parts[1]}'", nameof(callsign));
        }

        call = call.PadRight(6);

        var encoded = new byte[7];
        for (int i = 0; i < 6; i++)
        {
            encoded[i] = (byte)(call[i] << 1);
        }
        // SSID byte: 011SSSS0 (or 011SSSS1 for last address)
        encoded[6] = (byte)(0x60 | ((ssid & 0x0F) << 1) | (lastAddress ? 1 : 0));

        return encoded;
    }

    /// <summary>
    /// Builds an AX.25 UI (Unnumbered Information) frame with no layer 3 protocol.
    /// </summary>
    /// <param name="source">Source callsign (e.g. "VK2ABC" or "VK2ABC-5")</param>
    /// <param name="dest">Destination callsign (e.g. "CQ" or "VK2XYZ-1")</param>
    /// <param name="info">Information field payload</param>
    /// <returns>Complete AX.25 UI frame bytes (without KISS framing)</returns>
    public static byte[] BuildAx25UiFrame(string source, string dest, byte[] info)
    {
        var frame = new List<byte>();

        // Destination address (7 bytes)
        frame.AddRange(EncodeCallsign(dest, false));

        // Source address (7 bytes, with end-of-address bit)
        frame.AddRange(EncodeCallsign(source, true));

        // Control field: UI frame
        frame.Add(AX25_CONTROL_UI);

        // PID: No layer 3
        frame.Add(AX25_PID_NO_LAYER3);

        // Info field
        frame.AddRange(info);

        return frame.ToArray();
    }

    /// <summary>
    /// Wraps an AX.25 frame in KISS data frame framing, with proper byte stuffing.
    /// FEND (0xC0) in payload is escaped to FESC TFEND (0xDB 0xDC).
    /// FESC (0xDB) in payload is escaped to FESC TFESC (0xDB 0xDD).
    /// </summary>
    /// <param name="ax25Data">The AX.25 frame data to wrap</param>
    /// <param name="port">KISS port number (0-15)</param>
    /// <returns>Complete KISS frame with delimiters and escaping</returns>
    public static byte[] BuildKissDataFrame(byte[] ax25Data, int port = 0)
    {
        var frame = new List<byte> { FEND, BuildCommandByte(CMD_DATAFRAME, port) };

        // Apply KISS byte stuffing to the payload
        foreach (var b in ax25Data)
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
