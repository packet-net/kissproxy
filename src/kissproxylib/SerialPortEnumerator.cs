using System.IO.Ports;
using System.Runtime.InteropServices;

namespace kissproxylib;

/// <summary>
/// Information about a discovered serial port.
/// </summary>
/// <param name="Path">The path to use for opening the port (by-path on Linux, COM name on Windows)</param>
/// <param name="DisplayName">Human-readable display name</param>
/// <param name="ResolvedPath">The resolved device path (e.g., /dev/ttyACM0 on Linux)</param>
public record SerialPortInfo(string Path, string DisplayName, string? ResolvedPath);

/// <summary>
/// Cross-platform serial port enumeration for NinoTNC devices.
/// </summary>
public static class SerialPortEnumerator
{
    private const string LinuxByPathDir = "/dev/serial/by-path";
    private const string LinuxTtyAcmPrefix = "/dev/ttyACM";

    /// <summary>
    /// Enumerates available serial ports that could be NinoTNC devices.
    /// On Linux: filters to ttyACM devices via /dev/serial/by-path
    /// On Windows: returns all COM ports
    /// </summary>
    public static IEnumerable<SerialPortInfo> EnumerateNinoTncPorts()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return EnumerateLinuxPorts();
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return EnumerateWindowsPorts();
        }
        else
        {
            return EnumerateFallbackPorts();
        }
    }

    /// <summary>
    /// Enumerates all available serial ports (no filtering).
    /// </summary>
    public static IEnumerable<SerialPortInfo> EnumerateAllPorts()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return EnumerateAllLinuxPorts();
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return EnumerateWindowsPorts();
        }
        else
        {
            return EnumerateFallbackPorts();
        }
    }

    private static List<SerialPortInfo> EnumerateLinuxPorts()
    {
        var results = new List<SerialPortInfo>();

        if (!Directory.Exists(LinuxByPathDir))
        {
            return results;
        }

        foreach (var symlink in Directory.GetFiles(LinuxByPathDir))
        {
            try
            {
                var linkTarget = File.ResolveLinkTarget(symlink, returnFinalTarget: true);
                var resolved = linkTarget?.FullName;

                if (resolved != null && resolved.StartsWith(LinuxTtyAcmPrefix))
                {
                    var pathName = Path.GetFileName(symlink);
                    var ttyName = Path.GetFileName(resolved);
                    results.Add(new SerialPortInfo(
                        Path: symlink,
                        DisplayName: $"{ttyName} ({pathName})",
                        ResolvedPath: resolved
                    ));
                }
            }
            catch
            {
                // Skip symlinks we can't resolve
            }
        }

        return results;
    }

    private static List<SerialPortInfo> EnumerateAllLinuxPorts()
    {
        var results = new List<SerialPortInfo>();

        if (!Directory.Exists(LinuxByPathDir))
        {
            return results;
        }

        foreach (var symlink in Directory.GetFiles(LinuxByPathDir))
        {
            try
            {
                var linkTarget = File.ResolveLinkTarget(symlink, returnFinalTarget: true);
                var resolved = linkTarget?.FullName;

                if (resolved != null)
                {
                    var pathName = Path.GetFileName(symlink);
                    var ttyName = Path.GetFileName(resolved);
                    results.Add(new SerialPortInfo(
                        Path: symlink,
                        DisplayName: $"{ttyName} ({pathName})",
                        ResolvedPath: resolved
                    ));
                }
            }
            catch
            {
                // Skip symlinks we can't resolve
            }
        }

        return results;
    }

    private static List<SerialPortInfo> EnumerateWindowsPorts()
    {
        var results = new List<SerialPortInfo>();

        try
        {
            var ports = SerialPort.GetPortNames();
            foreach (var port in ports.OrderBy(p => p))
            {
                results.Add(new SerialPortInfo(
                    Path: port,
                    DisplayName: port,
                    ResolvedPath: port
                ));
            }
        }
        catch
        {
            // Return empty list if we can't enumerate ports
        }

        return results;
    }

    private static List<SerialPortInfo> EnumerateFallbackPorts()
    {
        var results = new List<SerialPortInfo>();

        try
        {
            var ports = SerialPort.GetPortNames();
            foreach (var port in ports.OrderBy(p => p))
            {
                results.Add(new SerialPortInfo(
                    Path: port,
                    DisplayName: port,
                    ResolvedPath: port
                ));
            }
        }
        catch
        {
            // Return empty list if we can't enumerate ports
        }

        return results;
    }
}
