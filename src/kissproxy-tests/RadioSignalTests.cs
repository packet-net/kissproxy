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
        var links = new LinkMonitor(store, "GB7RDG", TimeSpan.Zero);
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