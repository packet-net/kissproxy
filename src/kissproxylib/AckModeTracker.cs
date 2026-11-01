using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace kissproxylib;

/// <summary>
/// Timing information for an ACKMODE transmission.
/// Published to MQTT when an ACK is received.
/// </summary>
public record AckModeTiming
{
    /// <summary>Sequence high byte from the ACKMODE frame.</summary>
    [JsonPropertyName("seqHi")]
    public byte SeqHi { get; init; }

    /// <summary>Sequence low byte from the ACKMODE frame.</summary>
    [JsonPropertyName("seqLo")]
    public byte SeqLo { get; init; }

    /// <summary>Combined 16-bit sequence number.</summary>
    [JsonPropertyName("seqNumber")]
    public int SeqNumber => (SeqHi << 8) | SeqLo;

    /// <summary>Size of the AX.25 payload in bytes (not including KISS framing).</summary>
    [JsonPropertyName("payloadBytes")]
    public int PayloadBytes { get; init; }

    /// <summary>NinoTNC mode number (0-14), or null if unknown.</summary>
    [JsonPropertyName("mode")]
    public int? Mode { get; init; }

    /// <summary>Human-readable mode name.</summary>
    [JsonPropertyName("modeName")]
    public string? ModeName { get; init; }

    /// <summary>Bit rate in Hz for the current mode.</summary>
    [JsonPropertyName("bitRate")]
    public int? BitRate { get; init; }

    /// <summary>TXDELAY value in 10ms units as sent to TNC.</summary>
    [JsonPropertyName("txDelayUnits")]
    public int? TxDelayUnits { get; init; }

    /// <summary>TXDELAY in milliseconds.</summary>
    [JsonPropertyName("txDelayMs")]
    public double? TxDelayMs { get; init; }

    /// <summary>Calculated transmission duration in milliseconds.</summary>
    [JsonPropertyName("txDurationMs")]
    public double? TxDurationMs { get; init; }

    /// <summary>When the frame was queued (sent to TNC).</summary>
    [JsonPropertyName("queuedUtc")]
    public DateTime QueuedUtc { get; init; }

    /// <summary>Calculated transmission start time (after TXDELAY).</summary>
    [JsonPropertyName("txStartUtc")]
    public DateTime? TxStartUtc { get; init; }

    /// <summary>When transmission completed (ACK received).</summary>
    [JsonPropertyName("txEndUtc")]
    public DateTime TxEndUtc { get; init; }

    /// <summary>Total time from queue to ACK in milliseconds.</summary>
    [JsonPropertyName("totalMs")]
    public double TotalMs { get; init; }

    /// <summary>
    /// Serialize to JSON for MQTT publishing.
    /// </summary>
    public string ToJson()
    {
        return JsonSerializer.Serialize(this, new JsonSerializerOptions
        {
            WriteIndented = false
        });
    }
}

/// <summary>
/// Tracks pending ACKMODE frames and calculates timing when ACKs are received.
/// Thread-safe for use across proxy threads.
/// </summary>
public class AckModeTracker
{
    private readonly ConcurrentDictionary<(byte seqHi, byte seqLo), PendingAckModeFrame> _pending = new();

