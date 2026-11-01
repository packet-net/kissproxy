using M0LTE.Rig;

namespace kissproxylib;

/// <summary>Signal readings attributed to one received frame.</summary>
/// <param name="RssiDbm">Median RSSI of the samples taken while the frame's carrier was up</param>
/// <param name="SnrDb">RssiDbm above the noise floor</param>
/// <param name="NoiseFloorDbm">The channel-idle noise floor when the frame arrived</param>
public sealed record RadioMetadata(float? RssiDbm, float? SnrDb, float? NoiseFloorDbm, int SampleCount);

/// <summary>One minute of the noise floor on a modem's channel.</summary>
/// <param name="FloorDbm">The tracked (smoothed) floor at the end of the minute</param>
/// <param name="MedianIdleDbm">Median of the minute's channel-idle samples, if any</param>
public sealed record NoiseSample(DateTime Timestamp, string Modem, float FloorDbm, float? MedianIdleDbm);

/// <summary>
/// Reads a radio's RSSI continuously (each read straight after the last: about 70 a second on
/// Tait CCDI at 28800 baud, whose round trip measured 14 ms), follows its carrier-sense edges as
/// transmission windows, and attributes RSSI and SNR to each received frame by matching its
/// arrival time to the window that carried it. Readings taken while the channel is idle track the
/// noise floor. Ported from packet.net's RssiTaggingTransport (Packet.Ax25.Radio), which polls at
/// 40 ms busy and 400 ms idle; that missed most short frames at 9600 baud (a 15-byte RR is on air
/// for well under the idle period), so this one doesn't wait between reads.
/// </summary>
public sealed class RadioSignalMonitor : IAsyncDisposable
{
    // After a failed read, so a radio that has gone away isn't polled flat out
    private static readonly TimeSpan FailureBackoff = TimeSpan.FromMilliseconds(200);

    // Reads are back to back, but never closer than this: a guard against spinning on a radio
    // that answers instantly (a real CCDI read takes about 14 ms, so this never slows it)
    private static readonly TimeSpan MinReadPeriod = TimeSpan.FromMilliseconds(5);

    // Samples kept for attribution; comfortably longer than any frame plus TXDELAY
    private static readonly TimeSpan SampleHorizon = TimeSpan.FromSeconds(10);

    // How long past carrier-fall a delivered frame can still belong to that window: the
    // modem's decode and serial delivery (measured 34-115 ms at 1200 baud)
    private static readonly TimeSpan WindowAttributionSlack = TimeSpan.FromMilliseconds(500);

    // Without carrier sense: how far back to look, and how far above the floor counts as signal
    private static readonly TimeSpan AttributionLookback = TimeSpan.FromSeconds(5);
    private const float SignalThresholdDb = 6f;

    // The noise floor is an exponential average of idle readings with this time constant, so it
    // smooths the same however fast the readings come
    private static readonly TimeSpan NoiseFloorTimeConstant = TimeSpan.FromSeconds(10);

    private readonly IRadioControl radio;
    private readonly string modem;
    private readonly Action<NoiseSample>? onNoiseSample;
    private readonly Func<DateTime> clock;
    private readonly CancellationTokenSource cts = new();
    private readonly Task? samplerLoop;
    private readonly object gate = new();
    private readonly Queue<(DateTime At, float Dbm)> samples = new();
    private readonly Queue<CarrierWindow> windows = new();
    private readonly List<float> minuteIdle = [];
    private CarrierWindow? currentWindow;
    private DateTime lastSampleAt;
    private double? noiseFloorDbm;
    private DateTime minuteStart;
    private int consecutiveFailures;
    private int totalFailures;
    private volatile bool transmitting;

    /// <param name="onNoiseSample">Called once a minute with that minute's noise floor</param>
    /// <param name="startSampling">False to drive samples by hand (tests)</param>
    public RadioSignalMonitor(IRadioControl radio, string modem, Action<NoiseSample>? onNoiseSample = null,
        Func<DateTime>? clock = null, bool startSampling = true)
    {
        this.radio = radio;
        this.modem = modem;
        this.onNoiseSample = onNoiseSample;
        this.clock = clock ?? (() => DateTime.UtcNow);

        if (radio.Capabilities.HasFlag(RadioCapabilities.CarrierSense))
            radio.CarrierSenseChanged += OnCarrierSenseChanged;

        if (startSampling)
            samplerLoop = Task.Run(() => SampleLoopAsync(cts.Token));
    }

