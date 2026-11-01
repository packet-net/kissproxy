using AwesomeAssertions;
using kissproxylib;

namespace kissproxy_tests;

public class KissHelpersTests
{
    private const byte FEND = 0xc0;

    [Fact]
    public void DiscardsRepeatedFENDs()
    {
        byte[]? frame = null;
        List<byte> bytes = [FEND];
        KissHelpers.ProcessBuffer(bytes, FEND, f => frame = f);
        bytes.Should().Equal(FEND);
        frame.Should().BeNull();
    }

    [Fact]
    public void ProcessesFrame()
    {
        byte[]? frame = null;
        List<byte> buffer = [FEND, 0x11];
        KissHelpers.ProcessBuffer(buffer, FEND, f => frame = f);
        buffer.Should().BeEmpty();
        frame.Should().Equal(FEND, 0x11, FEND);
    }

    [Fact]
    public void ProcessesSingleDelimitedFrames()
    {
        List<byte[]> frames = [];

        List<byte> buffer = [FEND, 0x11];
        KissHelpers.ProcessBuffer(buffer, FEND, frames.Add);
        KissHelpers.ProcessBuffer(buffer, 0x12, frames.Add);
        KissHelpers.ProcessBuffer(buffer, 0x13, frames.Add);
        KissHelpers.ProcessBuffer(buffer, FEND, frames.Add);

        buffer.Should().BeEmpty();
        frames.Should().HaveCount(2);
        frames[0].Should().Equal(FEND, 0x11, FEND);
        frames[1].Should().Equal(FEND, 0x12, 0x13, FEND);
    }

    [Fact]
    public void ProcessesDoubleDelimitedFrames()
    {
        List<byte[]> frames = [];

        List<byte> buffer = [FEND, 0x11];
        KissHelpers.ProcessBuffer(buffer, FEND, frames.Add);
        KissHelpers.ProcessBuffer(buffer, FEND, frames.Add);
        KissHelpers.ProcessBuffer(buffer, 0x12, frames.Add);
        KissHelpers.ProcessBuffer(buffer, 0x13, frames.Add);
        KissHelpers.ProcessBuffer(buffer, FEND, frames.Add);

        buffer.Should().BeEmpty();
        frames.Should().HaveCount(2);
        frames[0].Should().Equal(FEND, 0x11, FEND);
        frames[1].Should().Equal(FEND, 0x12, 0x13, FEND);
    }
}