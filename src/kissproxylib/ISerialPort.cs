using System.IO.Ports;

namespace kissproxylib;

public interface ISerialPort : IDisposable
{
    void Open();
    void Close();
    int Read(byte[] buffer, int offset, int count);
    int ReadByte();
    void Write(byte[] buffer, int offset, int count);
    void DiscardInBuffer();
    int BytesToRead { get; }
    int ReadTimeout { get; set; }
}

public class RealSerialPort(string port, int baud) : ISerialPort
{
    // A TNC that stops draining must fail the write (and so reopen the port), not hang
    // every writer behind the serial write lock
    private readonly SerialPort serialPort = new(port, baud) { WriteTimeout = 2000 };
    public void Close() => serialPort.Close();
    public void Open() => serialPort.Open();
    public int Read(byte[] buffer, int offset, int count) => serialPort.Read(buffer, offset, count);
    public int ReadByte() => serialPort.ReadByte();
    public void Write(byte[] buffer, int offset, int count) => serialPort.Write(buffer, offset, count);
    public void Dispose() => serialPort.Dispose();
    public void DiscardInBuffer() => serialPort.DiscardInBuffer();
    public int BytesToRead => serialPort.BytesToRead;
    public int ReadTimeout
    {
        get => serialPort.ReadTimeout;
        set => serialPort.ReadTimeout = value;
    }
}
