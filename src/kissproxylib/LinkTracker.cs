namespace kissproxylib;

/// <summary>One AX.25 frame to or from a modem, as recorded for the link timeline.</summary>
/// <param name="Local">Our callsign on this link, or null for a frame we only heard</param>
/// <param name="Remote">The link partner (for a heard-only frame, its sender)</param>
/// <param name="Snr">Received SNR in dB, when a radio supplies one</param>
public sealed record FrameRecord(
    DateTime Timestamp,
    string Modem,
    bool Transmitted,
    int KissPort,
    string Source,
    string Dest,
    string Path,
    string Type,
    char? CommandResponse,
    bool PollFinal,
    int? Ns,
    int? Nr,
    int? Pid,
    int InfoLength,
    string Summary,
    byte[] Raw,
    string? Local,
    string Remote,
    double? Snr = null);

/// <summary>
/// Something that happened on a link. Kind "state" carries the new State; the others mark
/// trouble: retry, poll, attempt, rej, busy, frmr, refused, unexpected-disconnect.
/// </summary>
public sealed record LinkEvent(
    DateTime Timestamp,
    string Modem,
    string Local,
    string Remote,
    string Kind,
    string? State,
    string? Detail);

/// <summary>Link states shown on the timeline.</summary>
public static class LinkStates
{
    public const string Connecting = "connecting";
    public const string Connected = "connected";
    public const string Disconnecting = "disconnecting";
    public const string Disconnected = "disconnected";
}

/// <summary>
/// Works out connected-mode link state, and the trouble on each link, purely by watching
/// frames go by in both directions. Knows nothing of the node's own state machine, so it
/// infers: I or S frames on a link it thought was down mean it is connected.
/// Not thread-safe; the caller serialises Observe.
/// </summary>
public sealed class LinkTracker
{
    /// <summary>A connected link with no frames for this long is treated as gone.</summary>
    public static readonly TimeSpan SilenceLimit = TimeSpan.FromMinutes(15);

    /// <summary>A connect that gets no UA for this long has failed.</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromMinutes(3);

    /// <summary>A DISC that gets no UA or DM for this long has finished anyway.</summary>
    public static readonly TimeSpan DisconnectTimeout = TimeSpan.FromMinutes(1);

    // Polls closer together than this are T1 retries, not T3 keep-alives
    private static readonly TimeSpan RetryPollInterval = TimeSpan.FromSeconds(60);

    // Trouble within this long before a DISC or DM makes the disconnect unexpected
    private static readonly TimeSpan TroubleWindow = TimeSpan.FromMinutes(3);

    private readonly Dictionary<(string modem, string local, string remote), Link> links = [];
    private readonly HashSet<string> localCalls = new(StringComparer.OrdinalIgnoreCase);
    private string? myBaseCall;

    /// <summary>Our station callsign; every SSID of its base call counts as ours.</summary>
    public string? MyCallsign
    {
        set => myBaseCall = string.IsNullOrWhiteSpace(value) ? null : value.Split('-')[0].Trim().ToUpperInvariant();
    }

    /// <summary>Callsigns learned as ours (sources of frames we transmitted).</summary>
    public IReadOnlyCollection<string> LocalCalls => localCalls;

    public void AddLocalCall(string call) => localCalls.Add(call);

    public bool IsLocal(string call) =>
        localCalls.Contains(call) || (myBaseCall != null && call.Split('-')[0].Equals(myBaseCall, StringComparison.OrdinalIgnoreCase));

    /// <summary>Seeds a link's state, e.g. from the store after a restart.</summary>
    public void Seed(string modem, string local, string remote, string state, DateTime since)
    {
        var link = GetLink(modem, local, remote);
        link.State = state;
        link.LastFrame = since;
        if (state == LinkStates.Connecting)
            link.ConnectingSince = since;
    }

    /// <summary>The live state of every link the tracker knows, with when it last saw a frame.</summary>
    public IEnumerable<(string Modem, string Local, string Remote, string State, DateTime LastFrame)> Links =>
        links.Select(kv => (kv.Key.modem, kv.Key.local, kv.Key.remote, kv.Value.State, kv.Value.LastFrame));

