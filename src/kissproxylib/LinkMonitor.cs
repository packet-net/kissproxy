namespace kissproxylib;

/// <summary>
/// Takes every frame from every modem, works out which link it belongs to, feeds the link
/// tracker and stores frame and events. Shared by all the modems' proxies.
/// </summary>
public sealed class LinkMonitor : IDisposable
{
    private readonly FrameStore store;
    private readonly LinkTracker tracker = new();
    private readonly object sync = new();
    private readonly Timer? sweepTimer;

    /// <param name="sweepInterval">How often to close links that have simply stopped; zero for never (tests).</param>
    public LinkMonitor(FrameStore store, string? myCallsign, TimeSpan? sweepInterval = null)
    {
        this.store = store;
        tracker.MyCallsign = myCallsign;

        foreach (var call in store.LoadLocalCalls())
            tracker.AddLocalCall(call);

        // Carry open links over a restart, with when each last carried a frame (so a link
        // still busy isn't taken for one gone quiet)
        foreach (var e in store.LastStatesBefore(DateTime.UtcNow))
        {
            if (e.State == LinkStates.Disconnected)
                continue;
            var t = store.LastFrameTime(e.Modem, e.Local, e.Remote);
            tracker.Seed(e.Modem, e.Local, e.Remote, e.State!, t is { } last && last > e.Timestamp ? last : e.Timestamp);
        }

        var interval = sweepInterval ?? TimeSpan.FromMinutes(1);
        if (interval > TimeSpan.Zero)
            sweepTimer = new Timer(_ => Sweep(DateTime.UtcNow), null, interval, interval);
    }

    /// <summary>Closes links that have gone quiet, or whose connect or disconnect was never answered.</summary>
    public void Sweep(DateTime now)
    {
        lock (sync)
        {
            var events = tracker.Sweep(now);
            if (events.Count > 0)
                store.AddEvents(events);
        }
    }

    public void Dispose() => sweepTimer?.Dispose();

    public FrameStore Store => store;

    public string? MyCallsign
    {
        set { lock (sync) tracker.MyCallsign = value; }
    }

    /// <summary>Records one KISS frame seen going to (transmitted) or coming from a modem.</summary>
    /// <param name="radio">For a received frame, its signal readings given its AX.25 length</param>
    public void Record(string modem, bool transmitted, byte[] kissFrame, DateTime? timestamp = null,
        Func<int, RadioMetadata?>? radio = null)
    {
        var data = Ax25Decoder.UnkissData(kissFrame);
        if (data == null)
            return; // parameter commands and ACKMODE acks aren't frames on air

        var fields = Ax25Decoder.Parse(data.Ax25);
        if (fields == null)
            return;

        var summary = Ax25Decoder.Describe(data.Ax25);
        var t = timestamp ?? DateTime.UtcNow;
        var signal = transmitted ? null : radio?.Invoke(data.Ax25.Length);

        lock (sync)
        {
            int knownLocals = tracker.LocalCalls.Count;
            var (local, remote) = tracker.Classify(transmitted, fields);
            if (tracker.LocalCalls.Count != knownLocals)
                store.AddLocalCall(fields.Source);

            var frame = new FrameRecord(t, modem, transmitted, data.Port, fields.Source, fields.Dest,
                string.Join(",", fields.Digis), fields.Type, fields.CommandResponse, fields.PollFinal,
                fields.Ns, fields.Nr, fields.Pid, fields.InfoLength, summary, data.Ax25, local, remote,
                signal?.SnrDb, signal?.RssiDbm, signal?.NoiseFloorDbm);

            store.Add(frame, tracker.Observe(frame));
        }
    }

    /// <summary>Every link's live state, with when it last saw a frame.</summary>
    public IReadOnlyList<(string Modem, string Local, string Remote, string State, DateTime LastFrame)> LiveLinks
    {
        get { lock (sync) return tracker.Links.ToList(); }
    }
}
