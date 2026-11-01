using AwesomeAssertions;
using kissproxylib;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Sockets;
using Xunit.Abstractions;

namespace kissproxy_tests;

public class ProxyTests(ITestOutputHelper testOutputHelper)
{
    private const byte FEND = 0xc0;

    [Fact]
    public async Task NodeThatReconnectsIsServedAgain()
    {
        var factory = new MockSerialPortFactory();
        var port = FreeTcpPort();
        using var cts = new CancellationTokenSource();
        var target = new KissProxy("test", new TestLogger(testOutputHelper), factory);
        var runner = Task.Run(() => target.Run("fake", cts.Token, 57600, port));

        using (var first = await ConnectWithRetry(port))
        {
            first.GetStream().Write([FEND, 0x00, 0x11, FEND]);
            await WaitFor(() => factory.Ports.Sum(p => p.FramesWritten) >= 1);
        } // clean close: the node read returns 0

        using (var second = await ConnectWithRetry(port))
        {
            second.GetStream().Write([FEND, 0x00, 0x12, FEND]);
            await WaitFor(() => factory.Ports.Sum(p => p.FramesWritten) >= 2);
        }

        factory.Ports.Sum(p => p.FramesWritten).Should().Be(2);
        cts.Cancel();
        await runner;
    }

    [Fact]
    public async Task LostSerialPortIsReopened()
    {
        var factory = new MockSerialPortFactory();
        var port = FreeTcpPort();
        using var cts = new CancellationTokenSource();
        var target = new KissProxy("test", new TestLogger(testOutputHelper), factory);
        var runner = Task.Run(() => target.Run("fake", cts.Token, 57600, port));

        using var node = await ConnectWithRetry(port);
        await WaitFor(() => factory.Ports.Count == 1);

        factory.Ports[0].Unplug();
        await WaitFor(() => factory.Ports.Count == 2, TimeSpan.FromSeconds(10));

        // The node was dropped with the old port and can reconnect to the new one
        using var again = await ConnectWithRetry(port);
        again.GetStream().Write([FEND, 0x00, 0x11, FEND]);
        await WaitFor(() => factory.Ports[1].FramesWritten == 1);

        cts.Cancel();
        await runner;
    }

    private static int FreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<TcpClient> ConnectWithRetry(int port)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            var client = new TcpClient();
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, port);
                return client;
            }
            catch (SocketException) when (DateTime.UtcNow < deadline)
            {
                client.Dispose();
                await Task.Delay(50);
            }
        }
    }

    private static async Task WaitFor(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition not met in time");
            await Task.Delay(20);
        }
    }
}

internal class TestLogger(ITestOutputHelper testOutputHelper) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        try { testOutputHelper.WriteLine(formatter(state, exception)); }
        catch (InvalidOperationException) { } // logging after the test finished
    }
}

internal class MockSerialPortFactory : ISerialPortFactory
{
    private readonly List<FakeSerialPort> ports = [];

    public List<FakeSerialPort> Ports
    {
        get { lock (ports) return [.. ports]; }
    }

    public ISerialPort Create(string comPort, int baud)
    {
        var port = new FakeSerialPort();
        lock (ports) ports.Add(port);
        return port;
    }
}

internal class FakeSerialPort : ISerialPort
{
    private volatile bool unplugged;
    private int framesWritten;

    public int FramesWritten => Volatile.Read(ref framesWritten);

    public void Unplug() => unplugged = true;

    public void Close() { }
    public void DiscardInBuffer() { }
    public void Dispose() { }
    public void Open() { }

    public int Read(byte[] buffer, int offset, int count)
    {
        Thread.Sleep(20);
        if (unplugged)
            throw new IOException("Device removed");
        throw new TimeoutException();
    }

    public int ReadByte() => Read([], 0, 1);

    public void Write(byte[] bytes, int offset, int count)
    {
        if (unplugged)
            throw new IOException("Device removed");

        // Count data frames from the node; parameter frames sent on open are command != 0
        if (count >= 3 && bytes[offset] == 0xC0 && (bytes[offset + 1] & 0x0F) == 0)
            Interlocked.Increment(ref framesWritten);
    }

    public int BytesToRead => 0;
    public int ReadTimeout { get; set; } = -1;
}
