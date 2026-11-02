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
/// Reads a radio's RSSI ten times a second, follows its carrier-sense edges as transmission
/// windows, and attributes RSSI and SNR to each received frame by matching its arrival time to the
/// window that carried it. Readings taken while the channel is idle track the noise floor. Ported
/// from packet.net's RssiTaggingTransport (Packet.Ax25.Radio), which polls at 40 ms busy and
/// 400 ms idle; that missed most short frames at 9600 baud.
/// </summary>
public sealed class RadioSignalMonitor : IAsyncDisposable
{
    // After a failed read, so a radio that has gone away isn't polled flat out
    private static readonly TimeSpan FailureBackoff = TimeSpan.FromMilliseconds(200);

    // One read every this often. Back-to-back reads (about 60 a second on a TM8110, 25 on
    // GB7RDG's 70cm radio) drove the 70cm radio into a state where every read took about 250 ms,
    // after about 25 minutes each time, until it was power cycled; at ten a second it stayed
    // healthy, and with the look-ahead and midpoint stamps more 70cm frames got an SNR than at
    // full rate (75%, against 71%). It also costs the Pi far less.
    private static readonly TimeSpan ReadPeriod = TimeSpan.FromMilliseconds(100);

    // Samples kept for attribution; comfortably longer than any frame plus TXDELAY
    private static readonly TimeSpan SampleHorizon = TimeSpan.FromSeconds(10);

    // How long after a frame's delivery its carrier and readings may still turn up. A fast modem
    // can hand over a short frame before a slower radio reports its carrier: on GB7RDG's 70cm
    // Tait at 9600 baud, carrier rises were reported up to 52 ms and raised readings landed up
    // to 45 ms after the NinoTNC delivered the frame. Callers attribute at least this long after
    // delivery (LinkMonitor waits 250 ms) so those readings exist.
    // This is the default, used until the radio's own lag has been measured (see below)
    public static readonly TimeSpan LookAhead = TimeSpan.FromMilliseconds(100);

    // Until measured, modes at least this fast get the default look-ahead (GB7RDG: 70cm is
    // 9600, 2m 3600)
    private const int FastModeBitRate = 4800;

    // Each radio's lag is measured as it runs: for frames with just one carrier window anywhere
    // near them, how long after the frame started on air (delivery less airtime) the radio
    // reported the carrier rising. The look-ahead
    // is then the 90th percentile of the last MaxLagSamples, plus a margin, for every mode; a
    // radio that reports before delivery gets none. GB7RDG's TM8110 reports 18-36 ms before
    // delivery, its 70cm radio up to 52 ms after.
    private const int MaxLagSamples = 50;
    private const int MinLagSamples = 10;
    private static readonly TimeSpan LagMargin = TimeSpan.FromMilliseconds(30);
    public static readonly TimeSpan MaxLookAhead = TimeSpan.FromMilliseconds(200);
    private readonly Queue<double> riseLagsMs = new();

