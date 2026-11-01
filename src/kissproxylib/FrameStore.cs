using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace kissproxylib;

/// <summary>Frame counts for one link in one time bucket.</summary>
public sealed record ActivityBucket(string Modem, string? Local, string Remote, DateTime Start, int Received, int Transmitted);

/// <summary>
/// Keeps every frame and link event for a week in SQLite. Writes are batched: records wait
/// in memory and go to disk in one transaction every few minutes (and on Dispose), so the
/// SD card sees a few hundred small writes a day rather than one per frame. Queries include
/// the records that haven't been written yet. Without a database it keeps a day in memory.
/// </summary>
public sealed class FrameStore : IDisposable
{
    public static readonly TimeSpan DefaultFlushInterval = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan DefaultRetention = TimeSpan.FromDays(7);
    private static readonly TimeSpan MemoryOnlyRetention = TimeSpan.FromDays(1);

    private readonly ILogger logger;
    private readonly TimeSpan retention;
    private readonly Func<DateTime> clock;
    private readonly SqliteConnection? db;
    private readonly Timer? flushTimer;

    // pending: not yet written; inFlight: being written now (still visible to queries);
    // memory: everything, when there is no database
    private readonly object pendingLock = new();
    private List<FrameRecord> pendingFrames = [];
    private List<LinkEvent> pendingEvents = [];
    private List<FrameRecord> inFlightFrames = [];
    private List<LinkEvent> inFlightEvents = [];
    private readonly List<FrameRecord> memoryFrames = [];
    private readonly List<LinkEvent> memoryEvents = [];
    private readonly HashSet<string> pendingLocalCalls = [];

    private readonly object dbLock = new();
    private DateTime lastPrune = DateTime.MinValue;

    public bool IsPersistent => db != null;

    public FrameStore(string? dbPath, ILogger logger, TimeSpan? flushInterval = null, TimeSpan? retention = null, Func<DateTime>? clock = null)
    {
        this.logger = logger;
        this.retention = retention ?? DefaultRetention;
        this.clock = clock ?? (() => DateTime.UtcNow);

        if (dbPath != null)
        {
            try
            {
                var dir = Path.GetDirectoryName(Path.GetFullPath(dbPath));
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                db = new SqliteConnection($"Data Source={dbPath}");
                db.Open();
                Execute("""
                    PRAGMA journal_mode=WAL;
                    PRAGMA synchronous=NORMAL;
                    CREATE TABLE IF NOT EXISTS frames (
                        ts INTEGER NOT NULL, modem TEXT NOT NULL, tx INTEGER NOT NULL, kport INTEGER NOT NULL,
                        src TEXT NOT NULL, dest TEXT NOT NULL, path TEXT NOT NULL, type TEXT NOT NULL, cr TEXT,
                        pf INTEGER NOT NULL, ns INTEGER, nr INTEGER, pid INTEGER, info_len INTEGER NOT NULL,
                        summary TEXT NOT NULL, raw BLOB NOT NULL, local TEXT, remote TEXT NOT NULL, snr REAL);
                    DROP INDEX IF EXISTS frames_ts;
                    CREATE INDEX IF NOT EXISTS frames_activity ON frames(ts, modem, local, remote, tx);
                    CREATE INDEX IF NOT EXISTS frames_link ON frames(modem, remote, ts);
                    CREATE TABLE IF NOT EXISTS events (
                        ts INTEGER NOT NULL, modem TEXT NOT NULL, local TEXT NOT NULL, remote TEXT NOT NULL,
                        kind TEXT NOT NULL, state TEXT, detail TEXT);
                    CREATE INDEX IF NOT EXISTS events_ts ON events(ts);
                    CREATE TABLE IF NOT EXISTS local_calls (call TEXT PRIMARY KEY);
                    """);
            }
            catch (Exception ex)
            {
                logger.LogWarning("Frame history kept in memory only (last 24 h, lost on restart): can't open {path}: {error}", dbPath, ex.Message);
                db?.Dispose();
                db = null;
            }
        }

        var interval = flushInterval ?? DefaultFlushInterval;
        if (interval > TimeSpan.Zero)
            flushTimer = new Timer(_ => FlushSafely(), null, interval, interval);
    }