    /// <summary>
    /// Works out which link a parsed frame belongs to: (local, remote), or (null, sender)
    /// for UI frames and frames between other stations.
    /// </summary>
    public (string? Local, string Remote) Classify(bool transmitted, Ax25Fields f)
    {
        // A frame we send with a repeated (H bit) digi in its path is one we digipeated for
        // others; a frame we hear with an unrepeated digi isn't at its destination yet
        bool viaDigi = f.Digis.Any(d => !d.EndsWith('*'));
        bool digipeated = f.Digis.Any(d => d.EndsWith('*'));

        if (f.Type == "UI" || (transmitted && digipeated) || (!transmitted && viaDigi))
            return (null, f.Source);

        if (transmitted)
        {
            // Only connected-mode frames we originate teach us our own calls
            localCalls.Add(f.Source);
            return (f.Source, f.Dest);
        }

        return IsLocal(f.Dest) ? (f.Dest, f.Source) : (null, f.Source);
    }

    /// <summary>
    /// Closes links whose state should have ended by now (gone quiet, connect or disconnect
    /// never answered). Observe does this lazily on a link's next frame; call this
    /// periodically so links that simply stop are closed too.
    /// </summary>
    public List<LinkEvent> Sweep(DateTime now)
    {
        var events = new List<LinkEvent>();
        foreach (var ((modem, local, remote), link) in links)
            CloseStale(link, modem, local, remote, now, events);
        return events;
    }

    private static void CloseStale(Link link, string modem, string local, string remote, DateTime now, List<LinkEvent> events)
    {
        if (link.LastFrame == default)
            return;

        var quiet = now - link.LastFrame;
        void Close(string detail, string? trouble)
        {
            if (trouble != null)
                events.Add(new LinkEvent(link.LastFrame, modem, local, remote, "unexpected-disconnect", null, trouble));
            link.State = LinkStates.Disconnected;
            events.Add(new LinkEvent(link.LastFrame, modem, local, remote, "state", LinkStates.Disconnected, detail));
            link.ResetSession();
        }

        if (link.State == LinkStates.Connected && quiet > SilenceLimit)
            Close("went silent", $"silent for over {(int)SilenceLimit.TotalMinutes} min");
        else if (link.State == LinkStates.Connecting && quiet > ConnectTimeout)
            Close($"no connection after {link.Attempts} attempt(s)", null);
        else if (link.State == LinkStates.Disconnecting && quiet > DisconnectTimeout)
            Close($"disconnected by {link.DisconnectedBy} (no reply to DISC)", null);
    }

