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
            Password = "",  // set by the first login to the web UI
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

// Cancellation token for graceful shutdown
var cts = new CancellationTokenSource();

// Frame history for the link timeline: alongside the config when run from source
var frameDbPath = configPath == "/etc/kissproxy.conf" ? "/var/lib/kissproxy/frames.db" : "kissproxy-frames.db";
var frameStore = new FrameStore(frameDbPath, new ConsoleLogger());
var linkMonitor = new LinkMonitor(frameStore, configManager.Config.MyCallsign);
configManager.GlobalConfigChanged += () => linkMonitor.MyCallsign = configManager.Config.MyCallsign;
LogInfo(frameStore.IsPersistent ? $"Frame history in {frameDbPath}" : "Frame history in memory only");

var modemRunner = new ModemRunner(configManager, stateManager, linkMonitor, LogInfo, cts.Token);

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

// The timeline's JSON is a few hundred KB; it compresses well
builder.Services.AddResponseCompression(o => o.MimeTypes = ["application/json"]);

var app = builder.Build();
app.UseResponseCompression();

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
WebApi.MapEndpoints(app, configManager, stateManager, modemRunner.Proxies, linkMonitor);

// Start modem proxy tasks
foreach (var modemConfig in configManager.Modems)
{
    modemRunner.Start(modemConfig);
}

// Modems added, changed or deleted via the web UI
configManager.ModemConfigChanged += modemId => _ = modemRunner.OnModemConfigChangedAsync(modemId);

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

// Wait for proxy tasks to complete, then write out the last of the frame history
await modemRunner.WhenAllStopped();
frameStore.Dispose();

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