    public void Add(FrameRecord frame, IReadOnlyList<LinkEvent> events)
    {
        lock (pendingLock)
        {
            pendingFrames.Add(frame);
            pendingEvents.AddRange(events);

            // If the disk stops taking writes (an SD card gone read-only, say) don't grow forever
            if (pendingFrames.Count > MaxPendingFrames)
            {
                pendingFrames.RemoveRange(0, MaxPendingFrames / 10);
                if (!warnedDropping)
                {
                    logger.LogWarning("Frame history can't be written; dropping the oldest unwritten frames");
                    warnedDropping = true;
                }
            }
        }
    }

    // About two days of GB7RDG's traffic
    private const int MaxPendingFrames = 100_000;
    private bool warnedDropping;
    private readonly object flushLock = new();

    public void AddLocalCall(string call)
    {
        lock (pendingLock)
            pendingLocalCalls.Add(call);
    }

    /// <summary>Writes pending records to disk (or trims memory) and drops anything past retention.</summary>
    public void Flush()
    {
        // One flush at a time (the timer's and Dispose's), and the batch moves from in-flight to
        // written under the database lock, so a query sees each record exactly once
        lock (flushLock)
        {
            List<string> locals;
            lock (pendingLock)
            {
                inFlightFrames = pendingFrames;
                inFlightEvents = pendingEvents;
                pendingFrames = [];
                pendingEvents = [];
                locals = [.. pendingLocalCalls];
                pendingLocalCalls.Clear();
            }

            lock (dbLock)
            {
                try
                {
                    if (db == null)
                    {
                        var cutoff = clock() - MemoryOnlyRetention;
                        lock (pendingLock)
                        {
                            memoryFrames.AddRange(inFlightFrames);
                            memoryEvents.AddRange(inFlightEvents);
                            memoryFrames.RemoveAll(f => f.Timestamp < cutoff);
                            var keep = LatestStates(memoryEvents);
                            memoryEvents.RemoveAll(e => e.Timestamp < cutoff && !keep.Contains(e));
                        }
                    }
                    else
                    {
                        using var tx = db.BeginTransaction();
                        WriteFrames(tx, inFlightFrames);
                        WriteEvents(tx, inFlightEvents);
                        foreach (var call in locals)
                            Execute("INSERT OR IGNORE INTO local_calls(call) VALUES ($c)", tx, ("$c", call));

                        var now = clock();
                        if (now - lastPrune > TimeSpan.FromHours(1))
                        {
                            var cutoff = ToMs(now - retention);
                            Execute("DELETE FROM frames WHERE ts < $c", tx, ("$c", cutoff));
                            // Keep every link's latest state, or a link up for over a week loses its bar
                            Execute("""
                                DELETE FROM events WHERE ts < $c AND rowid NOT IN (
                                    SELECT e.rowid FROM events e
                                    JOIN (SELECT modem, local, remote, max(ts) AS ts FROM events
                                          WHERE kind = 'state' GROUP BY modem, local, remote) last
                                    ON e.modem = last.modem AND e.local = last.local AND e.remote = last.remote AND e.ts = last.ts
                                    WHERE e.kind = 'state')
                                """, tx, ("$c", cutoff));
                            lastPrune = now;
                        }
                        tx.Commit();
                    }

                    lock (pendingLock)
                    {
                        inFlightFrames = [];
                        inFlightEvents = [];
                    }
                }
                catch
                {
                    // Keep the records for the next attempt rather than losing them
                    lock (pendingLock)
                    {
                        pendingFrames.InsertRange(0, inFlightFrames);
                        pendingEvents.InsertRange(0, inFlightEvents);
                        inFlightFrames = [];
                        inFlightEvents = [];
                        foreach (var call in locals)
                            pendingLocalCalls.Add(call);
                    }
                    throw;
                }
            }
        }
    }

    private static HashSet<LinkEvent> LatestStates(IEnumerable<LinkEvent> events) =>
        events.Where(e => e.Kind == "state")
            .GroupBy(e => (e.Modem, e.Local, e.Remote))
            .Select(g => g.MaxBy(e => e.Timestamp)!)
            .ToHashSet();

    private void FlushSafely()
    {
        try { Flush(); }
        catch (Exception ex) { logger.LogWarning("Writing frame history failed: {error}", ex.Message); }
    }