    /// <summary>Feeds one classified frame; returns the events it caused, in time order.</summary>
    public List<LinkEvent> Observe(FrameRecord frame)
    {
        var events = new List<LinkEvent>();
        if (frame.Local == null)
            return events;

        var link = GetLink(frame.Modem, frame.Local, frame.Remote);
        var t = frame.Timestamp;
        string sender = frame.Source;

        void Emit(string kind, string? detail, DateTime? at = null) =>
            events.Add(new LinkEvent(at ?? t, frame.Modem, frame.Local, frame.Remote, kind, null, detail));

        void SetState(string state, string detail, DateTime? at = null)
        {
            if (link.State == state)
                return;
            link.State = state;
            events.Add(new LinkEvent(at ?? t, frame.Modem, frame.Local, frame.Remote, "state", state, detail));
        }

        // Things that should have happened since the last frame
        CloseStale(link, frame.Modem, frame.Local, frame.Remote, t, events);

        var side = frame.Transmitted ? link.Ours : link.Theirs;
        var other = frame.Transmitted ? link.Theirs : link.Ours;
        bool command = frame.CommandResponse != 'R';

        switch (frame.Type)
        {
            case "SABM" or "SABME" or "XID" when command:
                // A connect on a link we think is up means that end thinks it is down (BPQ
                // only sends XID before SABM, not to renegotiate)
                if (link.State == LinkStates.Connected)
                {
                    Emit("unexpected-disconnect", $"{sender} started a new connection ({frame.Type}) while the link was up");
                    link.ResetSession();
                }

                // BPQ opens with XID and follows with SABM once it has an answer: one attempt
                bool sabmAfterXid = frame.Type != "XID" && link.PendingXidFrom == sender;
                link.PendingXidFrom = frame.Type == "XID" ? sender : null;
                if (sabmAfterXid)
                    break;

                link.Attempts++;
                if (link.Attempts > 1)
                    Emit("attempt", link.AnsweredSinceLastAttempt
                        ? $"attempt {link.Attempts} by {sender}, answered but not completed"
                        : $"attempt {link.Attempts} by {sender}, no reply");
                link.AnsweredSinceLastAttempt = false;
                link.ConnectingSince ??= t;
                if (frame.Type == "SABME")
                    link.Extended = true;
                SetState(LinkStates.Connecting, $"{frame.Type} from {sender}");
                break;

            case "XID":
                // Response (or a renegotiation while connected): the other end is alive
                link.AnsweredSinceLastAttempt = true;
                break;

            case "DM" or "FRMR" when link.PendingXidFrom != null && link.PendingXidFrom != sender:
                // The other end doesn't do XID; the SABM that follows is the real attempt
                link.AnsweredSinceLastAttempt = true;
                break;

            case "UA":
                link.AnsweredSinceLastAttempt = true;
                if (link.State == LinkStates.Connecting || link.State == LinkStates.Disconnected)
                {
                    SetState(LinkStates.Connected, link.Attempts > 1 ? $"UA from {sender} after {link.Attempts} attempts" : $"UA from {sender}");
                    link.Attempts = 0;
                    link.ConnectingSince = null;
                }
                else if (link.State == LinkStates.Disconnecting)
                {
                    SetState(LinkStates.Disconnected, $"disconnected by {link.DisconnectedBy}");
                    link.ResetSession();
                }
                break;

            case "DM":
                link.AnsweredSinceLastAttempt = true;
                if (link.State == LinkStates.Connecting)
                {
                    Emit("refused", $"connection refused by {sender} (DM)");
                    SetState(LinkStates.Disconnected, $"refused by {sender}");
                }
                else if (link.State == LinkStates.Connected)
                {
                    Emit("unexpected-disconnect", $"DM from {sender} while connected");
                    SetState(LinkStates.Disconnected, $"DM from {sender}");
                }
                else
                {
                    SetState(LinkStates.Disconnected, $"DM from {sender}");
                }
                link.ResetSession();
                break;

            case "DISC":
                if (link.State != LinkStates.Disconnecting)
                {
                    int trouble = link.TroubleSince(t - TroubleWindow);
                    if (link.State == LinkStates.Connected && trouble >= 2)
                        Emit("unexpected-disconnect", $"DISC from {sender} after {trouble} retries");
                    link.DisconnectedBy = sender;
                    SetState(LinkStates.Disconnecting, $"DISC from {sender}");
                }
                break;

            case "FRMR":
                Emit("frmr", $"frame reject from {sender}");
                link.AddTrouble(t);
                break;

            case "I" or "RR" or "RNR" or "REJ" or "SREJ":
                if (link.State != LinkStates.Connected && link.State != LinkStates.Disconnecting)
                {
                    SetState(LinkStates.Connected, $"{frame.Type} from {sender} (connection already up)");
                    link.Attempts = 0;
                    link.ConnectingSince = null;
                }
                // Sequence numbers are decoded modulo 8; on a SABME (modulo 128) link they'd be wrong
                if (!link.Extended)
                    ObserveSequenced(frame, link, side, other, command, sender, Emit);
                break;
        }

        link.LastFrame = t;
        return events;
    }

