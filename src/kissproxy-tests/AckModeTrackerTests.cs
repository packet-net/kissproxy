using AwesomeAssertions;
using kissproxylib;

namespace kissproxy_tests;

public class AckModeTrackerTests
{
    [Fact]
    public void TrackOutbound_ValidAckModeFrame_ReturnsTrue()
    {
        var tracker = new AckModeTracker();

        // Build ACKMODE frame: FEND CMD SEQHI SEQLO PAYLOAD... FEND
        var frame = new byte[] { 0xC0, 0x0C, 0x12, 0x34, 0x01, 0x02, 0x03, 0xC0 };
        var result = tracker.TrackOutbound(frame);

        result.Should().BeTrue();
    }

    [Fact]
    public void TrackOutbound_NonAckModeFrame_ReturnsFalse()
    {
        var tracker = new AckModeTracker();

        // Data frame (command 0)
        var frame = new byte[] { 0xC0, 0x00, 0x01, 0x02, 0x03, 0xC0 };
        var result = tracker.TrackOutbound(frame);

        result.Should().BeFalse();
    }

    [Fact]
    public void ProcessAck_MatchesPendingFrame_ReturnsTimingInfo()
    {
        var tracker = new AckModeTracker();
        tracker.SetMode(0);       // 9600 bps
        tracker.SetTxDelay(30);   // 300ms

        // Track outbound frame
        var outFrame = new byte[] { 0xC0, 0x0C, 0x12, 0x34, 0x01, 0x02, 0x03, 0xC0 };
        tracker.TrackOutbound(outFrame);

        // Simulate delay
        Thread.Sleep(50);

        // Process ACK
        var ackFrame = new byte[] { 0xC0, 0x0C, 0x12, 0x34, 0xC0 };
        var timing = tracker.ProcessAck(ackFrame);

        timing.Should().NotBeNull();
        timing!.SeqHi.Should().Be(0x12);
        timing.SeqLo.Should().Be(0x34);
        timing.SeqNumber.Should().Be(0x1234);
        timing.PayloadBytes.Should().Be(3); // 0x01, 0x02, 0x03
        timing.Mode.Should().Be(0);
        timing.ModeName.Should().Be("9600 GFSK AX.25");
        timing.BitRate.Should().Be(9600);
        timing.TxDelayUnits.Should().Be(30);
        timing.TxDelayMs.Should().Be(300);
        timing.TotalMs.Should().BeGreaterThan(40); // At least 50ms passed
    }

    [Fact]
    public void ProcessAck_NoMatchingPendingFrame_ReturnsNull()
    {
        var tracker = new AckModeTracker();

        // Process ACK without tracking outbound first
        var ackFrame = new byte[] { 0xC0, 0x0C, 0x12, 0x34, 0xC0 };
        var timing = tracker.ProcessAck(ackFrame);

        timing.Should().BeNull();
    }

    [Fact]
    public void ProcessAck_DifferentSequenceNumber_ReturnsNull()
    {
        var tracker = new AckModeTracker();

        // Track outbound frame with seq 0x1234
        var outFrame = new byte[] { 0xC0, 0x0C, 0x12, 0x34, 0x01, 0xC0 };
        tracker.TrackOutbound(outFrame);

        // Process ACK with different seq 0x5678
        var ackFrame = new byte[] { 0xC0, 0x0C, 0x56, 0x78, 0xC0 };
        var timing = tracker.ProcessAck(ackFrame);

        timing.Should().BeNull();
    }