    /// <summary>Frames in [from, to), oldest first, optionally for one link; total is the full count.</summary>
    public IReadOnlyList<FrameRecord> QueryFrames(DateTime from, DateTime to, string? modem, string? local, string? remote,
        int offset, int limit, out int total)
    {
        bool Match(FrameRecord f) =>
            f.Timestamp >= from && f.Timestamp < to
            && (modem == null || f.Modem == modem)
            && (remote == null || f.Remote == remote)
            && (local == null || (local == "" ? f.Local == null : f.Local == local));

        // Under the database lock, so a batch being flushed is seen once, not twice or never
        lock (dbLock)
        {
            var unwritten = Unwritten(f => Match(f));
            var result = new List<FrameRecord>();
            total = unwritten.Count;

            if (db != null)
            {
                var (where, args) = FrameFilter(from, to, modem, local, remote);
                total += Convert.ToInt32(Scalar($"SELECT count(*) FROM frames WHERE {where}", args));
                result.AddRange(ReadFrames($"SELECT * FROM frames WHERE {where} ORDER BY ts LIMIT $limit OFFSET $offset",
                    [.. args, ("$limit", offset + limit), ("$offset", 0)]));
            }

            // Unwritten frames are always newer than written ones
            result.AddRange(unwritten);
            return result.Skip(offset).Take(limit).ToList();
        }
    }

    /// <summary>Link events in [from, to), oldest first.</summary>
    public IReadOnlyList<LinkEvent> QueryEvents(DateTime from, DateTime to)
    {
        var result = new List<LinkEvent>();
        lock (dbLock)
        {
            if (db != null)
                result.AddRange(ReadEvents("SELECT * FROM events WHERE ts >= $from AND ts < $to",
                    [("$from", ToMs(from)), ("$to", ToMs(to))]));
            result.AddRange(UnwrittenEvents(e => e.Timestamp >= from && e.Timestamp < to));
        }
        return result.OrderBy(e => e.Timestamp).ToList();
    }

    /// <summary>For each link, its last state change before the given time (within retention).</summary>
    public IReadOnlyList<LinkEvent> LastStatesBefore(DateTime before)
    {
        var all = new List<LinkEvent>();
        lock (dbLock)
        {
            if (db != null)
                all.AddRange(ReadEvents("""
                    SELECT e.* FROM events e
                    JOIN (SELECT modem, local, remote, max(ts) AS ts FROM events
                          WHERE kind = 'state' AND ts < $before GROUP BY modem, local, remote) last
                    ON e.modem = last.modem AND e.local = last.local AND e.remote = last.remote AND e.ts = last.ts
                    WHERE e.kind = 'state'
                    """, [("$before", ToMs(before))]));
            all.AddRange(UnwrittenEvents(e => e.Kind == "state" && e.Timestamp < before));
        }

        return all
            .GroupBy(e => (e.Modem, e.Local, e.Remote))
            .Select(g => g.OrderBy(e => e.Timestamp).Last())
            .ToList();
    }

    /// <summary>Frame counts per link per bucket in [from, to).</summary>
    public IReadOnlyList<ActivityBucket> QueryActivity(DateTime from, DateTime to, TimeSpan bucket)
    {
        long bucketMs = Math.Max(1, (long)bucket.TotalMilliseconds);
        var counts = new Dictionary<(string, string?, string, long), (int rx, int tx)>();

        void Count(string modem, string? local, string remote, long b, int rx, int tx)
        {
            counts.TryGetValue((modem, local, remote, b), out var c);
            counts[(modem, local, remote, b)] = (c.rx + rx, c.tx + tx);
        }

        // One lock for both, so a batch being flushed is counted once
        lock (dbLock)
        {
            if (db != null)
            {
                using var cmd = db.CreateCommand();
                cmd.CommandText = """
                    SELECT modem, local, remote, (ts - $from) / $bucket AS b, sum(tx = 0), sum(tx = 1)
                    FROM frames WHERE ts >= $from AND ts < $to GROUP BY modem, local, remote, b
                    """;
                cmd.Parameters.AddWithValue("$from", ToMs(from));
                cmd.Parameters.AddWithValue("$to", ToMs(to));
                cmd.Parameters.AddWithValue("$bucket", bucketMs);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    Count(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetString(2), r.GetInt64(3), r.GetInt32(4), r.GetInt32(5));
            }

            foreach (var f in Unwritten(f => f.Timestamp >= from && f.Timestamp < to))
                Count(f.Modem, f.Local, f.Remote, (ToMs(f.Timestamp) - ToMs(from)) / bucketMs, f.Transmitted ? 0 : 1, f.Transmitted ? 1 : 0);
        }

        return counts
            .Select(kv => new ActivityBucket(kv.Key.Item1, kv.Key.Item2, kv.Key.Item3,
                from + TimeSpan.FromMilliseconds(kv.Key.Item4 * bucketMs), kv.Value.rx, kv.Value.tx))
            .OrderBy(a => a.Start)
            .ToList();
    }