    /// <summary>Current noise floor in dBm, or null before the first idle sample.</summary>
    public float? NoiseFloorDbm
    {
        get { lock (gate) return noiseFloorDbm is { } nf ? (float)nf : null; }
    }

    /// <summary>RSSI reads that have failed in a row; the owner reopens the radio past a limit.</summary>
    public int ConsecutiveFailures => Volatile.Read(ref consecutiveFailures);

    /// <summary>RSSI reads that have failed since the monitor started.</summary>
    public int TotalFailures => Volatile.Read(ref totalFailures);

    /// <summary>
    /// Signal readings for a frame delivered at <paramref name="receivedAt"/>, or null when no
    /// sample can be attributed to it. <paramref name="bitRateHz"/>, when known, gives the
    /// frame's airtime, which decides between two windows that could both have carried it.
    /// </summary>
    public RadioMetadata? Attribute(DateTime receivedAt, int ax25Length, int? bitRateHz)
    {
        // Frame + FCS + a flag, ignoring bit stuffing
        TimeSpan? airtime = bitRateHz > 0 ? TimeSpan.FromSeconds((ax25Length + 3) * 8.0 / bitRateHz.Value) : null;

        lock (gate)
        {
            float? floor = noiseFloorDbm is { } nf ? (float)nf : null;

            if (radio.Capabilities.HasFlag(RadioCapabilities.CarrierSense) && FindWindow(receivedAt, airtime) is { } window)
            {
                var to = window.FallAt is { } fall && fall < receivedAt ? fall : receivedAt;
                var stats = Statistics(samples.Where(s => s.At >= window.RiseAt && s.At <= to).Select(s => s.Dbm));
                if (stats is { } st)
                    return new RadioMetadata(st.Median, floor is { } f ? st.Median - f : null, floor, st.Count);
                // The carrier was too short to sample; the floor is still worth keeping
                return floor != null ? new RadioMetadata(null, null, floor, 0) : null;
            }

            // No carrier sense, or the window was missed: samples above the floor count as signal
            var from = receivedAt - AttributionLookback;
            var signal = samples
                .Where(s => s.At >= from && s.At <= receivedAt && (floor == null || s.Dbm >= floor + SignalThresholdDb))
                .Select(s => s.Dbm);
            return Statistics(signal) is { } t
                ? new RadioMetadata(t.Median, floor is { } f2 ? t.Median - f2 : null, floor, t.Count)
                : null;
        }
    }

    /// <summary>Records one RSSI reading (the sampler's job; public for tests).</summary>
    public void Record(DateTime at, float dbm)
    {
        NoiseSample? finished = null;
        lock (gate)
        {
            // After the clock steps back, older-looking samples would never be pruned
            if (samples.Count > 0 && at < lastSampleAt - TimeSpan.FromSeconds(1))
                samples.Clear();
            var previousSampleAt = lastSampleAt;
            lastSampleAt = at;

            samples.Enqueue((at, dbm));
            while (samples.Count > 0 && samples.Peek().At < at - SampleHorizon)
                samples.Dequeue();

            // Close the minute before counting this sample, which belongs to the next one
            var minute = TruncateToMinute(at);
            if (minuteStart == default)
                minuteStart = minute;
            else if (minute > minuteStart)
            {
                if (noiseFloorDbm is { } floor)
                    finished = new NoiseSample(minuteStart, modem, (float)floor, Statistics(minuteIdle)?.Median);
                minuteIdle.Clear();
                minuteStart = minute;
            }

            // While we transmit, the receiver's reading isn't the channel's noise
            bool idle = !transmitting && (radio.ChannelBusy is { } busy
                ? !busy && currentWindow == null
                : noiseFloorDbm is null || dbm < noiseFloorDbm.Value + SignalThresholdDb);
            if (idle)
            {
                if (noiseFloorDbm is { } nf)
                {
                    // Weight by the time since the previous reading of any kind, so the first idle
                    // reading after a busy spell (which may hold a signal's tail) counts no more
                    // than any other; capped for gaps such as failed reads
                    var dt = Math.Min((at - previousSampleAt).TotalSeconds, NoiseFloorTimeConstant.TotalSeconds);
                    var alpha = 1 - Math.Exp(-Math.Max(dt, 0) / NoiseFloorTimeConstant.TotalSeconds);
                    noiseFloorDbm = nf + alpha * (dbm - nf);
                }
                else
                {
                    noiseFloorDbm = dbm;
                }

                minuteIdle.Add(dbm);
            }
        }

        if (finished != null)
            onNoiseSample?.Invoke(finished);
    }

