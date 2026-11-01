namespace kissproxylib;

/// <summary>
/// Takes every frame from every modem, works out which link it belongs to, feeds the link
/// tracker and stores frame and events. Shared by all the modems' proxies.
/// </summary>
public sealed class LinkMonitor
{
    private readonly FrameStore store;
    private readonly LinkTracker tracker = new();
    private readonly object sync = new();

    public LinkMonitor(FrameStore store, string? myCallsign)
    {
        this.store = store;
        tracker.MyCallsign = myCallsign;

        foreach (var call in store.LoadLocalCalls())
            tracker.AddLocalCall(call);

        // Carry link state over a restart, with when each link last carried a frame (so a
        // link still busy isn't taken for one gone quiet)
        var lastFrames = store.LastFrameTimes();
        foreach (var e in store.LastStatesBefore(DateTime.UtcNow))
        {
            var last = lastFrames.TryGetValue((e.Modem, e.Local, e.Remote), out var t) && t > e.Timestamp ? t : e.Timestamp;
            tracker.Seed(e.Modem, e.Local, e.Remote, e.State!, last);
        }
    }

    public FrameStore Store => store;

    public string? MyCallsign
    {
        set { lock (sync) tracker.MyCallsign = value; }
    }

    /// <summary>Records one KISS frame seen going to (transmitted) or coming from a modem.</summary>
    public void Record(string modem, bool transmitted, byte[] kissFrame, DateTime? timestamp = null)
    {
        var data = Ax25Decoder.UnkissData(kissFrame);
        if (data == null)
            return; // parameter commands and ACKMODE acks aren't frames on air

        var fields = Ax25Decoder.Parse(data.Ax25);
        if (fields == null)
            return;

        var summary = Ax25Decoder.Describe(data.Ax25);
        var t = timestamp ?? DateTime.UtcNow;

        lock (sync)
        {
            int knownLocals = tracker.LocalCalls.Count;
            var (local, remote) = tracker.Classify(transmitted, fields);
            if (tracker.LocalCalls.Count != knownLocals)
                store.AddLocalCall(fields.Source);

            var frame = new FrameRecord(t, modem, transmitted, data.Port, fields.Source, fields.Dest,
                string.Join(",", fields.Digis), fields.Type, fields.CommandResponse, fields.PollFinal,
                fields.Ns, fields.Nr, fields.Pid, fields.InfoLength, summary, data.Ax25, local, remote);

            store.Add(frame, tracker.Observe(frame));
        }
    }

    /// <summary>Every link's live state, with when it last saw a frame.</summary>
    public IReadOnlyList<(string Modem, string Local, string Remote, string State, DateTime LastFrame)> LiveLinks
    {
        get { lock (sync) return tracker.Links.ToList(); }
    }
}
