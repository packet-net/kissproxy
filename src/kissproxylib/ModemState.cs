using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace kissproxylib;

/// <summary>
/// Decoded NinoTNC TX Test frame status information.
/// This frame is sent by the TNC when the TX Test button is pressed.
/// </summary>
public class NinoTncStatus
{
    public DateTime Timestamp { get; set; }
    public string? FirmwareVersion { get; set; }
    public string? SerialNumber { get; set; }
    public long? UptimeMs { get; set; }
    public string? UptimeFormatted { get; set; }
    public int? BoardSwitchMode { get; set; }
    public string? BoardSwitchModeBinary { get; set; }
    public int? CurrentMode { get; set; }
    public string? CurrentModeName { get; set; }
    public long? Ax25RxPackets { get; set; }
    public long? Il2pRxPackets { get; set; }
    public long? Il2pRxUncorrectable { get; set; }
    public long? TxPacketCount { get; set; }
    public long? PreambleCount { get; set; }
    public long? LoopCycles { get; set; }
    public long? LostAdcSamples { get; set; }

    /// <summary>
    /// Attempts to parse a NinoTNC TX Test frame from raw KISS frame bytes.
    /// Returns null if the frame is not a TX Test frame.
    /// </summary>
    public static NinoTncStatus? TryParse(byte[] frame)
    {
        // TX Test frames are data frames (command 0) with a specific format
        // They contain "=FirmwareVr:" as a marker
        if (frame.Length < 50)
            return null;

        // Find the payload start (after FEND and command byte)
        int payloadStart = frame[0] == 0xC0 ? 2 : 1;
        if (payloadStart >= frame.Length)
            return null;

        // Skip the AX.25 header (typically 16 bytes: 7 dest + 7 src + 2 control/PID)
        // Look for the "=FirmwareVr:" marker
        int markerIndex = frame.AsSpan(payloadStart).IndexOf("=FirmwareVr:"u8);
        int markerStart = markerIndex < 0 ? -1 : payloadStart + markerIndex;

        if (markerStart < 0)
            return null;

        // Extract the payload as ASCII (from marker to end, excluding trailing FEND)
        int payloadEnd = frame.Length - 1;
        if (frame[payloadEnd] == 0xC0)
            payloadEnd--;

        var payloadBytes = new byte[payloadEnd - markerStart + 1];
        Array.Copy(frame, markerStart, payloadBytes, 0, payloadBytes.Length);
        var payload = Encoding.ASCII.GetString(payloadBytes);

        // Parse the key=value pairs (format: =Key:Value)
        var status = new NinoTncStatus { Timestamp = DateTime.UtcNow };
        var pairs = payload.Split('=', StringSplitOptions.RemoveEmptyEntries);

        foreach (var pair in pairs)
        {
            var colonIdx = pair.IndexOf(':');
            if (colonIdx < 0)
                continue;

            var key = pair.Substring(0, colonIdx);
            var value = pair.Substring(colonIdx + 1);

            switch (key)
            {
                case "FirmwareVr":
                    status.FirmwareVersion = value;
                    break;
                case "SerialNmbr":
                    // Serial number may contain nulls, clean them up
                    status.SerialNumber = value.Replace("\0", "").Trim();
                    if (string.IsNullOrEmpty(status.SerialNumber))
                        status.SerialNumber = "(not set)";
                    break;
                case "UptimeMilS":
                    if (TryParseHex(value, out var uptimeMs))
                    {
                        status.UptimeMs = uptimeMs;
                        status.UptimeFormatted = FormatUptime(uptimeMs);
                    }
                    break;
                case "BrdSwchMod":
                    // Format: XXYYZZZZ where XX=board revision, YY=DIP switch setting, ZZZZ=operating mode value
                    // Example: 040F0023 = board rev 04, switches 1111, operating mode 0x23
                    if (value.Length >= 4 && TryParseHex(value.Substring(2, 2), out var switchPos))
                    {
                        status.BoardSwitchModeBinary = Convert.ToString((int)switchPos & 0x0F, 2).PadLeft(4, '0');

                        if (value.Length >= 8 && TryParseHex(value.Substring(4, 4), out var opMode))
                        {
                            status.BoardSwitchMode = (int)opMode;

                            // Decode the actual operating mode from the firmware mode value
                            if (KissFrameBuilder.NinoFirmwareModes.TryGetValue((int)opMode, out var modeName))
                            {
                                status.CurrentModeName = modeName;
                                // Find the corresponding switch position for this mode
                                foreach (var kv in KissFrameBuilder.NinoModes)
                                {
                                    if (kv.Value == modeName)
                                    {
                                        status.CurrentMode = kv.Key;
                                        break;
                                    }
                                }
                            }
                            else
                            {
                                // Unknown firmware mode value, fall back to switch position
                                status.CurrentMode = (int)switchPos;
                                status.CurrentModeName = KissFrameBuilder.NinoModes.TryGetValue((int)switchPos, out var name) ? name : $"Mode {switchPos}";
                            }
                        }
                        else
                        {
                            // No operating mode value, use switch position
                            status.CurrentMode = (int)switchPos;
                            status.CurrentModeName = KissFrameBuilder.NinoModes.TryGetValue((int)switchPos, out var name) ? name : $"Mode {switchPos}";
                        }
                    }
                    break;
                case "AX25RxPkts":
                    if (TryParseHex(value, out var ax25Rx))
                        status.Ax25RxPackets = ax25Rx;
                    break;
                case "IL2PRxPkts":
                    if (TryParseHex(value, out var il2pRx))
                        status.Il2pRxPackets = il2pRx;
                    break;
                case "IL2PRxUnCr":
                    if (TryParseHex(value, out var il2pUnCr))
                        status.Il2pRxUncorrectable = il2pUnCr;
                    break;
                case "TxPktCount":
                    if (TryParseHex(value, out var txPkt))
                        status.TxPacketCount = txPkt;
                    break;
                case "PreamblCnt":
                    if (TryParseHex(value, out var preamble))
                        status.PreambleCount = preamble;
                    break;
                case "LoopCycles":
                    if (TryParseHex(value, out var loops))
                        status.LoopCycles = loops;
                    break;
                case "LostADCSmp":
                    if (TryParseHex(value, out var lostAdc))
                        status.LostAdcSamples = lostAdc;
                    break;
            }
        }

        return status;
    }