    /// <summary>When a link last carried a frame, if within retention.</summary>
    public DateTime? LastFrameTime(string modem, string local, string remote)
    {
        lock (dbLock)
        {
            var unwritten = Unwritten(f => f.Modem == modem && f.Local == local && f.Remote == remote);
            if (unwritten.Count > 0)
                return unwritten[^1].Timestamp;
            if (db == null)
                return null;

            // Uses the (modem, remote, ts) index
            var ms = Scalar("SELECT max(ts) FROM frames WHERE modem = $m AND remote = $r AND local = $l",
                [("$m", modem), ("$r", remote), ("$l", local)]);
            return ms is long v ? FromMs(v) : null;
        }
    }

    /// <summary>Link events with no frame of their own (from the periodic sweep).</summary>
    public void AddEvents(IReadOnlyList<LinkEvent> events)
    {
        lock (pendingLock)
            pendingEvents.AddRange(events);
    }

    /// <summary>Callsigns learned as ours in earlier runs.</summary>
    public IReadOnlyList<string> LoadLocalCalls()
    {
        if (db == null)
            return [];
        lock (dbLock)
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT call FROM local_calls";
            using var r = cmd.ExecuteReader();
            var calls = new List<string>();
            while (r.Read())
                calls.Add(r.GetString(0));
            return calls;
        }
    }

    public void Dispose()
    {
        flushTimer?.Dispose();
        FlushSafely();
        db?.Dispose();
    }

    private List<FrameRecord> Unwritten(Func<FrameRecord, bool> match)
    {
        lock (pendingLock)
            return memoryFrames.Concat(inFlightFrames).Concat(pendingFrames).Where(match).OrderBy(f => f.Timestamp).ToList();
    }

    private List<LinkEvent> UnwrittenEvents(Func<LinkEvent, bool> match)
    {
        lock (pendingLock)
            return memoryEvents.Concat(inFlightEvents).Concat(pendingEvents).Where(match).ToList();
    }

    private static (string where, (string, object)[] args) FrameFilter(DateTime from, DateTime to, string? modem, string? local, string? remote)
    {
        var where = "ts >= $from AND ts < $to";
        var args = new List<(string, object)> { ("$from", ToMs(from)), ("$to", ToMs(to)) };
        if (modem != null) { where += " AND modem = $modem"; args.Add(("$modem", modem)); }
        if (remote != null) { where += " AND remote = $remote"; args.Add(("$remote", remote)); }
        if (local == "") where += " AND local IS NULL";
        else if (local != null) { where += " AND local = $local"; args.Add(("$local", local)); }
        return (where, args.ToArray());
    }

    private void WriteFrames(SqliteTransaction tx, List<FrameRecord> frames)
    {
        if (frames.Count == 0)
            return;

        using var cmd = db!.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO frames VALUES ($ts, $modem, $tx, $kport, $src, $dest, $path, $type, $cr, $pf, $ns, $nr, $pid,
                $info_len, $summary, $raw, $local, $remote, $snr)
            """;
        var p = new Dictionary<string, SqliteParameter>();
        foreach (var name in new[] { "$ts", "$modem", "$tx", "$kport", "$src", "$dest", "$path", "$type", "$cr", "$pf", "$ns", "$nr", "$pid", "$info_len", "$summary", "$raw", "$local", "$remote", "$snr" })
            p[name] = cmd.Parameters.Add(new SqliteParameter { ParameterName = name });

        foreach (var f in frames)
        {
            p["$ts"].Value = ToMs(f.Timestamp);
            p["$modem"].Value = f.Modem;
            p["$tx"].Value = f.Transmitted ? 1 : 0;
            p["$kport"].Value = f.KissPort;
            p["$src"].Value = f.Source;
            p["$dest"].Value = f.Dest;
            p["$path"].Value = f.Path;
            p["$type"].Value = f.Type;
            p["$cr"].Value = f.CommandResponse is { } cr ? cr.ToString() : DBNull.Value;
            p["$pf"].Value = f.PollFinal ? 1 : 0;
            p["$ns"].Value = (object?)f.Ns ?? DBNull.Value;
            p["$nr"].Value = (object?)f.Nr ?? DBNull.Value;
            p["$pid"].Value = (object?)f.Pid ?? DBNull.Value;
            p["$info_len"].Value = f.InfoLength;
            p["$summary"].Value = f.Summary;
            p["$raw"].Value = f.Raw;
            p["$local"].Value = (object?)f.Local ?? DBNull.Value;
            p["$remote"].Value = f.Remote;
            p["$snr"].Value = (object?)f.Snr ?? DBNull.Value;
            cmd.ExecuteNonQuery();
        }
    }

    private void WriteEvents(SqliteTransaction tx, List<LinkEvent> events)
    {
        if (events.Count == 0)
            return;

        using var cmd = db!.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO events VALUES ($ts, $modem, $local, $remote, $kind, $state, $detail)";
        var p = new Dictionary<string, SqliteParameter>();
        foreach (var name in new[] { "$ts", "$modem", "$local", "$remote", "$kind", "$state", "$detail" })
            p[name] = cmd.Parameters.Add(new SqliteParameter { ParameterName = name });

        foreach (var e in events)
        {
            p["$ts"].Value = ToMs(e.Timestamp);
            p["$modem"].Value = e.Modem;
            p["$local"].Value = e.Local;
            p["$remote"].Value = e.Remote;
            p["$kind"].Value = e.Kind;
            p["$state"].Value = (object?)e.State ?? DBNull.Value;
            p["$detail"].Value = (object?)e.Detail ?? DBNull.Value;
            cmd.ExecuteNonQuery();
        }
    }

    private List<FrameRecord> ReadFrames(string sql, (string, object)[] args)
    {
        using var cmd = db!.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value);
        using var r = cmd.ExecuteReader();
        var frames = new List<FrameRecord>();
        while (r.Read())
        {
            frames.Add(new FrameRecord(
                FromMs(r.GetInt64(0)), r.GetString(1), r.GetInt32(2) == 1, r.GetInt32(3),
                r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7),
                r.IsDBNull(8) ? null : r.GetString(8)[0], r.GetInt32(9) == 1,
                r.IsDBNull(10) ? null : r.GetInt32(10), r.IsDBNull(11) ? null : r.GetInt32(11),
                r.IsDBNull(12) ? null : r.GetInt32(12), r.GetInt32(13), r.GetString(14), (byte[])r.GetValue(15),
                r.IsDBNull(16) ? null : r.GetString(16), r.GetString(17), r.IsDBNull(18) ? null : r.GetDouble(18)));
        }
        return frames;
    }

    private List<LinkEvent> ReadEvents(string sql, (string, object)[] args)
    {
        using var cmd = db!.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value);
        using var r = cmd.ExecuteReader();
        var events = new List<LinkEvent>();
        while (r.Read())
        {
            events.Add(new LinkEvent(FromMs(r.GetInt64(0)), r.GetString(1), r.GetString(2), r.GetString(3),
                r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6)));
        }
        return events;
    }

    private void Execute(string sql, SqliteTransaction? tx = null, params (string, object)[] args)
    {
        using var cmd = db!.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value);
        cmd.ExecuteNonQuery();
    }

    private object? Scalar(string sql, (string, object)[] args)
    {
        using var cmd = db!.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value);
        return cmd.ExecuteScalar();
    }

    public static long ToMs(DateTime t) => (long)(DateTime.SpecifyKind(t, DateTimeKind.Utc) - DateTime.UnixEpoch).TotalMilliseconds;

    public static DateTime FromMs(long ms) => DateTime.UnixEpoch.AddMilliseconds(ms);
}