    [Fact]
    public void ProcessAck_CalculatesTransmissionDuration_Correctly()
    {
        var tracker = new AckModeTracker();
        tracker.SetMode(0); // 9600 bps

        // 100 bytes payload = 800 bits / 9600 bps = 83.33ms
        var payload = new byte[100];
        // Frame: FEND + CMD + SEQHI + SEQLO + payload + FEND = 5 + payload.Length
        var outFrame = new byte[5 + payload.Length];
        outFrame[0] = 0xC0;  // FEND
        outFrame[1] = 0x0C;  // CMD (ACKMODE)
        outFrame[2] = 0x00;  // SEQHI
        outFrame[3] = 0x01;  // SEQLO
        Array.Copy(payload, 0, outFrame, 4, payload.Length);
        outFrame[^1] = 0xC0; // Final FEND

        tracker.TrackOutbound(outFrame);

        var ackFrame = new byte[] { 0xC0, 0x0C, 0x00, 0x01, 0xC0 };
        var timing = tracker.ProcessAck(ackFrame);

        timing.Should().NotBeNull();
        timing!.PayloadBytes.Should().Be(100);
        // 100 bytes * 8 bits / 9600 bps * 1000 = 83.33ms
        timing.TxDurationMs.Should().BeApproximately(83.33, 0.1);
    }

    [Theory]
    [InlineData(0, 9600, 50, 41.67)]      // 9600 GFSK: 50 bytes = 400 bits / 9600 = 41.67ms
    [InlineData(5, 3600, 50, 111.11)]     // 3600 QPSK: 50 bytes = 400 bits / 3600 = 111.11ms
    [InlineData(10, 1200, 50, 333.33)]    // 1200 BPSK: 50 bytes = 400 bits / 1200 = 333.33ms
    [InlineData(8, 300, 50, 1333.33)]     // 300 BPSK: 50 bytes = 400 bits / 300 = 1333.33ms
    public void ProcessAck_DifferentModes_CalculatesCorrectDuration(
        int mode, int expectedBitRate, int payloadBytes, double expectedDurationMs)
    {
        var tracker = new AckModeTracker();
        tracker.SetMode(mode);

        var payload = new byte[payloadBytes];
        // Frame: FEND + CMD + SEQHI + SEQLO + payload + FEND = 5 + payload.Length
        var outFrame = new byte[5 + payload.Length];
        outFrame[0] = 0xC0;  // FEND
        outFrame[1] = 0x0C;  // CMD (ACKMODE)
        outFrame[2] = 0xAA;  // SEQHI
        outFrame[3] = 0xBB;  // SEQLO
        Array.Copy(payload, 0, outFrame, 4, payload.Length);
        outFrame[^1] = 0xC0; // Final FEND

        tracker.TrackOutbound(outFrame);

        var ackFrame = new byte[] { 0xC0, 0x0C, 0xAA, 0xBB, 0xC0 };
        var timing = tracker.ProcessAck(ackFrame);

        timing.Should().NotBeNull();
        timing!.Mode.Should().Be(mode);
        timing.BitRate.Should().Be(expectedBitRate);
        timing.TxDurationMs.Should().BeApproximately(expectedDurationMs, 0.1);
    }

    [Fact]
    public void ProcessAck_WithTxDelay_IncludesInTiming()
    {
        var tracker = new AckModeTracker();
        tracker.SetMode(0);      // 9600 bps
        tracker.SetTxDelay(50);  // 50 units = 500ms

        var outFrame = new byte[] { 0xC0, 0x0C, 0x00, 0x01, 0x01, 0x02, 0x03, 0xC0 };
        tracker.TrackOutbound(outFrame);

        var ackFrame = new byte[] { 0xC0, 0x0C, 0x00, 0x01, 0xC0 };
        var timing = tracker.ProcessAck(ackFrame);

        timing.Should().NotBeNull();
        timing!.TxDelayUnits.Should().Be(50);
        timing.TxDelayMs.Should().Be(500);
    }

