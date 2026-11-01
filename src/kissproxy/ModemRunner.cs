using kissproxylib;
using System.Collections.Concurrent;

namespace kissproxy;

/// <summary>
/// Owns the running proxy for each configured modem: starts one per modem, stops it when
/// the modem is deleted, and restarts it when a setting it only reads at start changes.
/// </summary>
internal class ModemRunner(ConfigManager configManager, ModemStateManager stateManager, LinkMonitor linkMonitor, Action<string> logInfo, CancellationToken shutdown)
{
    private record Running(KissProxy Proxy, Config Config, CancellationTokenSource Cts, Task Task);

    private readonly ConcurrentDictionary<string, Running> running = new();
    private readonly SemaphoreSlim changeLock = new(1, 1);

    /// <summary>The running proxies by modem id, for the web API.</summary>
    public ConcurrentDictionary<string, KissProxy> Proxies { get; } = new();

    public void Start(Config config)
    {
        if (shutdown.IsCancellationRequested)
            return;

        var state = stateManager.GetOrCreate(config.Id);
        var proxy = new KissProxy(config.Id, new ConsoleLogger(config.Id)) { LinkMonitor = linkMonitor };
        var cts = CancellationTokenSource.CreateLinkedTokenSource(shutdown);

        // Global settings (MQTT etc.) are read as the proxy starts
        var globalConfig = configManager.Config;
        var task = Task.Run(async () =>
        {
            try
            {
                await proxy.Run(config, globalConfig, state, cts.Token);
            }
            catch (Exception ex)
            {
                logInfo($"ERROR: Modem {config.Id} proxy error: {ex.Message}");
            }
        });

        running[config.Id] = new Running(proxy, config, cts, task);
        Proxies[config.Id] = proxy;
        logInfo($"Started proxy for modem '{config.Id}' on port {config.TcpPort}");
    }

    /// <summary>Handles a modem being added, changed or deleted in the config.</summary>
    public async Task OnModemConfigChangedAsync(string id)
    {
        await changeLock.WaitAsync();
        try
        {
            var config = configManager.GetModem(id);
            running.TryGetValue(id, out var current);

            if (config == null)
            {
                if (current != null)
                {
                    await StopAsync(id, current);
                    logInfo($"Stopped proxy for deleted modem '{id}'");
                }

                // Here rather than in the delete request, so a quick re-add (handled after
                // this, in order) gets a fresh state instead of losing its proxy's one
                stateManager.Remove(id);
            }
            else if (current == null)
            {
                Start(config);
            }
            else if (NeedsRestart(current.Config, config))
            {
                logInfo($"Restarting proxy for modem '{id}' to apply its port settings");
                await StopAsync(id, current);
                Start(config);
            }
            else
            {
                // Filters etc. take effect at once; parameters reach the modem only on Apply
                current.Proxy.UpdateConfig(config);
                running[id] = current with { Config = config };
            }
        }
        catch (Exception ex)
        {
            logInfo($"ERROR: Applying config change for modem '{id}' failed: {ex.Message}");
        }
        finally
        {
            changeLock.Release();
        }
    }

    /// <summary>Waits for every proxy to finish (after shutdown has been requested).</summary>
    public Task WhenAllStopped() => Task.WhenAll(running.Values.Select(r => r.Task));

    private async Task StopAsync(string id, Running current)
    {
        running.TryRemove(id, out _);
        Proxies.TryRemove(id, out _);
        current.Cts.Cancel();
        try { await current.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (TimeoutException) { logInfo($"WARN: Proxy for modem '{id}' took more than 10 s to stop"); }
        current.Cts.Dispose();
    }

    // Settings the proxy reads only when it starts (the serial port and TCP listener, MQTT topics)
    private static bool NeedsRestart(Config old, Config updated) =>
        old.ComPort != updated.ComPort
        || old.Baud != updated.Baud
        || old.TcpPort != updated.TcpPort
        || old.AnyHost != updated.AnyHost
        || old.MqttTopicPrefix != updated.MqttTopicPrefix
        || old.Base64 != updated.Base64;
}
