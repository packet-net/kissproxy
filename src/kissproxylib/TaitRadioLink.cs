using M0LTE.Tait.Ccdi;
using Microsoft.Extensions.Logging;

namespace kissproxylib;

/// <summary>
/// Keeps a modem's Tait radio connected over CCDI and its RSSI sampled: opens the port, turns on
/// carrier-sense reports, runs a <see cref="RadioSignalMonitor"/>, and reopens with backoff when
/// the radio is unplugged or stops answering. Frames get no radio metadata while it is down.
/// </summary>
public sealed class TaitRadioLink : IAsyncDisposable
{
    // RSSI reads failing this many times in a row means the radio has gone. A read that gets no
    // answer costs the driver's 2 s transaction timeout, so this is about 20 s of silence.
    private const int MaxConsecutiveFailures = 10;

    // RSSI readings outside this range can't be real, so the reply that carried one is logged
    private const float PlausibleMinDbm = -140, PlausibleMaxDbm = -20;

    // Garbled and implausible replies logged in full per minute; the rest are only counted
    private const int MaxRepliesLoggedPerMinute = 10;

    // The driver reports Faulted when its keep-alive goes unanswered; give it this long to recover
    private static readonly TimeSpan FaultedLimit = TimeSpan.FromSeconds(30);

    // Progress messages (carrier-sense edges) are per radio session, and a radio that reboots
    // comes back without them, so they are turned on again after a fault and every so often
    private static readonly TimeSpan ProgressRefresh = TimeSpan.FromMinutes(5);

    // A connection must last this long before a later failure starts the backoff from scratch
    private static readonly TimeSpan StableConnection = TimeSpan.FromMinutes(1);

    private readonly string modem;
    private readonly string port;
    private readonly int baud;
    private readonly ILogger logger;
    private readonly Action<NoiseSample>? onNoiseSample;
    private readonly CancellationTokenSource cts = new();
    private readonly Task loop;
    private volatile RadioSignalMonitor? monitor;
    private volatile string status = "connecting";

    public TaitRadioLink(string modem, string port, int baud, ILogger logger, Action<NoiseSample>? onNoiseSample)
    {
        this.modem = modem;
        this.port = port;
        this.baud = baud;
        this.logger = logger;
        this.onNoiseSample = onNoiseSample;
        loop = Task.Run(() => RunAsync(cts.Token));
    }

    /// <summary>The radio's state for the web UI, e.g. "connected: Tait TM8110, noise floor -121 dBm".</summary>
    public string Status => monitor is { NoiseFloorDbm: { } nf } && status.StartsWith("connected")
        ? $"{status}, noise floor {nf:F0} dBm"
        : status;

