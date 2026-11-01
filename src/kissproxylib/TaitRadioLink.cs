using M0LTE.Tait.Ccdi;
using Microsoft.Extensions.Logging;

namespace kissproxylib;

/// <summary>
/// Keeps a modem's Tait radio connected over CCDI and its RSSI sampled: opens the port, turns on
/// carrier-sense reports, runs a <see cref="RadioSignalMonitor"/>, and reopens with backoff when
/// the radio is unplugged or stops answering. Frames get no radio metadata while it is down.
/// </summary>
public sealed class TaitRadioLink : IAsyncDisposable
{
    // RSSI reads failing this many times in a row (about 2 s while busy) means the radio has gone
    private const int MaxConsecutiveFailures = 50;

    private readonly string modem;
    private readonly string port;
    private readonly int baud;
    private readonly ILogger logger;
    private readonly Action<NoiseSample>? onNoiseSample;
    private readonly CancellationTokenSource cts = new();
    private readonly Task loop;
    private volatile RadioSignalMonitor? monitor;
    private volatile string status = "connecting";

    public TaitRadioLink(string modem, string port, int baud, ILogger logger, Action<NoiseSample>? onNoiseSample)
    {
        this.modem = modem;
        this.port = port;
        this.baud = baud;
        this.logger = logger;
        this.onNoiseSample = onNoiseSample;
        loop = Task.Run(() => RunAsync(cts.Token));
    }

    /// <summary>The radio's state for the web UI, e.g. "connected: Tait TM8110, noise -121 dBm".</summary>
    public string Status => monitor is { NoiseFloorDbm: { } nf } ? $"{status}, noise floor {nf:F0} dBm" : status;

    /// <summary>Signal readings for a received frame, or null while the radio is down.</summary>
    public RadioMetadata? Attribute(DateTime receivedAt, int ax25Length, int? bitRateHz) =>
        monitor?.Attribute(receivedAt, ax25Length, bitRateHz);

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        int failures = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            TaitCcdiRadio? radio = null;
            RadioSignalMonitor? m = null;
            try
            {
                radio = TaitCcdiRadio.Open(port, baud);
                await radio.SetProgressMessagesAsync(true, cancellationToken); // carrier-sense edges
                var identity = await radio.QueryIdentityAsync(cancellationToken);

                m = new RadioSignalMonitor(radio, modem, onNoiseSample);
                monitor = m;
                status = $"connected: {identity.ProductName}";
                logger.LogInformation("Tait radio on {port}: {product}", port, identity.ProductName);
                failures = 0;

                while (!cancellationToken.IsCancellationRequested && m.ConsecutiveFailures < MaxConsecutiveFailures)
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);

                if (!cancellationToken.IsCancellationRequested)
                    logger.LogWarning("Tait radio on {port} stopped answering; reopening", port);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                if (failures == 0)
                    logger.LogWarning("Tait radio on {port}: {error}", port, ex.Message);
                status = $"not connected: {ex.Message}";
            }
            finally
            {
                monitor = null;
                if (m != null)
                    await m.DisposeAsync();
                if (radio != null)
                {
                    try { await radio.DisposeAsync(); } catch { }
                }
            }

            // 5 s, 10 s, 20 s, 40 s, then a minute between attempts
            var delay = TimeSpan.FromSeconds(Math.Min(5 * (1 << Math.Min(failures, 4)), 60));
            failures++;
            try { await Task.Delay(delay, cancellationToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    public async ValueTask DisposeAsync()
    {
        cts.Cancel();
        try { await loop; } catch { }
        cts.Dispose();
    }
}
