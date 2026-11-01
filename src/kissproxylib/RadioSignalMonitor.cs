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
/// Samples a radio's RSSI (often while the channel is busy, less often while idle, so the idle
/// samples track the noise floor), follows its carrier-sense edges as transmission windows, and
/// attributes RSSI and SNR to each received frame by matching its arrival time to the window that
/// carried it. Ported from packet.net's RssiTaggingTransport (Packet.Ax25.Radio), whose defaults
/// were bench-measured on Tait CCDI at 28800 baud: RSSI poll round trip 14 ms, carrier-edge
/// latency about 27 ms.
/// </summary>
public sealed class RadioSignalMonitor : IAsyncDisposable
{
    public static readonly TimeSpan BusySamplePeriod = TimeSpan.FromMilliseconds(40);
    public static readonly TimeSpan IdleSamplePeriod = TimeSpan.FromMilliseconds(400);

    // Samples kept for attribution; comfortably longer than any frame plus TXDELAY
    private static readonly TimeSpan SampleHorizon = TimeSpan.FromSeconds(10);

    // How long past carrier-fall a delivered frame can still belong to that window: the
    // modem's decode and serial delivery (measured 34-115 ms at 1200 baud)
    private static readonly TimeSpan WindowAttributionSlack = TimeSpan.FromMilliseconds(500);

    // Without carrier sense: how far back to look, and how far above the floor counts as signal
    private static readonly TimeSpan AttributionLookback = TimeSpan.FromSeconds(5);
    private const float SignalThresholdDb = 6f;

    // EMA coefficient for the idle-sample noise floor
    private const float NoiseFloorSmoothing = 0.2f;

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
    private volatile bool transmitting;

    // Released on a carrier rise so the sampler skips the rest of its idle wait: at 9600 baud a
    // whole frame can be shorter than the 400 ms idle period
    private readonly SemaphoreSlim wake = new(0, 1);

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
                noiseFloorDbm = noiseFloorDbm is { } nf ? nf + NoiseFloorSmoothing * (dbm - nf) : dbm;
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
                if (wake.CurrentCount == 0)
                    wake.Release();
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
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var dbm = await radio.ReadRssiDbmAsync(cancellationToken);
                Record(clock(), dbm);
                Volatile.Write(ref consecutiveFailures, 0);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                Interlocked.Increment(ref consecutiveFailures);
            }

            bool busy = radio.ChannelBusy != false;
            try { await wake.WaitAsync(busy ? BusySamplePeriod : IdleSamplePeriod, cancellationToken); }
            catch (OperationCanceledException) { return; }
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
        wake.Dispose();
    }

    private sealed class CarrierWindow(DateTime riseAt)
    {
        public DateTime RiseAt { get; } = riseAt;
        public DateTime? FallAt { get; set; }
    }
}