    /// <summary>Signal readings for a received frame, or null while the radio is down.</summary>
    public RadioMetadata? Attribute(DateTime receivedAt, int ax25Length, int? bitRateHz) =>
        monitor?.Attribute(receivedAt, ax25Length, bitRateHz);

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        int failures = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            TaitCcdiRadio? radio = null;
            RadioSignalMonitor? m = null;
            var connectedAt = DateTime.UtcNow;
            try
            {
                radio = TaitCcdiRadio.Open(port, baud);
                await radio.SetProgressMessagesAsync(true, cancellationToken); // carrier-sense edges
                var identity = await radio.QueryIdentityAsync(cancellationToken);

                var counts = new MessageCounts();
                radio.MessageReceived += (_, message) =>
                {
                    counts.Message();
                    if (message is CcdiQueryResultMessage { CctmCommand: 64 } r
                        && (r.AsDecibels() is not { } dbm || dbm < PlausibleMinDbm || dbm > PlausibleMaxDbm)
                        && counts.Implausible())
                        logger.LogWarning("Tait radio on {port}: implausible RSSI reply {reply}", port, new CcdiFrame('j', $"064{r.Value}").Encode());
                };
                radio.LineRejected += (_, line) =>
                {
                    if (counts.Garbled())
                        logger.LogWarning("Tait radio on {port}: garbled reply dropped: {line}", port, Printable(line));
                };
                radio.ProgressReceived += (_, p) => counts.Progress(p.Type.ToString());
                radio.ErrorReceived += (_, _) => counts.Error();
                radio.CarrierSenseChanged += (_, _) => counts.Edge();

                m = new RadioSignalMonitor(radio, modem, onNoiseSample, log: line => logger.LogInformation("{line}; {messages}", line, counts.TakeSummary()));
                var session = new SessionState();
                var sessionMonitor = m;
                radio.ConnectionStateChanged += (_, state) => session.OnConnectionState(state);
                radio.TransmitterStateChanged += (_, e) => sessionMonitor.SetTransmitting(e.Transmitting);

                monitor = m;
                status = $"connected: {identity.ProductName}";
                logger.LogInformation("Tait radio on {port}: {product}", port, identity.ProductName);
                connectedAt = DateTime.UtcNow;
                var progressSentAt = connectedAt;
                int failuresSeen = 0;

                while (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                    var now = DateTime.UtcNow;

                    if (m.ConsecutiveFailures >= MaxConsecutiveFailures || session.FaultedSince is { } f && now - f > FaultedLimit)
                    {
                        logger.LogWarning("Tait radio on {port} stopped answering; reopening", port);
                        break;
                    }

                    bool answering = m.ConsecutiveFailures < 3 && session.FaultedSince == null;
                    status = answering ? $"connected: {identity.ProductName}" : "not answering";

                    // Reads that failed and now succeed again may mean the radio rebooted (too
                    // briefly to fault), which loses the progress setting just the same
                    bool readsRecovered = answering && m.TotalFailures != failuresSeen;
                    if (answering)
                        failuresSeen = m.TotalFailures;

                    if (answering && (session.TakeRecovered() || readsRecovered || now - progressSentAt > ProgressRefresh))
                    {
                        progressSentAt = now;
                        try { await radio.SetProgressMessagesAsync(true, cancellationToken); }
                        catch (Exception ex) when (ex is not OperationCanceledException) { }
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                if (failures == 0)
                    logger.LogWarning("Tait radio on {port}: {error}", port, ex.Message);
                status = $"not connected: {ex.Message}";
            }
            finally
            {
                monitor = null;
                if (m != null)
                    await m.DisposeAsync();
                if (radio != null)
                {
                    try { await radio.DisposeAsync(); } catch { }
                }
            }

            if (status.StartsWith("connected") || status == "not answering")
                status = "reconnecting";

            // Only a connection that lasted starts the backoff again; one that keeps failing
            // straight after opening backs off like a radio that won't open at all
            if (DateTime.UtcNow - connectedAt > StableConnection)
                failures = 0;

            // 5 s, 10 s, 20 s, 40 s, then a minute between attempts
            var delay = TimeSpan.FromSeconds(Math.Min(5 * (1 << Math.Min(failures, 4)), 60));
            failures++;
            try { await Task.Delay(delay, cancellationToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    public async ValueTask DisposeAsync()
    {
        cts.Cancel();
        try { await loop; } catch { }
        cts.Dispose();
    }

    // Control characters in a garbled line would mangle the journal
    private static string Printable(string line) =>
        string.Concat(line.Select(c => c is >= ' ' and <= '~' ? c.ToString() : $"<{(int)c:X2}>"));

    // What the radio sent in the last minute, for the log: every message (including the answers
    // to our queries), the unsolicited progress messages by type, carrier edges and unsolicited
    // errors. A radio flooding progress messages would show here when its reads slow down.
    private sealed class MessageCounts
    {
        private readonly object gate = new();
        private readonly Dictionary<string, int> progress = [];
        private int messages, errors, edges, garbled, implausible;

        public void Message() { lock (gate) messages++; }
        public void Error() { lock (gate) errors++; }
        public void Edge() { lock (gate) edges++; }

        /// <summary>Counts a dropped line; true while this minute's ones are still worth logging.</summary>
        public bool Garbled() { lock (gate) return ++garbled + implausible <= MaxRepliesLoggedPerMinute; }

        /// <summary>Counts an impossible RSSI reading; true while still worth logging.</summary>
        public bool Implausible() { lock (gate) return garbled + ++implausible <= MaxRepliesLoggedPerMinute; }

        public void Progress(string type)
        {
            lock (gate)
                progress[type] = progress.GetValueOrDefault(type) + 1;
        }

        public string TakeSummary()
        {
            lock (gate)
            {
                var byType = progress.Count == 0 ? "none" : string.Join(" ", progress.OrderByDescending(p => p.Value).Select(p => $"{p.Key}x{p.Value}"));
                var line = $"radio sent {messages} messages: progress {byType}, {edges} carrier edges, {errors} errors, {garbled} garbled, {implausible} implausible RSSI";
                progress.Clear();
                messages = errors = edges = garbled = implausible = 0;
                return line;
            }
        }
    }

    // The driver's connection-state events arrive on its own threads
    private sealed class SessionState
    {
        private readonly object gate = new();
        private DateTime? faultedSince;
        private bool recovered;

        public DateTime? FaultedSince { get { lock (gate) return faultedSince; } }

        public void OnConnectionState(TaitConnectionState state)
        {
            lock (gate)
            {
                if (state == TaitConnectionState.Faulted)
                {
                    faultedSince ??= DateTime.UtcNow;
                }
                else
                {
                    recovered |= faultedSince != null;
                    faultedSince = null;
                }
            }
        }

        /// <summary>True once after the radio comes back from a fault.</summary>
        public bool TakeRecovered()
        {
            lock (gate)
            {
                bool r = recovered;
                recovered = false;
                return r;
            }
        }
    }
}