    private static void ObserveSequenced(FrameRecord frame, Link link, Side side, Side other, bool command, string sender,
        Action<string, string?, DateTime?> emit)
    {
        var t = frame.Timestamp;

        // N(R) from this side acknowledges the other side's I-frames
        if (frame.Nr is { } nr && other.AckedUpTo != nr)
        {
            if (other.AckedUpTo is { } from)
            {
                for (int k = from; k != nr; k = (k + 1) & 7)
                    other.Outstanding.Remove(k);
            }
            else
            {
                other.Outstanding.Clear();
            }
            other.AckedUpTo = nr;
            link.Progress++;
        }

        if (frame.Type == "I" && frame.Ns is { } ns)
        {
            // N(S) and the info field only: a resend may differ in N(R) or the P bit
            var key = (frame.InfoLength, Hash(frame.Raw.AsSpan(frame.Raw.Length - frame.InfoLength)));
            if (side.Outstanding.TryGetValue(ns, out var previous) && previous == key)
            {
                emit("retry", $"N(S)={ns} resent by {sender}", null);
                link.AddTrouble(t);
            }
            else
            {
                link.Progress++;
            }
            side.Outstanding[ns] = key;
        }

        switch (frame.Type)
        {
            case "REJ":
                emit("rej", $"reject from {sender}, resend from N(R)={frame.Nr}", null);
                link.AddTrouble(t);
                break;
            case "SREJ":
                emit("rej", $"selective reject from {sender} for N(R)={frame.Nr}", null);
                link.AddTrouble(t);
                break;
            case "RNR" when !side.Busy:
                emit("busy", $"{sender} busy (RNR)", null);
                break;
        }
        side.Busy = frame.Type == "RNR";

        // A poll soon after the last one, with nothing moved in between, is a retry: the
        // GB7WIN loop was RR P answered by RR F every 8 s with the same N(R) both ways
        if (command && frame.PollFinal && frame.Type != "I")
        {
            if (side.LastPoll is { } last && t - last < RetryPollInterval && side.ProgressAtLastPoll == link.Progress)
            {
                side.PollsWithoutProgress++;
                emit("poll", $"poll {side.PollsWithoutProgress + 1} by {sender} with nothing acknowledged", null);
                link.AddTrouble(t);
            }
            else
            {
                side.PollsWithoutProgress = 0;
            }
            side.LastPoll = t;
            side.ProgressAtLastPoll = link.Progress;
        }
    }

    private static int Hash(ReadOnlySpan<byte> bytes)
    {
        var hash = new HashCode();
        hash.AddBytes(bytes);
        return hash.ToHashCode();
    }

    private Link GetLink(string modem, string local, string remote)
    {
        if (!links.TryGetValue((modem, local, remote), out var link))
        {
            link = new Link();
            links[(modem, local, remote)] = link;
        }
        return link;
    }

    private sealed class Side
    {
        public readonly Dictionary<int, (int length, int hash)> Outstanding = [];
        public int? AckedUpTo;
        public bool Busy;
        public DateTime? LastPoll;
        public long ProgressAtLastPoll;
        public int PollsWithoutProgress;
    }

    private sealed class Link
    {
        public string State = LinkStates.Disconnected;
        public DateTime LastFrame;
        public DateTime? ConnectingSince;
        public int Attempts;
        public bool AnsweredSinceLastAttempt;
        public string? DisconnectedBy;
        public string? PendingXidFrom;
        public bool Extended;
        public long Progress;
        public Side Ours = new();
        public Side Theirs = new();
        private readonly Queue<DateTime> trouble = new();

        public void AddTrouble(DateTime t)
        {
            trouble.Enqueue(t);
            while (trouble.Count > 50)
                trouble.Dequeue();
        }

        public int TroubleSince(DateTime from) => trouble.Count(x => x >= from);

        public void ResetSession()
        {
            Attempts = 0;
            PendingXidFrom = null;
            Extended = false;
            ConnectingSince = null;
            AnsweredSinceLastAttempt = false;
            Ours = new Side();
            Theirs = new Side();
            trouble.Clear();
        }
    }
}
