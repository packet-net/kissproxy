using AwesomeAssertions;
using kissproxylib;

namespace kissproxy_tests;

public class Ax25DecoderTests
{
    // Builds dest + source with the C/R bits for a command (or response) and the given control byte.
    private static List<byte> Header(string source, string dest, bool command, byte control, params string[] digis)
    {
        var frame = new List<byte>();
        var d = KissFrameBuilder.EncodeCallsign(dest, false);
        var s = KissFrameBuilder.EncodeCallsign(source, digis.Length == 0);
        if (command) d[6] |= 0x80; else s[6] |= 0x80;
        frame.AddRange(d);
        frame.AddRange(s);
        for (int i = 0; i < digis.Length; i++)
            frame.AddRange(KissFrameBuilder.EncodeCallsign(digis[i], i == digis.Length - 1));
        frame.Add(control);
        return frame;
    }

    private static string Kiss(List<byte> ax25) =>
        Ax25Decoder.DescribeKissFrame(KissFrameBuilder.BuildKissDataFrame([.. ax25]))!;

    [Fact]
    public void RrPollCommand()
    {
        // RR, P set, N(R)=3: 011 1 0001
        Kiss(Header("GB7WIN", "M0LTE", command: true, 0x71)).Should().Be("GB7WIN>M0LTE <RR C P R3>");
    }

    [Fact]
    public void RrFinalResponse()
    {
        Kiss(Header("M0LTE", "GB7WIN", command: false, 0x91)).Should().Be("M0LTE>GB7WIN <RR R F R4>");
    }

    [Theory]
    [InlineData((byte)0x53, true, "<DISC C P>")]
    [InlineData((byte)0x73, false, "<UA R F>")]
    [InlineData((byte)0x3F, true, "<SABM C P>")]
    [InlineData((byte)0x1F, false, "<DM R F>")]
    [InlineData((byte)0x85, false, "<RNR R R4>")]
    [InlineData((byte)0x29, true, "<REJ C R1>")]
    public void UnnumberedAndSupervisory(byte control, bool command, string expected)
    {
        Kiss(Header("GB7WIN", "GB7RDG", command, control)).Should().EndWith(" " + expected);
    }

    [Fact]
    public void IFrameWithText()
    {
        // I, N(S)=2, N(R)=3, no poll: 011 0 010 0
        var frame = Header("M0LTE", "GB7WIN", command: true, 0x64);
        frame.Add(0xF0);
        frame.AddRange("BYE\r"u8.ToArray());
        Kiss(frame).Should().Be("M0LTE>GB7WIN <I C S2 R3> pid=F0 len=4: BYE\\r");
    }

    [Fact]
    public void LongTextIsTruncatedAndNonPrintablesShownAsDots()
    {
        var frame = Header("M0LTE", "GB7WIN", command: true, 0x00);
        frame.Add(0xF0);
        frame.AddRange([0x01, 0xFF]);
        frame.AddRange(Enumerable.Repeat((byte)'x', 100));
        var line = Kiss(frame);
        line.Should().Contain("len=102: ..xxx");
        line.Should().EndWith("...");
        line.Length.Should().BeLessThan(140);
    }

    [Fact]
    public void UiWithDigipeaterPath()
    {
        var frame = Header("M0LTE-9", "ID", command: true, 0x03, "GB7RDG");
        frame[^2] |= 0x80; // GB7RDG has repeated it
        frame.Add(0xF0);
        frame.AddRange("hi"u8.ToArray());
        Kiss(frame).Should().Be("M0LTE-9>ID,GB7RDG* <UI C> pid=F0 len=2: hi");
    }

    [Fact]
    public void XidParametersAreDecoded()
    {
        // What BPQ sent in the GB7WIN trace: classes, HDLC options, N1 = 256 bytes, k = 7.
        var frame = Header("GB7WIN", "GB7RDG", command: true, 0xBF);
        byte[] parameters = [2, 2, 0x00, 0x21, 3, 3, 0x22, 0xA4, 0x86, 6, 2, 0x08, 0x00, 8, 1, 7];
        frame.AddRange([0x82, 0x80, 0x00, (byte)parameters.Length]);
        frame.AddRange(parameters);
        Kiss(frame).Should().Be("GB7WIN>GB7RDG <XID C P> N1=256 k=7");
    }

    [Fact]
    public void NetRomNodesBroadcast()
    {
        var frame = Header("GB7RDG", "NODES", command: true, 0x03);
        frame.Add(0xCF);
        frame.Add(0xFF);
        frame.AddRange("RDG   "u8.ToArray());
        frame.AddRange(new byte[21 * 3]);
        Kiss(frame).Should().Be("GB7RDG>NODES <UI C> NET/ROM NODES RDG routes=3");
    }

    [Fact]
    public void NetRomInfo()
    {
        var frame = Header("GB7RDG", "GB7WIN", command: true, 0x00);
        frame.Add(0xCF);
        frame.AddRange(KissFrameBuilder.EncodeCallsign("GB7RDG", false));
        frame.AddRange(KissFrameBuilder.EncodeCallsign("G8PZT", false));
        frame.AddRange([25, 0x03, 0x2D, 2, 1, 0x05]);
        frame.AddRange("hello"u8.ToArray());
        Kiss(frame).Should().Be("GB7RDG>GB7WIN <I C S0 R0> NET/ROM GB7RDG>G8PZT ttl=25 <INFO 03/2D S2 R1> len=5: hello");
    }

    [Fact]
    public void EscapedBytesAreUnescaped()
    {
        var frame = Header("M0LTE", "GB7WIN", command: true, 0x00);
        frame.Add(0xF0);
        frame.AddRange([0xC0, 0xDB]);
        Kiss(frame).Should().EndWith("pid=F0 len=2: ..");
    }

    [Fact]
    public void NonDataCommandsAreIgnored()
    {
        Ax25Decoder.DescribeKissFrame(KissFrameBuilder.BuildParameterFrame(KissFrameBuilder.CMD_TXDELAY, 10)).Should().BeNull();
    }

    [Fact]
    public void AckModeAckFromModem()
    {
        Ax25Decoder.DescribeKissFrame([0xC0, 0x0C, 0x12, 0x34, 0xC0]).Should().Be("ACKMODE ack seq=1234");
    }

    [Fact]
    public void KissPortIsShownWhenNonZero()
    {
        var ax25 = Header("GB7WIN", "M0LTE", command: true, 0x71);
        Ax25Decoder.DescribeKissFrame(KissFrameBuilder.BuildKissDataFrame([.. ax25], port: 2)).Should().Be("p2 GB7WIN>M0LTE <RR C P R3>");
    }

    [Fact]
    public void GarbageDoesNotThrow()
    {
        Ax25Decoder.DescribeKissFrame([0xC0, 0x00, 0x01, 0x02, 0xC0]).Should().StartWith("undecodable len=2");
    }
}