    /// <summary>Feeds a carrier-sense edge (the radio's event's job; public for tests).</summary>
    public void OnCarrierSense(bool busy, DateTime at)
    {
        lock (gate)
        {
            if (busy)
            {
                currentWindow = new CarrierWindow(at);
            }
            else if (currentWindow is { } w)
            {
                w.FallAt = at;
                windows.Enqueue(w);
                currentWindow = null;
                while (windows.Count > 8)
                    windows.Dequeue();
            }
        }
    }

    private void OnCarrierSenseChanged(object? sender, CarrierSenseChange e) => OnCarrierSense(e.Busy, e.At.UtcDateTime);

    /// <summary>Our own transmitter keyed or released (from the radio's progress reports).</summary>
    public void SetTransmitting(bool keyed) => transmitting = keyed;

    // The frame belongs to the window whose carrier carried it. Delivery trails carrier-fall by
    // the modem's latency, so closed windows stay eligible for a slack, and the next station may
    // key inside it: a window that opened after the frame was already on air can't claim it.
    private CarrierWindow? FindWindow(DateTime receivedAt, TimeSpan? airtime)
    {
        CarrierWindow? closed = null;
        foreach (var w in windows)
        {
            if (w.RiseAt <= receivedAt && receivedAt <= w.FallAt!.Value + WindowAttributionSlack)
                closed = w;
        }

        if (currentWindow is not { } open || open.RiseAt > receivedAt)
            return closed;
        if (closed is null)
            return open;

        bool fitsSinceTheOpenRise = airtime is { } air && receivedAt - air >= open.RiseAt;
        return closed.FallAt!.Value > open.RiseAt || fitsSinceTheOpenRise ? open : closed;
    }

    private async Task SampleLoopAsync(CancellationToken cancellationToken)
    {
        var period = System.Diagnostics.Stopwatch.StartNew();
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                period.Restart();
                var dbm = await radio.ReadRssiDbmAsync(cancellationToken);
                Record(clock(), dbm);
                Volatile.Write(ref consecutiveFailures, 0);
                if (period.Elapsed < MinReadPeriod)
                    await Task.Delay(MinReadPeriod - period.Elapsed, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                Interlocked.Increment(ref consecutiveFailures);
                Interlocked.Increment(ref totalFailures);
                try { await Task.Delay(FailureBackoff, cancellationToken); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    private static (float Median, int Count)? Statistics(IEnumerable<float> values)
    {
        var sorted = values.ToList();
        if (sorted.Count == 0)
            return null;
        sorted.Sort();
        return (sorted[sorted.Count / 2], sorted.Count);
    }

    private static DateTime TruncateToMinute(DateTime t) => new(t.Ticks - t.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);

    public async ValueTask DisposeAsync()
    {
        if (radio.Capabilities.HasFlag(RadioCapabilities.CarrierSense))
            radio.CarrierSenseChanged -= OnCarrierSenseChanged;
        cts.Cancel();
        if (samplerLoop != null)
        {
            try { await samplerLoop; } catch { }
        }
        cts.Dispose();
    }

    private sealed class CarrierWindow(DateTime riseAt)
    {
        public DateTime RiseAt { get; } = riseAt;
        public DateTime? FallAt { get; set; }
    }
}