    // A running average of how long a read takes. Readings are stamped at the middle of their
    // query (the radio measured somewhere in it, not when its answer arrived), and counted for a
    // carrier window if they're within half a read of it: on GB7RDG's 70cm radio a read takes
    // about 39 ms, as long as a short frame's whole carrier.
    private double typicalReadMs;

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
    private readonly Thread? sampler;
    private readonly Action<string>? log;
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
    /// <param name="log">Gets a line a minute with the read rate and timings</param>
    public RadioSignalMonitor(IRadioControl radio, string modem, Action<NoiseSample>? onNoiseSample = null,
        Func<DateTime>? clock = null, bool startSampling = true, Action<string>? log = null)
    {
        this.radio = radio;
        this.log = log;
        this.modem = modem;
        this.onNoiseSample = onNoiseSample;
        this.clock = clock ?? (() => DateTime.UtcNow);

        if (radio.Capabilities.HasFlag(RadioCapabilities.CarrierSense))
            radio.CarrierSenseChanged += OnCarrierSenseChanged;

        if (startSampling)
        {
            sampler = new Thread(() => SampleLoop(cts.Token)) { IsBackground = true, Name = $"rssi:{modem}" };
            sampler.Start();
        }
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

            if (airtime is { } air)
                LearnLag(receivedAt, air);

            // With the radio's lag measured, its own look-ahead for every mode; before that, the
            // default for fast modes only. No look-ahead, or no known airtime: packet.net's rule.
            var lookAhead = LearnedLookAhead() ?? (bitRateHz >= FastModeBitRate ? LookAhead : TimeSpan.Zero);
            bool ahead = lookAhead > TimeSpan.Zero && airtime != null;
            var latest = ahead ? receivedAt + lookAhead : receivedAt;
            var found = ahead ? FindWindowFast(receivedAt, airtime!.Value, lookAhead) : FindWindow(receivedAt, airtime);
            var halfRead = TimeSpan.FromMilliseconds(typicalReadMs / 2);
            if (radio.Capabilities.HasFlag(RadioCapabilities.CarrierSense) && found is { } window)
            {
                var to = window.FallAt is { } fall && fall < latest ? fall : latest;
                var stats = Statistics(samples.Where(s => s.At >= window.RiseAt - halfRead && s.At <= to + halfRead).Select(s => s.Dbm));
                if (stats is { } st)
                    return new RadioMetadata(st.Median, floor is { } f ? st.Median - f : null, floor, st.Count);
                // The carrier was too short to sample; the floor is still worth keeping
                return floor != null ? new RadioMetadata(null, null, floor, 0) : null;
            }

            // No carrier sense, or the window was missed: samples above the floor count as signal
            var from = receivedAt - AttributionLookback;
            var signal = samples
                .Where(s => s.At >= from && s.At <= latest + halfRead && (floor == null || s.Dbm >= floor + SignalThresholdDb))
                .Select(s => s.Dbm);
            return Statistics(signal) is { } t
                ? new RadioMetadata(t.Median, floor is { } f2 ? t.Median - f2 : null, floor, t.Count)
                : null;
        }
    }

    /// <summary>Notes how long a read took (the sampler's job; public for tests).</summary>
    public void NoteReadTime(TimeSpan took)
    {
        lock (gate)
            typicalReadMs = typicalReadMs == 0 ? took.TotalMilliseconds : typicalReadMs + 0.05 * (took.TotalMilliseconds - typicalReadMs);
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

    // With a look-ahead: the frame belongs to the window whose carrier carried it. A window is
    // plausible if it rose no later than the look-ahead after the frame started on air (its delivery
    // less its airtime: a radio can report carrier late, but a carrier that rose well after the
    // frame was already on air is someone else's), and fell no more than the slack before
    // delivery. Of those, the one nearest the delivery wins: a previous station's carrier ended
    // earlier, a late-reported one of the frame's own is close.
    private CarrierWindow? FindWindowFast(DateTime receivedAt, TimeSpan airtime, TimeSpan lookAhead)
    {
        var latestRise = receivedAt - airtime + lookAhead;

        CarrierWindow? best = null;
        var bestDistance = TimeSpan.MaxValue;
        foreach (var w in windows.Concat(currentWindow is { } c ? [c] : Array.Empty<CarrierWindow>()))
        {
            if (w.RiseAt > latestRise || w.FallAt is { } f && f + WindowAttributionSlack < receivedAt)
                continue;

            // Zero when the delivery falls inside the window
            var distance = w.RiseAt > receivedAt ? w.RiseAt - receivedAt
                : w.FallAt is { } fall && fall < receivedAt ? receivedAt - fall
                : TimeSpan.Zero;
            if (distance < bestDistance)
            {
                best = w;
                bestDistance = distance;
            }
        }
        return best;
    }

    // Without a look-ahead (packet.net's rule): the frame belongs to the window whose
    // carrier carried it. Delivery trails carrier-fall by the modem's decode and serial
    // latency, so closed windows stay eligible for a slack, and the next station may key inside
    // it: a window that opened after the frame was already on air can't claim it.
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

    // Learns from frames with a single carrier window anywhere near them: from half a second
    // before the frame started on air to MaxLookAhead after delivery
    private void LearnLag(DateTime receivedAt, TimeSpan airtime)
    {
        var spanStart = receivedAt - airtime - TimeSpan.FromMilliseconds(500);
        var spanEnd = receivedAt + MaxLookAhead;
        var near = windows.Concat(currentWindow is { } c ? [c] : Array.Empty<CarrierWindow>())
            .Where(w => w.RiseAt <= spanEnd && (w.FallAt ?? DateTime.MaxValue) >= spanStart)
            .Take(2)
            .ToList();
        if (near.Count != 1)
            return;

        // Measured from the frame's start on air (delivery less airtime), the same reference
        // FindWindowFast applies the look-ahead to
        riseLagsMs.Enqueue((near[0].RiseAt - (receivedAt - airtime)).TotalMilliseconds);
        while (riseLagsMs.Count > MaxLagSamples)
            riseLagsMs.Dequeue();
    }

    // Null until enough frames have been measured
    private TimeSpan? LearnedLookAhead()
    {
        if (riseLagsMs.Count < MinLagSamples)
            return null;
        var sorted = riseLagsMs.OrderBy(x => x).ToList();
        var p90 = sorted[(int)Math.Round(0.9 * (sorted.Count - 1))];
        if (p90 <= 0)
            return TimeSpan.Zero;
        var ahead = TimeSpan.FromMilliseconds(p90) + LagMargin;
        return ahead < MaxLookAhead ? ahead : MaxLookAhead;
    }

    /// <summary>The measured carrier lag and the look-ahead it gives, for the log.</summary>
    public string LagSummary()
    {
        lock (gate)
        {
            if (riseLagsMs.Count == 0)
                return "carrier lag not measured yet";
            var sorted = riseLagsMs.OrderBy(x => x).ToList();
            var median = sorted[sorted.Count / 2];
            var learned = LearnedLookAhead();
            return $"carrier rise reported {median:+0;-0;0} ms from frame start (median of {sorted.Count}), "
                + (learned is { } l ? $"look-ahead {l.TotalMilliseconds:F0} ms" : $"look-ahead not yet learned (needs {MinLagSamples})");
        }
    }

    // One thread per radio doing read, record, read, record: the radio's serial link is the
    // pace-setter, so a plain blocking loop is all this needs
    private void SampleLoop(CancellationToken cancellationToken)
    {
        var read = new System.Diagnostics.Stopwatch();
        var stats = new ReadStats(clock());
        while (!cancellationToken.IsCancellationRequested)
        {
            read.Restart();
            var asked = clock();
            try
            {
                var dbm = radio.ReadRssiDbmAsync(cancellationToken).AsTask().GetAwaiter().GetResult();
                var took = read.Elapsed;
                // Stamped at the middle of the query: the radio measured somewhere in it
                Record(asked + took / 2, dbm);
                NoteReadTime(took);
                Volatile.Write(ref consecutiveFailures, 0);
                stats.Read(took);
                if (read.Elapsed < ReadPeriod)
                    cancellationToken.WaitHandle.WaitOne(ReadPeriod - read.Elapsed);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                Interlocked.Increment(ref consecutiveFailures);
                Interlocked.Increment(ref totalFailures);
                stats.Failed();
                cancellationToken.WaitHandle.WaitOne(FailureBackoff);
            }

            var now = clock();
            if (now - stats.Since >= TimeSpan.FromMinutes(1))
            {
                try { log?.Invoke($"{stats.Summary(now, NoiseFloorDbm)}; {LagSummary()}"); }
                catch { } // logging must never stop the sampling
                stats = new ReadStats(now);
            }
        }
    }

    private sealed class ReadStats(DateTime since)
    {
        public DateTime Since { get; } = since;
        private int reads, failures;
        private TimeSpan total, worst;

        public void Read(TimeSpan took)
        {
            reads++;
            total += took;
            if (took > worst)
                worst = took;
        }

        public void Failed() => failures++;

        public string Summary(DateTime now, float? floor)
        {
            double secs = Math.Max(1, (now - Since).TotalSeconds);
            var avg = reads > 0 ? total.TotalMilliseconds / reads : 0;
            return $"RSSI {reads / secs:F1} reads/s, {avg:F1} ms average, {worst.TotalMilliseconds:F0} ms worst, {failures} failed"
                + (floor is { } f ? $", noise floor {f:F1} dBm" : "");
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
        bool stopped = sampler == null || await Task.Run(() => sampler.Join(TimeSpan.FromSeconds(5)));
        // A thread still stuck in a read keeps using the token; disposing it under the thread
        // would throw there and end the process, so leave it to the GC in that case
        if (stopped)
            cts.Dispose();
    }

    private sealed class CarrierWindow(DateTime riseAt)
    {
        public DateTime RiseAt { get; } = riseAt;
        public DateTime? FallAt { get; set; }
    }
}