    [Fact]
    public void ProcessAck_CalculatesTxStartTime_FromTxEnd()
    {
        var tracker = new AckModeTracker();
        tracker.SetMode(0); // 9600 bps

        // 10 bytes payload = 80 bits / 9600 bps = 8.33ms
        var outFrame = new byte[] { 0xC0, 0x0C, 0x00, 0x01, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0xC0 };
        tracker.TrackOutbound(outFrame);

        var ackFrame = new byte[] { 0xC0, 0x0C, 0x00, 0x01, 0xC0 };
        var timing = tracker.ProcessAck(ackFrame);

        timing.Should().NotBeNull();
        timing!.TxStartUtc.Should().NotBeNull();
        timing.TxEndUtc.Should().BeAfter(timing.TxStartUtc!.Value);

        // TxStart should be approximately TxEnd - TxDuration
        var expectedStart = timing.TxEndUtc.AddMilliseconds(-timing.TxDurationMs!.Value);
        var actualStart = timing.TxStartUtc!.Value;
        (actualStart - expectedStart).TotalMilliseconds.Should().BeLessThan(1);
    }

    [Fact]
    public void ToJson_ProducesValidJson()
    {
        var timing = new AckModeTiming
        {
            SeqHi = 0x12,
            SeqLo = 0x34,
            PayloadBytes = 50,
            Mode = 0,
            ModeName = "9600 GFSK AX.25",
            BitRate = 9600,
            TxDelayUnits = 30,
            TxDelayMs = 300,
            TxDurationMs = 41.67,
            QueuedUtc = DateTime.Parse("2024-01-15T10:30:00Z"),
            TxStartUtc = DateTime.Parse("2024-01-15T10:30:00.300Z"),
            TxEndUtc = DateTime.Parse("2024-01-15T10:30:00.342Z"),
            TotalMs = 342
        };

        var json = timing.ToJson();

        json.Should().Contain("\"seqHi\":18");
        json.Should().Contain("\"seqLo\":52");
        json.Should().Contain("\"seqNumber\":4660");
        json.Should().Contain("\"payloadBytes\":50");
        json.Should().Contain("\"mode\":0");
        json.Should().Contain("\"modeName\":\"9600 GFSK AX.25\"");
        json.Should().Contain("\"bitRate\":9600");
        json.Should().Contain("\"txDelayUnits\":30");
        json.Should().Contain("\"txDelayMs\":300");
        json.Should().Contain("\"totalMs\":342");
    }

    [Fact]
    public void MultipleFrames_EachTrackedIndependently()
    {
        var tracker = new AckModeTracker();
        tracker.SetMode(0);
        tracker.SetTxDelay(30);

        // Track three frames
        var frame1 = new byte[] { 0xC0, 0x0C, 0x00, 0x01, 0x11, 0xC0 };
        var frame2 = new byte[] { 0xC0, 0x0C, 0x00, 0x02, 0x22, 0xC0 };
        var frame3 = new byte[] { 0xC0, 0x0C, 0x00, 0x03, 0x33, 0xC0 };

        tracker.TrackOutbound(frame1);
        tracker.TrackOutbound(frame2);
        tracker.TrackOutbound(frame3);

        // ACKs come back in different order
        var ack2 = new byte[] { 0xC0, 0x0C, 0x00, 0x02, 0xC0 };
        var timing2 = tracker.ProcessAck(ack2);
        timing2.Should().NotBeNull();
        timing2!.SeqLo.Should().Be(0x02);

        var ack1 = new byte[] { 0xC0, 0x0C, 0x00, 0x01, 0xC0 };
        var timing1 = tracker.ProcessAck(ack1);
        timing1.Should().NotBeNull();
        timing1!.SeqLo.Should().Be(0x01);

        var ack3 = new byte[] { 0xC0, 0x0C, 0x00, 0x03, 0xC0 };
        var timing3 = tracker.ProcessAck(ack3);
        timing3.Should().NotBeNull();
        timing3!.SeqLo.Should().Be(0x03);

        // Processing same ACK again should return null (already processed)
        var timing1Again = tracker.ProcessAck(ack1);
        timing1Again.Should().BeNull();
    }
}