    /// <summary>
    /// Maximum age for pending frames before they're cleaned up.
    /// </summary>
    public TimeSpan MaxPendingAge { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Current NinoTNC mode. Set this when mode changes.
    /// </summary>
    public int? CurrentMode { get; set; }

    /// <summary>
    /// Current TXDELAY value in 10ms units. Set this when TXDELAY changes.
    /// </summary>
    public int? CurrentTxDelay { get; set; }

    /// <summary>
    /// Records an outbound ACKMODE frame being sent to the modem.
    /// </summary>
    /// <param name="frame">The complete KISS frame</param>
    /// <returns>True if frame was tracked, false if not a valid ACKMODE frame</returns>
    public bool TrackOutbound(byte[] frame)
    {
        var parsed = KissFrameBuilder.ParseAckModeFrame(frame);
        if (!parsed.HasValue)
            return false;

        var (seqHi, seqLo, payloadBytes) = parsed.Value;
        var key = (seqHi, seqLo);

        var pending = new PendingAckModeFrame
        {
            SeqHi = seqHi,
            SeqLo = seqLo,
            PayloadBytes = payloadBytes,
            QueuedUtc = DateTime.UtcNow,
            Mode = CurrentMode,
            TxDelayUnits = CurrentTxDelay
        };

        _pending[key] = pending;

        // Cleanup old entries periodically
        CleanupOldEntries();

        return true;
    }

    /// <summary>
    /// Processes an inbound ACK and calculates timing information.
    /// </summary>
    /// <param name="frame">The ACK frame from the modem</param>
    /// <returns>Timing information if matched, null otherwise</returns>
    public AckModeTiming? ProcessAck(byte[] frame)
    {
        var parsed = KissFrameBuilder.ParseAckModeAck(frame);
        if (!parsed.HasValue)
            return null;

        var (seqHi, seqLo) = parsed.Value;
        var key = (seqHi, seqLo);

        if (!_pending.TryRemove(key, out var pending))
            return null;

        var ackTime = DateTime.UtcNow;
        var totalMs = (ackTime - pending.QueuedUtc).TotalMilliseconds;

        // Get mode info for timing calculations
        NinoModeInfo? modeInfo = null;
        if (pending.Mode.HasValue)
        {
            modeInfo = KissFrameBuilder.GetModeInfo(pending.Mode.Value);
        }

        // Calculate transmission duration
        double? txDurationMs = null;
        if (modeInfo != null)
        {
            txDurationMs = modeInfo.CalculateTransmissionMs(pending.PayloadBytes);
        }

        // Calculate TXDELAY in milliseconds
        double? txDelayMs = null;
        if (pending.TxDelayUnits.HasValue)
        {
            txDelayMs = pending.TxDelayUnits.Value * 10.0;
        }

        // Calculate transmission start time
        // tx_end = ack_time
        // tx_start = tx_end - tx_duration
        DateTime? txStartUtc = null;
        if (txDurationMs.HasValue)
        {
            txStartUtc = ackTime.AddMilliseconds(-txDurationMs.Value);
        }

        return new AckModeTiming
        {
            SeqHi = seqHi,
            SeqLo = seqLo,
            PayloadBytes = pending.PayloadBytes,
            Mode = pending.Mode,
            ModeName = modeInfo?.Name,
            BitRate = modeInfo?.BitRateHz,
            TxDelayUnits = pending.TxDelayUnits,
            TxDelayMs = txDelayMs,
            TxDurationMs = txDurationMs,
            QueuedUtc = pending.QueuedUtc,
            TxStartUtc = txStartUtc,
            TxEndUtc = ackTime,
            TotalMs = totalMs
        };
    }

    /// <summary>
    /// Updates the current mode. Call this when SETHW command is sent.
    /// </summary>
    public void SetMode(int mode)
    {
        CurrentMode = mode;
    }

    /// <summary>
    /// Updates the current TXDELAY. Call this when TXDELAY command is sent.
    /// </summary>
    public void SetTxDelay(int txDelayUnits)
    {
        CurrentTxDelay = txDelayUnits;
    }

    private void CleanupOldEntries()
    {
        var cutoff = DateTime.UtcNow - MaxPendingAge;
        var toRemove = _pending
            .Where(kv => kv.Value.QueuedUtc < cutoff)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var key in toRemove)
        {
            _pending.TryRemove(key, out _);
        }
    }

    private class PendingAckModeFrame
    {
        public byte SeqHi { get; init; }
        public byte SeqLo { get; init; }
        public int PayloadBytes { get; init; }
        public DateTime QueuedUtc { get; init; }
        public int? Mode { get; init; }
        public int? TxDelayUnits { get; init; }
    }
}