    private static bool TryParseHex(string hex, out long value)
    {
        value = 0;
        if (string.IsNullOrEmpty(hex))
            return false;
        try
        {
            value = Convert.ToInt64(hex, 16);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string FormatUptime(long milliseconds)
    {
        var ts = TimeSpan.FromMilliseconds(milliseconds);
        if (ts.TotalDays >= 1)
            return $"{(int)ts.TotalDays}d {ts.Hours}h {ts.Minutes}m";
        if (ts.TotalHours >= 1)
            return $"{(int)ts.TotalHours}h {ts.Minutes}m {ts.Seconds}s";
        if (ts.TotalMinutes >= 1)
            return $"{(int)ts.TotalMinutes}m {ts.Seconds}s";
        return $"{ts.Seconds}.{ts.Milliseconds:D3}s";
    }
}

/// <summary>
/// Information about the last KISS frame processed.
/// </summary>
public class FrameInfo
{
    public DateTime Timestamp { get; set; }
    public byte CommandCode { get; set; }
    public string CommandName { get; set; } = "";
    public int Port { get; set; }
    public int PayloadLength { get; set; }

    // For data frames: decoded AX.25 info if available
    public string? SourceCallsign { get; set; }
    public string? DestCallsign { get; set; }
    public string? DecodedDescription { get; set; }

    // For parameter frames: the value
    public int? ParameterValue { get; set; }

    // Raw frame data for display
    public string? HexDump { get; set; }
    public string? AsciiDump { get; set; }

    /// <summary>
    /// Creates hex and ASCII dumps from raw frame bytes.
    /// </summary>
    public static (string hex, string ascii) CreateDumps(byte[] frame, int maxBytes = 256)
    {
        var bytesToShow = Math.Min(frame.Length, maxBytes);
        var hex = BitConverter.ToString(frame, 0, bytesToShow).Replace("-", " ");
        if (frame.Length > maxBytes)
            hex += " ...";

        // ASCII dump: show printable characters, replace others with '.'
        var ascii = new char[bytesToShow];
        for (int i = 0; i < bytesToShow; i++)
        {
            var b = frame[i];
            ascii[i] = (b >= 0x20 && b < 0x7F) ? (char)b : '.';
        }
        var asciiStr = new string(ascii);
        if (frame.Length > maxBytes)
            asciiStr += "...";

        return (hex, asciiStr);
    }
}

/// <summary>
/// Tracks the most recent value sent by the node for a KISS parameter command,
/// and whether it was passed through to the modem or filtered.
/// </summary>
public record KissParamStatus(int Value, bool Filtered);

internal readonly record struct FrameSummary(byte CommandCode, int? ParameterValue)
{
    public bool IsDataFrame => CommandCode == KissFrameBuilder.CMD_DATAFRAME;
}

/// <summary>
/// Runtime state for a single modem, including live statistics.
/// </summary>
public class ModemState
{
    public string Id { get; set; } = "";
    public bool NodeConnected { get; set; }
    public bool SerialOpen { get; set; }
    internal bool CaptureDetailedFrameInfo { get; set; }

    // Live timing
    public DateTime? LastFrameToModem { get; set; }
    public DateTime? LastFrameFromModem { get; set; }
    public DateTime? LastDataFrameToModem { get; set; }
    public DateTime? LastDataFrameFromModem { get; set; }

    // Statistics
    public long FramesToModem { get; set; }
    public long FramesFromModem { get; set; }
    public long DataFramesToModem { get; set; }
    public long DataFramesFromModem { get; set; }
    public long FramesFiltered { get; set; }
    public long BytesToModem { get; set; }
    public long BytesFromModem { get; set; }

    // Last frame metadata
    public FrameInfo? LastFrameToModemInfo { get; set; }
    public FrameInfo? LastFrameFromModemInfo { get; set; }

    // Current parameter values (last sent/received)
    public int? CurrentTxDelay { get; set; }
    public int? CurrentPersistence { get; set; }
    public int? CurrentSlotTime { get; set; }
    public int? CurrentTxTail { get; set; }
    public bool? CurrentFullDuplex { get; set; }
    public int? CurrentNinoMode { get; set; }

    // Last parameter command from node (value + whether it was passed or filtered)
    public KissParamStatus? LastNodeTxDelay { get; set; }
    public KissParamStatus? LastNodePersistence { get; set; }
    public KissParamStatus? LastNodeSlotTime { get; set; }
    public KissParamStatus? LastNodeTxTail { get; set; }
    public KissParamStatus? LastNodeSetHw { get; set; }

    // NinoTNC status from TX Test frame
    public NinoTncStatus? NinoTncStatus { get; set; }

    private readonly object lockObj = new();

    /// <summary>
    /// Event raised when this modem's state changes.
    /// </summary>
    internal Action? OnStateChanged;

    /// <summary>
    /// Records a frame sent to the modem.
    /// </summary>
    public void RecordFrameToModem(byte[] frame, FrameInfo? info = null)
        => RecordFrameToModem(frame, ToSummary(info), info);

    internal void RecordFrameToModem(byte[] frame, FrameSummary? summary, FrameInfo? info)
    {
        lock (lockObj)
        {
            FramesToModem++;
            BytesToModem += frame.Length;
            LastFrameToModem = DateTime.UtcNow;

            if (summary.HasValue)
            {
                if (summary.Value.IsDataFrame)
                {
                    DataFramesToModem++;
                    LastDataFrameToModem = DateTime.UtcNow;
                }

                UpdateCurrentParameters(summary.Value);
                RecordNodeParamCommand(summary.Value, filtered: false);
            }

            if (info != null)
            {
                LastFrameToModemInfo = info;
            }
            else
            {
                LastFrameToModemInfo = null;
            }
        }
        OnStateChanged?.Invoke();
    }

    /// <summary>
    /// Records a frame received from the modem.
    /// </summary>
    public void RecordFrameFromModem(byte[] frame, FrameInfo? info = null)
        => RecordFrameFromModem(frame, ToSummary(info), info);

    private static FrameSummary? ToSummary(FrameInfo? info) =>
        info is null ? null : new FrameSummary(info.CommandCode, info.ParameterValue);

    internal void RecordFrameFromModem(byte[] frame, FrameSummary? summary, FrameInfo? info)
    {
        lock (lockObj)
        {
            FramesFromModem++;
            BytesFromModem += frame.Length;
            LastFrameFromModem = DateTime.UtcNow;

            if (summary.HasValue)
            {
                if (summary.Value.IsDataFrame)
                {
                    DataFramesFromModem++;
                    LastDataFrameFromModem = DateTime.UtcNow;

                    // Always parsed (a quick marker search), so a TX Test pressed while no
                    // browser is watching still shows up when one opens the page
                    var tncStatus = NinoTncStatus.TryParse(frame);
                    if (tncStatus != null)
                    {
                        NinoTncStatus = tncStatus;
                    }
                }

                UpdateCurrentParameters(summary.Value);
            }

            if (info != null)
            {
                LastFrameFromModemInfo = info;
            }
            else
            {
                LastFrameFromModemInfo = null;
            }
        }
        OnStateChanged?.Invoke();
    }

    /// <summary>
    /// Records a filtered frame (not forwarded to modem).
    /// </summary>
    public void RecordFilteredFrame()
    {
        lock (lockObj)
        {
            FramesFiltered++;
        }
        OnStateChanged?.Invoke();
    }

    /// <summary>
    /// Sets the connection state and notifies listeners.
    /// </summary>
    public void SetConnectionState(bool? nodeConnected = null, bool? serialOpen = null)
    {
        lock (lockObj)
        {
            if (nodeConnected.HasValue)
                NodeConnected = nodeConnected.Value;
            if (serialOpen.HasValue)
                SerialOpen = serialOpen.Value;
        }
        OnStateChanged?.Invoke();
    }

    /// <summary>
    /// Records bytes transferred (for granular byte counting).
    /// </summary>
    public void RecordBytesToModem(int count)
    {
        lock (lockObj)
        {
            BytesToModem += count;
        }
    }

    /// <summary>
    /// Records bytes received from modem.
    /// </summary>
    public void RecordBytesFromModem(int count)
    {
        lock (lockObj)
        {
            BytesFromModem += count;
        }
    }

    /// <summary>
    /// Records a parameter command from the node, whether it was passed through or filtered.
    /// Call this for every inbound parameter frame from the node.
    /// </summary>
    public void RecordNodeParamCommand(FrameInfo info, bool filtered)
    {
        RecordNodeParamCommand(new FrameSummary(info.CommandCode, info.ParameterValue), filtered);
    }

    internal void RecordNodeParamCommand(FrameSummary summary, bool filtered)
    {
        if (!summary.ParameterValue.HasValue)
            return;

        var status = new KissParamStatus(summary.ParameterValue.Value, filtered);

        lock (lockObj)
        {
            switch (summary.CommandCode)
            {
                case KissFrameBuilder.CMD_TXDELAY:     LastNodeTxDelay     = status; break;
                case KissFrameBuilder.CMD_PERSISTENCE: LastNodePersistence = status; break;
                case KissFrameBuilder.CMD_SLOTTIME:    LastNodeSlotTime    = status; break;
                case KissFrameBuilder.CMD_TXTAIL:      LastNodeTxTail      = status; break;
                case KissFrameBuilder.CMD_SETHW:       LastNodeSetHw       = status; break;
            }
        }
    }

    private void UpdateCurrentParameters(FrameInfo info)
    {
        UpdateCurrentParameters(new FrameSummary(info.CommandCode, info.ParameterValue));
    }

    private void UpdateCurrentParameters(FrameSummary summary)
    {
        if (!summary.ParameterValue.HasValue)
            return;

        switch (summary.CommandCode)
        {
            case KissFrameBuilder.CMD_TXDELAY:
                CurrentTxDelay = summary.ParameterValue;
                break;
            case KissFrameBuilder.CMD_PERSISTENCE:
                CurrentPersistence = summary.ParameterValue;
                break;
            case KissFrameBuilder.CMD_SLOTTIME:
                CurrentSlotTime = summary.ParameterValue;
                break;
            case KissFrameBuilder.CMD_TXTAIL:
                CurrentTxTail = summary.ParameterValue;
                break;
            case KissFrameBuilder.CMD_FULLDUPLEX:
                CurrentFullDuplex = summary.ParameterValue != 0;
                break;
            case KissFrameBuilder.CMD_SETHW:
                CurrentNinoMode = summary.ParameterValue.Value & 0x0F;
                break;
        }
    }

    /// <summary>
    /// Resets all statistics (but not connection state).
    /// </summary>
    public void ResetStats()
    {
        lock (lockObj)
        {
            FramesToModem = 0;
            FramesFromModem = 0;
            DataFramesToModem = 0;
            DataFramesFromModem = 0;
            FramesFiltered = 0;
            BytesToModem = 0;
            BytesFromModem = 0;
            LastFrameToModem = null;
            LastFrameFromModem = null;
            LastDataFrameToModem = null;
            LastDataFrameFromModem = null;
            LastFrameToModemInfo = null;
            LastFrameFromModemInfo = null;
        }
    }

    /// <summary>
    /// Creates a snapshot of the current state (thread-safe).
    /// </summary>
    public ModemState Snapshot()
    {
        lock (lockObj)
        {
            return new ModemState
            {
                Id = Id,
                NodeConnected = NodeConnected,
                SerialOpen = SerialOpen,
                LastFrameToModem = LastFrameToModem,
                LastFrameFromModem = LastFrameFromModem,
                LastDataFrameToModem = LastDataFrameToModem,
                LastDataFrameFromModem = LastDataFrameFromModem,
                FramesToModem = FramesToModem,
                FramesFromModem = FramesFromModem,
                DataFramesToModem = DataFramesToModem,
                DataFramesFromModem = DataFramesFromModem,
                FramesFiltered = FramesFiltered,
                BytesToModem = BytesToModem,
                BytesFromModem = BytesFromModem,
                LastFrameToModemInfo = LastFrameToModemInfo,
                LastFrameFromModemInfo = LastFrameFromModemInfo,
                CurrentTxDelay = CurrentTxDelay,
                CurrentPersistence = CurrentPersistence,
                CurrentSlotTime = CurrentSlotTime,
                CurrentTxTail = CurrentTxTail,
                CurrentFullDuplex = CurrentFullDuplex,
                CurrentNinoMode = CurrentNinoMode,
                LastNodeTxDelay = LastNodeTxDelay,
                LastNodePersistence = LastNodePersistence,
                LastNodeSlotTime = LastNodeSlotTime,
                LastNodeTxTail = LastNodeTxTail,
                LastNodeSetHw = LastNodeSetHw,
                NinoTncStatus = NinoTncStatus
            };
        }
    }
}

/// <summary>
/// Manages state for all modems.
/// </summary>
public class ModemStateManager
{
    private readonly ConcurrentDictionary<string, ModemState> states = new();
    private int detailedTrackingSubscribers;

    /// <summary>
    /// Event raised when any modem state changes. Provides the modem ID and snapshot.
    /// </summary>
    public event Action<string, ModemState>? StateChanged;

    /// <summary>
    /// Gets all modem states.
    /// </summary>
    public IReadOnlyDictionary<string, ModemState> States => states;

    /// <summary>
    /// Gets or creates a state object for a modem.
    /// </summary>
    public ModemState GetOrCreate(string id)
    {
        return states.GetOrAdd(id, key =>
        {
            var state = new ModemState
            {
                Id = key,
                CaptureDetailedFrameInfo = Volatile.Read(ref detailedTrackingSubscribers) > 0
            };
            state.OnStateChanged = () => NotifyStateChanged(id, state);
            return state;
        });
    }

    public IDisposable EnableDetailedFrameTracking()
    {
        Interlocked.Increment(ref detailedTrackingSubscribers);
        SetDetailedFrameTracking(enabled: true);
        return new DetailedFrameTrackingLease(this);
    }

    /// <summary>
    /// Notifies listeners that a modem state has changed.
    /// </summary>
    private void NotifyStateChanged(string id, ModemState state)
    {
        StateChanged?.Invoke(id, state.Snapshot());
    }

    private void DisableDetailedFrameTracking()
    {
        var remainingSubscribers = Interlocked.Decrement(ref detailedTrackingSubscribers);
        if (remainingSubscribers <= 0)
        {
            Interlocked.Exchange(ref detailedTrackingSubscribers, 0);
            SetDetailedFrameTracking(enabled: false);
        }
    }

    private void SetDetailedFrameTracking(bool enabled)
    {
        foreach (var state in states.Values)
        {
            state.CaptureDetailedFrameInfo = enabled;
        }
    }

    /// <summary>
    /// Gets a state snapshot for a modem (thread-safe).
    /// </summary>
    public ModemState? GetSnapshot(string id)
    {
        if (states.TryGetValue(id, out var state))
        {
            return state.Snapshot();
        }
        return null;
    }

    /// <summary>
    /// Gets state snapshots for all modems.
    /// </summary>
    public IEnumerable<ModemState> GetAllSnapshots()
    {
        return states.Values.Select(s => s.Snapshot());
    }

    /// <summary>
    /// Removes a modem state.
    /// </summary>
    public bool Remove(string id)
    {
        return states.TryRemove(id, out _);
    }

    private sealed class DetailedFrameTrackingLease(ModemStateManager manager) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                manager.DisableDetailedFrameTracking();
            }
        }
    }
}
