using AwesomeAssertions;
using kissproxylib;
using M0LTE.Rig;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace kissproxy_tests;

public class RadioSignalTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 11, 1, 14, 0, 0, DateTimeKind.Utc);
    private readonly string tempDir = Path.Combine(Path.GetTempPath(), $"kissproxy-radio-{Guid.NewGuid():N}");
    private readonly FakeRadio radio = new();
    private readonly List<NoiseSample> noise = [];
    private readonly RadioSignalMonitor monitor;

    public RadioSignalTests()
    {
        monitor = new RadioSignalMonitor(radio, "70cm", noise.Add, startSampling: false);
    }

    public void Dispose()
    {
        if (Directory.Exists(tempDir))
            Directory.Delete(tempDir, true);
    }

    // Idle samples every 400 ms at the given level, from start for the given time
    private void Idle(DateTime start, TimeSpan length, float dbm)
    {
        radio.Busy = false;
        for (var t = start; t < start + length; t += TimeSpan.FromMilliseconds(400))
            monitor.Record(t, dbm);
    }

    // A transmission: carrier up, samples every 40 ms, carrier down
    private void Carrier(DateTime rise, TimeSpan length, float dbm)
    {
        radio.Busy = true;
        monitor.OnCarrierSense(true, rise);
        for (var t = rise; t < rise + length; t += TimeSpan.FromMilliseconds(40))
            monitor.Record(t, dbm);
        radio.Busy = false;
        monitor.OnCarrierSense(false, rise + length);
    }

    [Fact]
    public void FrameGetsItsCarriersRssiAndSnrAgainstTheIdleFloor()
    {
        Idle(T0, TimeSpan.FromSeconds(10), -121);
        Carrier(T0.AddSeconds(10), TimeSpan.FromMilliseconds(600), -90);

        // Delivered by the modem shortly after the carrier fell
        var meta = monitor.Attribute(T0.AddSeconds(10.7), 40, 9600);

        meta.Should().NotBeNull();
        meta!.RssiDbm.Should().Be(-90);
        meta.NoiseFloorDbm.Should().BeApproximately(-121, 0.01f);
        meta.SnrDb.Should().BeApproximately(31, 0.01f);
        meta.SampleCount.Should().Be(15);
    }

    [Fact]
    public void NextStationKeyingInsideTheSlack_DoesNotStealTheFrame()
    {
        Idle(T0, TimeSpan.FromSeconds(5), -120);
        Carrier(T0.AddSeconds(5), TimeSpan.FromMilliseconds(500), -95);
        // The other end answers 100 ms later, much stronger
        radio.Busy = true;
        monitor.OnCarrierSense(true, T0.AddSeconds(5.6));
        monitor.Record(T0.AddSeconds(5.6), -70);

        // The first station's 40-byte frame, delivered 200 ms after its carrier fell: at 1200
        // baud it couldn't have been carried by the window that opened 100 ms ago
        var meta = monitor.Attribute(T0.AddSeconds(5.7), 40, 1200);

        meta!.RssiDbm.Should().Be(-95);
    }

    [Fact]
    public void NoSampleOnAir_GivesNoReadingRatherThanAGuess()
    {
        Idle(T0, TimeSpan.FromSeconds(5), -120);

        monitor.Attribute(T0.AddSeconds(30), 40, 9600).Should().BeNull();
    }

    [Fact]
    public void WithoutCarrierSense_SamplesAboveTheFloorCountAsSignal()
    {
        var plain = new FakeRadio { Capabilities = RadioCapabilities.RssiRead, Busy = null };
        var m = new RadioSignalMonitor(plain, "2m", startSampling: false);
        for (int i = 0; i < 20; i++)
            m.Record(T0.AddMilliseconds(i * 400), -118);
        for (int i = 0; i < 5; i++)
            m.Record(T0.AddSeconds(8).AddMilliseconds(i * 40), -80);

        var meta = m.Attribute(T0.AddSeconds(8.3), 40, 1200);

        meta!.RssiDbm.Should().Be(-80);
        meta.SnrDb.Should().BeApproximately(38, 0.5f);
    }

    [Fact]
    public void NoiseFloorIsReportedOncePerMinute()
    {
        Idle(T0, TimeSpan.FromSeconds(60), -121);
        Idle(T0.AddSeconds(60), TimeSpan.FromSeconds(60), -110);
        monitor.Record(T0.AddSeconds(120), -110);

        noise.Should().HaveCount(2);
        noise[0].Timestamp.Should().Be(T0);
        noise[0].MedianIdleDbm.Should().Be(-121);
        noise[1].Timestamp.Should().Be(T0.AddMinutes(1));
        noise[1].MedianIdleDbm.Should().Be(-110);
        noise[1].FloorDbm.Should().BeApproximately(-110, 0.1f);
    }

    [Fact]
    public void ReceivedFramesAreStoredWithSignal_TransmittedWithout()
    {
        using var store = new FrameStore(null, NullLogger.Instance, flushInterval: TimeSpan.Zero);
        var links = new LinkMonitor(store, "GB7RDG", TimeSpan.Zero, attributionDelay: TimeSpan.Zero);
        var meta = new RadioMetadata(-90, 31, -121, 15);
        var ui = KissFrameBuilder.BuildKissDataFrame(KissFrameBuilder.BuildAx25UiFrame("GB7WOD", "BEACON", "hi"u8.ToArray()));

        links.Record("70cm", false, ui, T0, _ => meta);
        links.Record("70cm", true, ui, T0.AddSeconds(1), _ => meta);

        var frames = store.QueryFrames(T0, T0.AddMinutes(1), null, null, null, 0, 10, out _);
        (frames[0].Snr, frames[0].Rssi, frames[0].NoiseFloor).Should().Be((31.0, -90.0, -121.0));
        (frames[1].Snr, frames[1].Rssi, frames[1].NoiseFloor).Should().Be(((double?)null, (double?)null, (double?)null));
    }

    [Fact]
    public void OlderDatabase_GainsTheSignalColumns_AndNoiseRoundTrips()
    {
        Directory.CreateDirectory(tempDir);
        var dbPath = Path.Combine(tempDir, "old.db");
        using (var old = new SqliteConnection($"Data Source={dbPath}"))
        {
            old.Open();
            using var cmd = old.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE frames (
                    ts INTEGER NOT NULL, modem TEXT NOT NULL, tx INTEGER NOT NULL, kport INTEGER NOT NULL,
                    src TEXT NOT NULL, dest TEXT NOT NULL, path TEXT NOT NULL, type TEXT NOT NULL, cr TEXT,
                    pf INTEGER NOT NULL, ns INTEGER, nr INTEGER, pid INTEGER, info_len INTEGER NOT NULL,
                    summary TEXT NOT NULL, raw BLOB NOT NULL, local TEXT, remote TEXT NOT NULL, snr REAL);
                INSERT INTO frames VALUES (1, '2m', 0, 0, 'A', 'B', '', 'UI', 'C', 0, NULL, NULL, 240, 0, 's', x'00', NULL, 'A', NULL);
                """;
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        using var store = new FrameStore(dbPath, NullLogger.Instance, flushInterval: TimeSpan.Zero);
        store.IsPersistent.Should().BeTrue();
        store.QueryFrames(FrameStore.FromMs(0), FrameStore.FromMs(10), null, null, null, 0, 10, out var total);
        total.Should().Be(1);

        store.AddNoise(new NoiseSample(T0, "70cm", -121.5f, -122f));
        store.Flush();
        store.QueryNoise(T0, T0.AddMinutes(1)).Should().ContainSingle(n => n.FloorDbm == -121.5f && n.MedianIdleDbm == -122f);
    }

    [Fact]
    public void OurOwnTransmissions_DontMoveTheNoiseFloor()
    {
        Idle(T0, TimeSpan.FromSeconds(10), -121);
        monitor.SetTransmitting(true);
        Idle(T0.AddSeconds(10), TimeSpan.FromSeconds(5), -40); // the receiver sees our own carrier
        monitor.SetTransmitting(false);

        monitor.NoiseFloorDbm.Should().BeApproximately(-121, 0.01f);
    }

    [Fact]
    public void CarrierTooShortToSample_StillGivesTheFloor()
    {
        Idle(T0, TimeSpan.FromSeconds(10), -121);
        monitor.OnCarrierSense(true, T0.AddSeconds(10.1));
        monitor.OnCarrierSense(false, T0.AddSeconds(10.2));

        var meta = monitor.Attribute(T0.AddSeconds(10.25), 20, 9600);

        meta.Should().Be(new RadioMetadata(null, null, meta!.NoiseFloorDbm, 0));
        meta.NoiseFloorDbm.Should().BeApproximately(-121, 0.01f);
    }

    [Fact]
    public void ClockSteppingBack_DropsTheOldSamples()
    {
        Idle(T0.AddHours(1), TimeSpan.FromSeconds(2), -121);
        Idle(T0, TimeSpan.FromSeconds(10), -121);
        Carrier(T0.AddSeconds(10), TimeSpan.FromMilliseconds(400), -90);

        monitor.Attribute(T0.AddSeconds(10.5), 40, 9600)!.SampleCount.Should().Be(10);
    }

    [Fact]
    public void DenseReadings_ASpikeBarelyMovesTheFloor()
    {
        // Continuous reads: one every 14 ms
        radio.Busy = false;
        for (var t = T0; t < T0.AddSeconds(20); t += TimeSpan.FromMilliseconds(14))
            monitor.Record(t, -121);
        monitor.Record(T0.AddSeconds(20.014), -100);

        monitor.NoiseFloorDbm.Should().BeApproximately(-121, 0.1f);
    }

    [Fact]
    public void DenseReadings_FloorFollowsARealChangeOverSeconds()
    {
        radio.Busy = false;
        for (var t = T0; t < T0.AddSeconds(20); t += TimeSpan.FromMilliseconds(14))
            monitor.Record(t, -121);
        for (var t = T0.AddSeconds(20); t < T0.AddSeconds(30); t += TimeSpan.FromMilliseconds(14))
            monitor.Record(t, -111);

        // Ten seconds at -111 with a 10 s time constant: most of the way there, not all
        monitor.NoiseFloorDbm.Should().BeInRange(-115, -113);
    }

    [Fact]
    public void ShortBurstAt9600_GetsSeveralReadings()
    {
        // A 15-byte RR at 9600 baud with a 50 ms TXDELAY: about 65 ms of carrier
        radio.Busy = false;
        for (var t = T0; t < T0.AddSeconds(5); t += TimeSpan.FromMilliseconds(14))
            monitor.Record(t, -125);
        var rise = T0.AddSeconds(5);
        radio.Busy = true;
        monitor.OnCarrierSense(true, rise);
        for (var t = rise; t < rise.AddMilliseconds(65); t += TimeSpan.FromMilliseconds(14))
            monitor.Record(t, -113);
        radio.Busy = false;
        monitor.OnCarrierSense(false, rise.AddMilliseconds(65));

        var meta = monitor.Attribute(rise.AddMilliseconds(90), 15, 9600);

        meta!.SampleCount.Should().BeGreaterThanOrEqualTo(4);
        meta.SnrDb.Should().BeApproximately(12, 0.5f);
    }

    [Fact]
    public async Task SamplerThread_ReadsAndReportsItsRate()
    {
        // A clock that moves a second per reading, so a minute's stats come round quickly
        long ticks = T0.Ticks;
        var lines = new List<string>();
        var m = new RadioSignalMonitor(radio, "70cm", clock: () => new DateTime(Interlocked.Add(ref ticks, TimeSpan.TicksPerSecond), DateTimeKind.Utc),
            log: line => { lock (lines) lines.Add(line); });

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            lock (lines) if (lines.Count > 0) break;
            await Task.Delay(20);
        }
        await m.DisposeAsync();

        lock (lines)
        {
            lines.Should().NotBeEmpty();
            lines[0].Should().MatchRegex(@"^RSSI [0-9.]+ reads/s, [0-9.]+ ms average, [0-9]+ ms worst, 0 failed, noise floor -120\.0 dBm; carrier lag not measured yet$");
        }
        m.NoiseFloorDbm.Should().Be(-120);
    }

    [Fact]
    public void RadioReportingLate_StillGetsTheFramesReadings()
    {
        // GB7RDG's 70cm radio at 9600 baud: the NinoTNC delivers a short RR before the radio
        // reports its carrier, and the raised readings land just after delivery
        Idle(T0, TimeSpan.FromSeconds(5), -126);
        var delivered = T0.AddSeconds(5);
        monitor.Record(delivered.AddMilliseconds(-31), -126);
        radio.Busy = true;
        monitor.OnCarrierSense(true, delivered.AddMilliseconds(5));
        monitor.Record(delivered.AddMilliseconds(17), -113);
        monitor.Record(delivered.AddMilliseconds(45), -114);
        radio.Busy = false;
        monitor.OnCarrierSense(false, delivered.AddMilliseconds(50));
        monitor.Record(delivered.AddMilliseconds(67), -125);

        var meta = monitor.Attribute(delivered, 15, 9600);

        meta!.SampleCount.Should().Be(2);
        meta.RssiDbm.Should().Be(-113);
        meta.SnrDb.Should().BeApproximately(13, 0.5f);
    }

    [Fact]
    public void NextStationKeyingWellAfter_IsNotTheFrames()
    {
        Idle(T0, TimeSpan.FromSeconds(5), -126);
        var delivered = T0.AddSeconds(5);
        // Nothing on air for this frame; another station keys 300 ms later, strongly
        radio.Busy = true;
        monitor.OnCarrierSense(true, delivered.AddMilliseconds(300));
        monitor.Record(delivered.AddMilliseconds(310), -80);

        monitor.Attribute(delivered, 15, 9600)?.RssiDbm.Should().BeNull();
    }

    [Fact]
    public async Task DelayedAttribution_StoresTheFrameLaterWithItsSignal_ButTracksItAtOnce()
    {
        using var store = new FrameStore(null, NullLogger.Instance, flushInterval: TimeSpan.Zero);
        var links = new LinkMonitor(store, "GB7RDG", TimeSpan.Zero, attributionDelay: TimeSpan.FromMilliseconds(100));
        var sabm = new List<byte>();
        var d = KissFrameBuilder.EncodeCallsign("GB7RDG", false); d[6] |= 0x80;
        sabm.AddRange(d);
        sabm.AddRange(KissFrameBuilder.EncodeCallsign("GB7BSK", true));
        sabm.Add(0x3F);
        int calls = 0;

        links.Record("70cm", false, KissFrameBuilder.BuildKissDataFrame([.. sabm]), T0, _ => { calls++; return new RadioMetadata(-113, 13, -126, 2); });

        links.LiveLinks.Should().ContainSingle(l => l.Remote == "GB7BSK" && l.State == LinkStates.Connecting);
        store.QueryFrames(T0, T0.AddMinutes(1), null, null, null, 0, 10, out var before);
        before.Should().Be(0);
        calls.Should().Be(0);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        int total = 0;
        while (DateTime.UtcNow < deadline && total == 0)
        {
            await Task.Delay(20);
            store.QueryFrames(T0, T0.AddMinutes(1), null, null, null, 0, 10, out total);
        }
        store.QueryFrames(T0, T0.AddMinutes(1), null, null, null, 0, 10, out _).Single().Snr.Should().Be(13);
    }

    [Fact]
    public void QuickResponderJustAfter_DoesNotStealTheFrame_OnAPromptRadio()
    {
        // 2m-style radio: the frame's carrier is reported well before delivery
        Idle(T0, TimeSpan.FromSeconds(5), -122);
        var delivered = T0.AddSeconds(5);
        Carrier(delivered.AddMilliseconds(-229), TimeSpan.FromMilliseconds(211), -109);
        // A fast 9600 responder keys up 60 ms after delivery, much stronger, and is gone by the
        // time the frame is attributed
        Carrier(delivered.AddMilliseconds(60), TimeSpan.FromMilliseconds(100), -80);

        monitor.Attribute(delivered, 60, 9600)!.RssiDbm.Should().Be(-109);
    }

    [Fact]
    public void PreviousStationJustBefore_DoesNotStealTheFrame_OnALateRadio()
    {
        // 70cm-style late radio: the previous station's carrier ended 150 ms before delivery,
        // and this frame's own carrier is only reported after delivery
        Idle(T0, TimeSpan.FromSeconds(5), -126);
        var delivered = T0.AddSeconds(5);
        Carrier(delivered.AddMilliseconds(-450), TimeSpan.FromMilliseconds(300), -100);
        radio.Busy = true;
        monitor.OnCarrierSense(true, delivered.AddMilliseconds(5));
        monitor.Record(delivered.AddMilliseconds(17), -113);
        radio.Busy = false;
        monitor.OnCarrierSense(false, delivered.AddMilliseconds(50));

        monitor.Attribute(delivered, 15, 9600)!.RssiDbm.Should().Be(-113);
    }

    [Fact]
    public void SlowMode_NextStationKeyingBeforeDelivery_DoesNotStealTheFrame()
    {
        // 1200 baud: a 20-byte frame (153 ms) ends on air 80 ms before delivery; the next
        // station keys up in that gap and is still up at delivery
        Idle(T0, TimeSpan.FromSeconds(5), -120);
        var delivered = T0.AddSeconds(5);
        Carrier(delivered.AddMilliseconds(-600), TimeSpan.FromMilliseconds(520), -95);
        radio.Busy = true;
        monitor.OnCarrierSense(true, delivered.AddMilliseconds(-40));
        monitor.Record(delivered.AddMilliseconds(-30), -70);

        monitor.Attribute(delivered, 20, 1200)!.RssiDbm.Should().Be(-95);
    }

    [Fact]
    public void UnknownBitRate_NoLookAhead()
    {
        Idle(T0, TimeSpan.FromSeconds(5), -120);
        var delivered = T0.AddSeconds(5);
        Carrier(delivered.AddMilliseconds(-300), TimeSpan.FromMilliseconds(250), -95);
        Carrier(delivered.AddMilliseconds(50), TimeSpan.FromMilliseconds(100), -70);

        monitor.Attribute(delivered, 20, null)!.RssiDbm.Should().Be(-95);
    }

    // A short frame whose carrier the radio reports riseLagMs after delivery
    private DateTime LateFrame(DateTime delivered, double riseLagMs, float dbm)
    {
        radio.Busy = true;
        monitor.OnCarrierSense(true, delivered.AddMilliseconds(riseLagMs));
        monitor.Record(delivered.AddMilliseconds(riseLagMs + 10), dbm);
        radio.Busy = false;
        monitor.OnCarrierSense(false, delivered.AddMilliseconds(riseLagMs + 40));
        return delivered;
    }

    [Fact]
    public void LateRadio_LearnsItsLookAhead_AndUsesItAtAnyBitRate()
    {
        Idle(T0, TimeSpan.FromSeconds(2), -126);
        for (int i = 0; i < 12; i++)
        {
            var delivered = T0.AddSeconds(3 + i * 2);
            LateFrame(delivered, 20 + i * 3, -113); // lags 20..53 ms
            monitor.Attribute(delivered, 15, 9600);
        }
        // 15 bytes at 9600 is 15 ms on air, so the lags from frame start are 35..68 ms:
        // p90 is 65, plus 30
        monitor.LagSummary().Should().Contain("look-ahead 95 ms");

        // A longer frame on the same radio, reported just as late, isn't rejected for its airtime
        var longer = T0.AddSeconds(40);
        LateFrame(longer, 40, -110);
        monitor.Attribute(longer, 128, 9600)!.RssiDbm.Should().Be(-110);
    }

    [Fact]
    public void PromptRadio_LearnsNoLookAhead_EvenAtFastRates()
    {
        Idle(T0, TimeSpan.FromSeconds(2), -122);
        for (int i = 0; i < 12; i++)
        {
            var delivered = T0.AddSeconds(3 + i * 2);
            Carrier(delivered.AddMilliseconds(-150), TimeSpan.FromMilliseconds(120), -109);
            monitor.Attribute(delivered, 15, 9600);
        }
        monitor.LagSummary().Should().Contain("look-ahead 0 ms");

        // So a station keying 40 ms after delivery can't take a 9600 frame whose own carrier was missed
        var delivered2 = T0.AddSeconds(40);
        Carrier(delivered2.AddMilliseconds(40), TimeSpan.FromMilliseconds(100), -80);
        monitor.Attribute(delivered2, 15, 9600)?.RssiDbm.Should().NotBe(-80);
    }

    [Fact]
    public void SlowRadio_AReadingStraddlingAShortCarrierStillCounts()
    {
        // 39 ms reads, as on GB7RDG's 70cm radio. A 30 ms carrier as reported, with the one read
        // that overlapped it stamped (at its middle) 10 ms after the reported fall
        monitor.NoteReadTime(TimeSpan.FromMilliseconds(39));
        Idle(T0, TimeSpan.FromSeconds(5), -126);
        var delivered = T0.AddSeconds(5);
        radio.Busy = true;
        monitor.OnCarrierSense(true, delivered.AddMilliseconds(-40));
        radio.Busy = false;
        monitor.OnCarrierSense(false, delivered.AddMilliseconds(-10));
        monitor.Record(delivered, -113);

        monitor.Attribute(delivered, 15, 1200)!.RssiDbm.Should().Be(-113);
    }

    private sealed class FakeRadio : IRadioControl
    {
        public RadioCapabilities Capabilities { get; init; } = RadioCapabilities.RssiRead | RadioCapabilities.CarrierSense;
        public bool? Busy { get; set; } = false;
        public bool? ChannelBusy => Busy;
        public event EventHandler<CarrierSenseChange>? CarrierSenseChanged;
        public ValueTask<float> ReadRssiDbmAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(-120f);
        public ValueTask SetTransmitterAsync(bool transmit, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void Raise(bool busy, DateTimeOffset at) => CarrierSenseChanged?.Invoke(this, new CarrierSenseChange(busy, at));
    }
}