namespace kissproxylib;

public sealed record TimelineSegment(DateTime Start, DateTime End, string State, string? Detail, bool Silent);

/// <summary>One or more events of a kind merged into a bin; Details holds the first few.</summary>
public sealed record TimelineMarker(DateTime Time, string Kind, int Count, IReadOnlyList<string> Details);

public sealed record TimelineActivity(DateTime Start, int Received, int Transmitted);

/// <summary>One row of the timeline: a link (or, with no Local, a station only heard).</summary>
public sealed record TimelineRow(
    string Modem,
    string? Local,
    string Remote,
    IReadOnlyList<TimelineSegment> Segments,
    IReadOnlyList<TimelineMarker> Markers,
    IReadOnlyList<TimelineActivity> Activity);

public sealed record Timeline(DateTime From, DateTime To, TimeSpan Bucket, IReadOnlyList<TimelineRow> Rows);

/// <summary>Turns stored frames and link events into timeline rows for a time window.</summary>
public static class TimelineBuilder
{
    /// <summary>Activity is counted in about this many buckets across the window.</summary>
    public const int Buckets = 720;

    /// <summary>Markers of a kind closer than window/MarkerBins are merged (about a pixel or two).</summary>
    public const int MarkerBins = 1500;

    public static Timeline Build(LinkMonitor monitor, DateTime from, DateTime to, DateTime now)
    {
        var store = monitor.Store;
        var bucket = TimeSpan.FromTicks(Math.Max(TimeSpan.TicksPerSecond, (to - from).Ticks / Buckets));

        var startStates = store.LastStatesBefore(from).ToDictionary(e => (e.Modem, (string?)e.Local, e.Remote));
        var events = store.QueryEvents(from, to);
        var activity = store.QueryActivity(from, to, bucket);
        var live = monitor.LiveLinks.ToDictionary(l => (l.Modem, (string?)l.Local, l.Remote));

        var keys = new HashSet<(string Modem, string? Local, string Remote)>();
        foreach (var e in events) keys.Add((e.Modem, e.Local, e.Remote));
        foreach (var a in activity) keys.Add((a.Modem, a.Local, a.Remote));
        foreach (var (key, e) in startStates)
        {
            if (e.State != LinkStates.Disconnected)
                keys.Add(key);
        }

        var eventsByLink = events.ToLookup(e => (e.Modem, (string?)e.Local, e.Remote));
        var activityByLink = activity.ToLookup(a => (a.Modem, a.Local, a.Remote));

        var rows = new List<TimelineRow>();
        foreach (var key in keys)
        {
            var linkEvents = eventsByLink[key].ToList();
            var segments = key.Local == null
                ? []
                : BuildSegments(from, to, now, startStates.GetValueOrDefault(key), linkEvents,
                    live.TryGetValue(key, out var l) ? l.LastFrame : null);

            rows.Add(new TimelineRow(key.Modem, key.Local, key.Remote, segments,
                MergeMarkers(linkEvents.Where(e => e.Kind != "state"), from, to),
                activityByLink[key].Select(a => new TimelineActivity(a.Start, a.Received, a.Transmitted)).ToList()));
        }

        return new Timeline(from, to, bucket, rows
            .OrderBy(r => r.Modem)
            .ThenBy(r => r.Local == null)
            .ThenBy(r => r.Remote)
            .ThenBy(r => r.Local)
            .ToList());
    }

    private static List<TimelineMarker> MergeMarkers(IEnumerable<LinkEvent> events, DateTime from, DateTime to)
    {
        long binTicks = Math.Max(TimeSpan.TicksPerMillisecond, (to - from).Ticks / MarkerBins);
        return events
            .GroupBy(e => (e.Kind, Bin: (e.Timestamp - from).Ticks / binTicks))
            .Select(g =>
            {
                var items = g.OrderBy(e => e.Timestamp).ToList();
                return new TimelineMarker(items[0].Timestamp, g.Key.Kind, items.Count,
                    items.Take(3).Select(e => $"{e.Timestamp:HH:mm:ss} {e.Detail}").ToList());
            })
            .OrderBy(m => m.Time)
            .ToList();
    }

    private static List<TimelineSegment> BuildSegments(DateTime from, DateTime to, DateTime now, LinkEvent? start,
        List<LinkEvent> events, DateTime? liveLastFrame)
    {
        var segments = new List<TimelineSegment>();
        string state = start?.State ?? LinkStates.Disconnected;
        string? detail = start?.Detail;
        var cursor = from;

        foreach (var e in events.Where(e => e.Kind == "state"))
        {
            if (state != LinkStates.Disconnected && e.Timestamp > cursor)
                segments.Add(new TimelineSegment(cursor, e.Timestamp, state, detail, false));
            state = e.State!;
            detail = e.Detail;
            cursor = e.Timestamp;
        }

        if (state == LinkStates.Disconnected)
            return segments;

        // Still open at the end of the window. If the link has gone quiet the tracker only
        // notices at its next frame, so stop the bar at the last frame and mark it silent.
        var end = to < now ? to : now;
        bool silent = false;
        if (liveLastFrame is { } last && to >= now)
        {
            var limit = state == LinkStates.Connecting ? LinkTracker.ConnectTimeout : LinkTracker.SilenceLimit;
            if (now - last > limit)
            {
                end = last > cursor ? last : cursor;
                silent = true;
            }
        }

        if (end > cursor)
            segments.Add(new TimelineSegment(cursor, end, state, detail, silent));
        return segments;
    }
}
