using kissproxy;
using kissproxylib;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

// Determine config file path
string configPath = File.Exists("/etc/kissproxy.conf")
    ? "/etc/kissproxy.conf"
    : "kissproxy.conf";

// Load configuration (create default if doesn't exist)
var configManager = new ConfigManager(configPath);
GlobalConfig config;
try
{
    if (!File.Exists(configPath))
    {
        // Create default config file
        var defaultConfig = new GlobalConfig
        {
            WebPort = 8080,
            Password = "changeme",
            Modems = []
        };
        var defaultJson = System.Text.Json.JsonSerializer.Serialize(defaultConfig, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
        });
        File.WriteAllText(configPath, defaultJson);
        LogInfo($"Created default configuration at {configPath}");
    }

    config = configManager.Load();
    LogInfo($"Loaded configuration from {configPath}");
}
catch (Exception ex)
{
    LogError($"Error loading config: {ex.Message}");
    return 1;
}

// Check if we have any modems configured
if (config.Modems.Count == 0)
{
    LogInfo("No modems configured. Starting web UI for initial setup.");
}

// Check config writeability
if (configManager.IsWriteable)
{
    LogInfo("Config file is writeable");
}
else
{
    LogWarn($"Config file is NOT writeable: {configPath}");
}

// Check if password needs to be set
if (configManager.NeedsMigration)
{
    LogWarn("No web UI password set: the first login to the web UI sets it");
}

// Create state manager
var stateManager = new ModemStateManager();

// Create proxy instances dictionary
var proxyInstances = new Dictionary<string, KissProxy>();

// Cancellation token for graceful shutdown
var cts = new CancellationTokenSource();

// Start web server
var builder = WebApplication.CreateBuilder();
builder.Services.AddLogging(logging =>
{
    logging.ClearProviders();
    logging.AddConsole();
    logging.SetMinimumLevel(LogLevel.Information);
    // Per-request lines from the web UI's polling would bury the frame log
    logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
});
// Reduce shutdown timeout so SSE connections don't block exit for 30 seconds
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(5));

var app = builder.Build();

// Wire ASP.NET Core shutdown lifecycle to our cancellation token (and vice versa).
// This ensures: Ctrl-C stops proxy tasks, and cts.Cancel() from any source stops the web host.
var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
lifetime.ApplicationStopping.Register(() =>
{
    cts.Cancel();
    LogInfo("Shutdown requested...");
});
cts.Token.Register(() => lifetime.StopApplication());

// Serve static files from wwwroot
var wwwrootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot");
if (Directory.Exists(wwwrootPath))
{
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new PhysicalFileProvider(wwwrootPath)
    });
    app.MapFallbackToFile("index.html");
}
else
{
    // Development: try to find wwwroot relative to source
    var devWwwroot = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
    if (Directory.Exists(devWwwroot))
    {
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(devWwwroot)
        });
        app.MapFallbackToFile("index.html");
    }
    else
    {
        LogWarn($"wwwroot directory not found at {wwwrootPath} or {devWwwroot}");
    }
}

// Map API endpoints
WebApi.MapEndpoints(app, configManager, stateManager, proxyInstances);

// Start modem proxy tasks
var proxyTasks = new List<Task>();
foreach (var modemConfig in config.Modems)
{
    var state = stateManager.GetOrCreate(modemConfig.Id);
    var logger = new ConsoleLogger(modemConfig.Id);
    var proxy = new KissProxy(modemConfig.Id, logger);

    proxyInstances[modemConfig.Id] = proxy;

    var task = Task.Run(async () =>
    {
        try
        {
            await proxy.Run(modemConfig, config, state, cts.Token);
        }
        catch (Exception ex)
        {
            LogError($"Modem {modemConfig.Id} proxy error: {ex.Message}");
        }
    });
    proxyTasks.Add(task);

    LogInfo($"Started proxy for modem '{modemConfig.Id}' on port {modemConfig.TcpPort}");
}

// Subscribe to config changes: update existing proxies, or start a new one for newly-added modems
configManager.ModemConfigChanged += modemId =>
{
    var newConfig = configManager.GetModem(modemId);
    if (newConfig == null)
        return; // Modem was deleted — no action needed (existing proxy will keep running until restart)

    if (proxyInstances.TryGetValue(modemId, out var proxy))
    {
        // Existing proxy — update in-memory config only; do not send params to modem.
        // Params are sent only on explicit Apply Now (POST .../apply).
        proxy.UpdateConfig(newConfig);
    }
    else
    {
        // New modem added via web UI — start a proxy task for it
        var state = stateManager.GetOrCreate(modemId);
        var newProxyLogger = new ConsoleLogger(modemId);
        var newProxy = new KissProxy(modemId, newProxyLogger);
        proxyInstances[modemId] = newProxy;

        // Capture current global config (MQTT settings etc.) at the moment the proxy starts
        var globalConfig = configManager.Config;
        var task = Task.Run(async () =>
        {
            try
            {
                await newProxy.Run(newConfig, globalConfig, state, cts.Token);
            }
            catch (Exception ex)
            {
                LogError($"Modem {modemId} proxy error: {ex.Message}");
            }
        });
        proxyTasks.Add(task);

        LogInfo($"Started proxy for new modem '{modemId}' on port {newConfig.TcpPort}");
    }
};

// Run web server
var webUrl = $"http://0.0.0.0:{config.WebPort}";
LogInfo($"Starting web server on {webUrl}");

try
{
    await app.RunAsync(webUrl);
}
catch (Exception ex)
{
    LogError($"Web server error: {ex.Message}");
    cts.Cancel();
}

// Wait for proxy tasks to complete
await Task.WhenAll(proxyTasks);

return 0;

// Helper methods
static void LogInfo(string message) => Console.WriteLine($"{DateTime.UtcNow:HH:mm:ss.ff}Z  {message}");
static void LogWarn(string message) => Console.WriteLine($"{DateTime.UtcNow:HH:mm:ss.ff}Z  WARN: {message}");
static void LogError(string message) => Console.WriteLine($"{DateTime.UtcNow:HH:mm:ss.ff}Z  ERROR: {message}");

/// <summary>
/// Simple console logger implementation.
/// </summary>
class ConsoleLogger : ILogger
{
    private readonly string instanceName;
    private readonly LogLevel minimumLevel;
    private string? scopeName;

    public ConsoleLogger(string instanceName = "", LogLevel minimumLevel = LogLevel.Information)
    {
        this.instanceName = instanceName;
        this.minimumLevel = minimumLevel;
    }

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
            return;

        var name = scopeName ?? instanceName;
        var prefix = string.IsNullOrEmpty(name) ? "" : $"[{name}] ";
        var message = formatter(state, exception);

        var levelStr = logLevel switch
        {
            LogLevel.Error or LogLevel.Critical => "ERROR",
            LogLevel.Warning => "WARN",
            LogLevel.Debug => "DEBUG",
            _ => "INFO"
        };

        if (logLevel >= LogLevel.Warning)
        {
            Console.WriteLine($"{DateTime.UtcNow:HH:mm:ss.ff}Z  {levelStr}: {prefix}{message}");
        }
        else
        {
            Console.WriteLine($"{DateTime.UtcNow:HH:mm:ss.ff}Z  {prefix}{message}");
        }
    }

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= minimumLevel;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
    {
        scopeName = state.ToString();
        return new ScopeDisposable(() => scopeName = null);
    }

    private class ScopeDisposable(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }
}
