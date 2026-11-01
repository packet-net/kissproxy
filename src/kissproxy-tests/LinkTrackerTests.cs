using AwesomeAssertions;
using kissproxylib;
using Microsoft.Extensions.Logging.Abstractions;

namespace kissproxy_tests;

public class LinkTrackerTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 10, 31, 23, 20, 0, DateTimeKind.Utc);
    private readonly string tempDir = Path.Combine(Path.GetTempPath(), $"kissproxy-links-{Guid.NewGuid():N}");
    private readonly FrameStore store;
    private readonly LinkMonitor monitor;

    public LinkTrackerTests()
    {
        store = new FrameStore(null, NullLogger.Instance, flushInterval: TimeSpan.Zero);
        monitor = new LinkMonitor(store, "GB7RDG", sweepInterval: TimeSpan.Zero);
    }

    public void Dispose()
    {
        store.Dispose();
        if (Directory.Exists(tempDir))
            Directory.Delete(tempDir, true);
    }

    // A KISS data frame: control byte, C/R from the command flag, optional PID and info
    private static byte[] Frame(string source, string dest, bool command, byte control, byte? pid = null, string info = "")
        => FrameVia(source, dest, [], command, control, pid, info);

    // digis: callsigns, with a trailing '*' for one that has repeated the frame (H bit)
    private static byte[] FrameVia(string source, string dest, string[] digis, bool command, byte control, byte? pid = null, string info = "")
    {
        var d = KissFrameBuilder.EncodeCallsign(dest, false);
        var s = KissFrameBuilder.EncodeCallsign(source, digis.Length == 0);
        if (command) d[6] |= 0x80; else s[6] |= 0x80;
        var ax25 = new List<byte>();
        ax25.AddRange(d);
        ax25.AddRange(s);
        for (int i = 0; i < digis.Length; i++)
        {
            var digi = KissFrameBuilder.EncodeCallsign(digis[i].TrimEnd('*'), i == digis.Length - 1);
            if (digis[i].EndsWith('*')) digi[6] |= 0x80;
            ax25.AddRange(digi);
        }
        ax25.Add(control);
        if (pid is { } p) ax25.Add(p);
        ax25.AddRange(System.Text.Encoding.ASCII.GetBytes(info));
        return KissFrameBuilder.BuildKissDataFrame([.. ax25]);
    }

    private static byte RR(int nr, bool pf) => (byte)(0x01 | (nr << 5) | (pf ? 0x10 : 0));
    private static byte I(int ns, int nr) => (byte)((ns << 1) | (nr << 5));
    private const byte SABM = 0x3F, UA = 0x73, DISC = 0x53, DM = 0x1F, XID = 0xBF;

    private void Rx(double seconds, byte[] frame) => monitor.Record("6m", false, frame, T0.AddSeconds(seconds));
    private void Tx(double seconds, byte[] frame) => monitor.Record("6m", true, frame, T0.AddSeconds(seconds));

    private List<LinkEvent> Events(string remote) =>
        store.QueryEvents(T0.AddDays(-1), T0.AddDays(1)).Where(e => e.Remote == remote).ToList();

    private List<string?> States(string remote) => Events(remote).Where(e => e.Kind == "state").Select(e => e.State).ToList();

    [Fact]
    public void CleanConnectAndDisconnect()
    {
        Tx(0, Frame("GB7RDG", "GB7WIN", true, SABM));
        Rx(1, Frame("GB7WIN", "GB7RDG", false, UA));
        Tx(2, Frame("GB7RDG", "GB7WIN", true, I(0, 0), 0xF0, "hello"));
        Rx(3, Frame("GB7WIN", "GB7RDG", false, RR(1, false)));
        Tx(4, Frame("GB7RDG", "GB7WIN", true, DISC));
        Rx(5, Frame("GB7WIN", "GB7RDG", false, UA));

        States("GB7WIN").Should().Equal(LinkStates.Connecting, LinkStates.Connected, LinkStates.Disconnecting, LinkStates.Disconnected);
        Events("GB7WIN").Where(e => e.Kind != "state").Should().BeEmpty();
    }

    [Fact]
    public void PollLoopThenDisconnect_IsTroubleAndUnexpected()
    {
        // The GB7WIN session of 2026-10-31: WIN polls RR P R3 every 8 s, we answer RR F R4,
        // nothing moves, then WIN gives up with DISC
        Rx(0, Frame("GB7WIN", "M0LTE", true, I(3, 3), 0xF0, "text"));
        Tx(1, Frame("M0LTE", "GB7WIN", false, RR(4, false)));
        for (int i = 0; i < 5; i++)
        {
            Rx(10 + i * 8, Frame("GB7WIN", "M0LTE", true, RR(3, true)));
            Tx(10.1 + i * 8, Frame("M0LTE", "GB7WIN", false, RR(4, true)));
        }
        Rx(60, Frame("GB7WIN", "M0LTE", true, DISC));
        Tx(60.1, Frame("M0LTE", "GB7WIN", false, UA));

        var events = Events("GB7WIN");
        events.Count(e => e.Kind == "poll").Should().Be(4);
        events.Should().Contain(e => e.Kind == "unexpected-disconnect" && e.Detail!.Contains("after"));
        States("GB7WIN").Last().Should().Be(LinkStates.Disconnected);
        events.All(e => e.Local == "M0LTE").Should().BeTrue();
    }

    [Fact]
    public void UnansweredConnectAttempts_StayConnectingThenFail()
    {
        // BPQ trying GB7WOD: XID C P every 20 s, never a reply
        for (int i = 0; i < 5; i++)
            Tx(i * 20, Frame("GB7RDG", "GB7WOD", true, XID));
        Tx(400, Frame("GB7RDG", "GB7WOD", true, XID));

        var events = Events("GB7WOD");
        events.Count(e => e.Kind == "attempt" && e.Detail!.EndsWith("no reply")).Should().Be(4);
        States("GB7WOD").Should().Equal(LinkStates.Connecting, LinkStates.Disconnected, LinkStates.Connecting);
        events.Single(e => e.State == LinkStates.Disconnected).Detail.Should().Contain("5 attempt");
    }

    [Fact]
    public void AnsweredButRepeatedAttempts_AreMarked()
    {
        // GB7WIN after its DISC: XID C P, we answer XID R F, it asks again
        for (int i = 0; i < 4; i++)
        {
            Rx(i * 9, Frame("GB7WIN", "GB7RDG", true, XID));
            Tx(i * 9 + 0.1, Frame("GB7RDG", "GB7WIN", false, XID));
        }

        Events("GB7WIN").Count(e => e.Kind == "attempt" && e.Detail!.Contains("answered but not completed")).Should().Be(3);
    }

    [Fact]
    public void ResentIFrame_IsARetry()
    {
        Tx(0, Frame("GB7RDG", "GB7WIN", true, SABM));
        Rx(1, Frame("GB7WIN", "GB7RDG", false, UA));
        Tx(2, Frame("GB7RDG", "GB7WIN", true, I(0, 0), 0xF0, "hello"));
        Tx(5, Frame("GB7RDG", "GB7WIN", true, I(0, 0), 0xF0, "hello"));
        Rx(6, Frame("GB7WIN", "GB7RDG", false, RR(1, false)));
        // A new frame reusing N(S)=0 after it was acknowledged is not a retry
        for (int n = 1; n <= 8; n++)
            Tx(6 + n, Frame("GB7RDG", "GB7WIN", true, I(n & 7, 0), 0xF0, $"line {n}"));

        Events("GB7WIN").Count(e => e.Kind == "retry").Should().Be(1);
    }

    [Fact]
    public void SilentLink_IsAnUnexpectedDisconnect()
    {
        Tx(0, Frame("GB7RDG", "GB7WIN", true, SABM));
        Rx(1, Frame("GB7WIN", "GB7RDG", false, UA));
        Rx(1 + 20 * 60, Frame("GB7WIN", "GB7RDG", true, RR(0, true)));

        var events = Events("GB7WIN");
        events.Should().Contain(e => e.Kind == "unexpected-disconnect" && e.Detail!.StartsWith("silent"));
        States("GB7WIN").Should().Equal(LinkStates.Connecting, LinkStates.Connected, LinkStates.Disconnected, LinkStates.Connected);
    }

    [Fact]
    public void DmToSabm_IsARefusal()
    {
        Tx(0, Frame("GB7RDG", "GB7WIN", true, SABM));
        Rx(1, Frame("GB7WIN", "GB7RDG", false, DM));

        Events("GB7WIN").Should().Contain(e => e.Kind == "refused");
        States("GB7WIN").Last().Should().Be(LinkStates.Disconnected);
    }

    [Fact]
    public void TrafficOnUnknownLink_InfersConnected()
    {
        Rx(0, Frame("GB7WIN", "GB7RDG", true, RR(1, true)));
        States("GB7WIN").Should().Equal(LinkStates.Connected);
    }

    [Fact]
    public void BeaconsAndOtherStations_AreHeardOnly()
    {
        Rx(0, Frame("GB7WOD", "ID", true, 0x03, 0xF0, "beacon"));
        Rx(1, Frame("G8PZT", "GB7XYZ", true, SABM));
        // GB7RDG-2 is ours (same base call as GB7RDG) even before we have transmitted as it
        Rx(2, Frame("GB7BEX", "GB7RDG-2", true, SABM));

        var frames = store.QueryFrames(T0.AddDays(-1), T0.AddDays(1), null, null, null, 0, 100, out var total);
        total.Should().Be(3);
        frames[0].Local.Should().BeNull();
        frames[1].Local.Should().BeNull();
        frames[2].Local.Should().Be("GB7RDG-2");
        Events("GB7WOD").Should().BeEmpty();
        Events("G8PZT").Should().BeEmpty();
    }

    [Fact]
    public void Sqlite_FlushQueryAndStateSurvivesRestart()
    {
        var dbPath = Path.Combine(tempDir, "frames.db");
        using (var sqlStore = new FrameStore(dbPath, NullLogger.Instance, flushInterval: TimeSpan.Zero))
        {
            sqlStore.IsPersistent.Should().BeTrue();
            var m = new LinkMonitor(sqlStore, null, TimeSpan.Zero);
            m.Record("70cm", true, Frame("M0LTE", "GB7WIN", true, SABM), T0);
            m.Record("70cm", false, Frame("GB7WIN", "M0LTE", false, UA), T0.AddSeconds(1));

            // Unwritten frames are visible before a flush
            sqlStore.QueryFrames(T0, T0.AddHours(1), "70cm", null, null, 0, 10, out var before);
            before.Should().Be(2);
            sqlStore.Flush();
            m.Record("70cm", false, Frame("GB7WIN", "M0LTE", true, RR(0, true)), T0.AddSeconds(2));

            var frames = sqlStore.QueryFrames(T0, T0.AddHours(1), "70cm", "M0LTE", "GB7WIN", 0, 10, out var total);
            total.Should().Be(3);
            frames.Select(f => f.Type).Should().Equal("SABM", "UA", "RR");
            frames[0].Raw.Should().NotBeEmpty();

            var activity = sqlStore.QueryActivity(T0, T0.AddHours(1), TimeSpan.FromMinutes(1));
            activity.Should().ContainSingle(a => a.Remote == "GB7WIN" && a.Received == 2 && a.Transmitted == 1);
        } // Dispose flushes

        using var reopened = new FrameStore(dbPath, NullLogger.Instance, flushInterval: TimeSpan.Zero);
        reopened.LoadLocalCalls().Should().Contain("M0LTE");
        var restarted = new LinkMonitor(reopened, null, TimeSpan.Zero);
        restarted.LiveLinks.Should().ContainSingle(l => l.Remote == "GB7WIN" && l.State == LinkStates.Connected);
    }

    [Fact]
    public void Timeline_BuildsSegmentsAndCutsSilentLinks()
    {
        Tx(0, Frame("GB7RDG", "GB7WIN", true, SABM));
        Rx(10, Frame("GB7WIN", "GB7RDG", false, UA));
        Rx(70, Frame("GB7WIN", "GB7RDG", true, RR(0, true)));
        Rx(80, Frame("GB7WOD", "ID", true, 0x03, 0xF0, "beacon"));

        // Viewed an hour later: the link went quiet after 70 s
        var now = T0.AddHours(1);
        var timeline = TimelineBuilder.Build(monitor, T0.AddMinutes(-5), now, now);

        var win = timeline.Rows.Single(r => r.Remote == "GB7WIN");
        win.Segments.Select(s => s.State).Should().Equal(LinkStates.Connecting, LinkStates.Connected);
        win.Segments[1].Silent.Should().BeTrue();
        win.Segments[1].End.Should().Be(T0.AddSeconds(70));
        win.Activity.Sum(a => a.Received + a.Transmitted).Should().Be(3);

        var heard = timeline.Rows.Single(r => r.Remote == "GB7WOD");
        heard.Local.Should().BeNull();
        heard.Segments.Should().BeEmpty();
    }

    [Fact]
    public void BpqXidThenSabm_IsOneCleanConnect()
    {
        Tx(0, Frame("GB7RDG", "GB7WIN", true, XID));
        Rx(1, Frame("GB7WIN", "GB7RDG", false, XID));
        Tx(2, Frame("GB7RDG", "GB7WIN", true, SABM));
        Rx(3, Frame("GB7WIN", "GB7RDG", false, UA));

        Events("GB7WIN").Where(e => e.Kind != "state").Should().BeEmpty();
        Events("GB7WIN").Last().Detail.Should().Be("UA from GB7WIN");
    }

    [Fact]
    public void DmToXid_IsNoXidSupportNotARefusal()
    {
        Tx(0, Frame("GB7RDG", "G8XYZ", true, XID));
        Rx(1, Frame("G8XYZ", "GB7RDG", false, DM));
        Tx(2, Frame("GB7RDG", "G8XYZ", true, SABM));
        Rx(3, Frame("G8XYZ", "GB7RDG", false, UA));

        Events("G8XYZ").Where(e => e.Kind != "state").Should().BeEmpty();
        States("G8XYZ").Should().Equal(LinkStates.Connecting, LinkStates.Connected);
    }

    [Fact]
    public void DigipeatingThroughUs_IsHeardOnlyAndTeachesNoCalls()
    {
        Rx(0, FrameVia("M0ABC", "M0XYZ", ["GB7RDG"], true, SABM));
        Tx(0.1, FrameVia("M0ABC", "M0XYZ", ["GB7RDG*"], true, SABM));
        Rx(1, FrameVia("M0XYZ", "M0ABC", ["GB7RDG"], false, UA));
        Tx(1.1, FrameVia("M0XYZ", "M0ABC", ["GB7RDG*"], false, UA));

        monitor.LiveLinks.Should().BeEmpty();
        store.QueryFrames(T0.AddDays(-1), T0.AddDays(1), null, null, null, 0, 10, out _).Should().OnlyContain(f => f.Local == null);
    }

    [Fact]
    public void FrameHeardBeforeItsDigipeater_IsNotCountedTwice()
    {
        // GB7WIN connects to us via GB7XYZ; we hear it both direct and repeated
        Rx(0, FrameVia("GB7WIN", "GB7RDG", ["GB7XYZ"], true, SABM));
        Rx(0.5, FrameVia("GB7WIN", "GB7RDG", ["GB7XYZ*"], true, SABM));

        Events("GB7WIN").Should().NotContain(e => e.Kind == "attempt");
        States("GB7WIN").Should().Equal(LinkStates.Connecting);
    }

    [Fact]
    public void Sweep_ClosesLinksThatJustStop()
    {
        Tx(0, Frame("GB7RDG", "GB7WIN", true, SABM));
        Rx(1, Frame("GB7WIN", "GB7RDG", false, UA));
        Tx(10, Frame("GB7RDG", "GB7BEX", true, SABM));
        Rx(11, Frame("GB7BEX", "GB7RDG", false, UA));
        Tx(12, Frame("GB7RDG", "GB7BEX", true, DISC)); // no UA ever comes

        monitor.Sweep(T0.AddMinutes(5));
        States("GB7BEX").Last().Should().Be(LinkStates.Disconnected);
        States("GB7WIN").Last().Should().Be(LinkStates.Connected);

        monitor.Sweep(T0.AddMinutes(20));
        States("GB7WIN").Last().Should().Be(LinkStates.Disconnected);
        Events("GB7WIN").Should().Contain(e => e.Kind == "unexpected-disconnect");
        Events("GB7WIN").Single(e => e.State == LinkStates.Disconnected).Timestamp.Should().Be(T0.AddSeconds(1));
    }

    [Fact]
    public void ResendWithPollBit_IsStillARetry()
    {
        Tx(0, Frame("GB7RDG", "GB7WIN", true, SABM));
        Rx(1, Frame("GB7WIN", "GB7RDG", false, UA));
        Tx(2, Frame("GB7RDG", "GB7WIN", true, I(0, 0), 0xF0, "hello"));
        Tx(6, Frame("GB7RDG", "GB7WIN", true, (byte)(I(0, 0) | 0x10), 0xF0, "hello"));

        Events("GB7WIN").Count(e => e.Kind == "retry").Should().Be(1);
    }

    [Fact]
    public void ExtendedModeLink_SkipsSequenceAnalysis()
    {
        const byte SABME = 0x7F;
        Tx(0, Frame("GB7RDG", "GB7WIN", true, SABME));
        Rx(1, Frame("GB7WIN", "GB7RDG", false, UA));
        for (int i = 0; i < 4; i++)
            Rx(10 + i * 8, Frame("GB7WIN", "GB7RDG", true, RR(3, true)));

        Events("GB7WIN").Should().NotContain(e => e.Kind == "poll");
        States("GB7WIN").Should().Equal(LinkStates.Connecting, LinkStates.Connected);
    }

    [Fact]
    public void LongLivedLink_KeepsItsBarAfterPruning()
    {
        var dbPath = Path.Combine(tempDir, "prune.db");
        var now = T0;
        using var sqlStore = new FrameStore(dbPath, NullLogger.Instance, flushInterval: TimeSpan.Zero,
            retention: TimeSpan.FromHours(1), clock: () => now);
        var m = new LinkMonitor(sqlStore, "GB7RDG", TimeSpan.Zero);
        m.Record("6m", true, Frame("GB7RDG", "GB7WIN", true, SABM), T0);
        m.Record("6m", false, Frame("GB7WIN", "GB7RDG", false, UA), T0.AddSeconds(1));
        for (int i = 1; i <= 60; i++)
            m.Record("6m", true, Frame("GB7RDG", "GB7WIN", true, RR(0, true)), T0.AddMinutes(i * 3));

        now = T0.AddHours(3);
        sqlStore.Flush(); // prunes everything older than an hour, but not the link's latest state

        var timeline = TimelineBuilder.Build(m, now.AddMinutes(-30), now, now);
        timeline.Rows.Single(r => r.Remote == "GB7WIN").Segments.Should().ContainSingle(s => s.State == LinkStates.Connected);
    }
}
