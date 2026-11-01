using kissproxy;
using kissproxylib;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace kissproxy;

/// <summary>
/// Web API endpoints for kissproxy management.
/// </summary>
public static class WebApi
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    // Compact JSON for SSE: each event must be a single line
    private static readonly JsonSerializerOptions SseJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    // A browser gets at most this many updates a second per modem, however busy the channel
    private static readonly TimeSpan SseMinInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Maps all API endpoints to the web application.
    /// </summary>
    public static void MapEndpoints(
        IEndpointRouteBuilder app,
        ConfigManager configManager,
        ModemStateManager stateManager,
        IReadOnlyDictionary<string, KissProxy> proxyInstances,
        LinkMonitor linkMonitor)
    {
        // Login endpoint (no auth required)
        app.MapPost("/api/login", (HttpContext ctx) => HandleLogin(ctx, configManager));

        // All other API endpoints require authentication
        var api = app.MapGroup("/api").AddEndpointFilter(async (context, next) =>
        {
            var httpContext = context.HttpContext;
            if (!IsAuthenticated(httpContext, configManager))
            {
                return Results.Unauthorized();
            }
            return await next(context);
        });

        // Config status
        api.MapGet("/config-status", () => new
        {
            writeable = configManager.IsWriteable,
            path = configManager.ConfigPath,
            needsMigration = configManager.NeedsMigration
        });

        // Modems - list all
        api.MapGet("/modems", () =>
        {
            var modems = configManager.Modems.Select(m => new
            {
                config = m,
                state = stateManager.GetSnapshot(m.Id)
            });
            return Results.Json(modems, JsonOptions);
        });

        // Modems - get single
        api.MapGet("/modems/{id}", (string id) =>
        {
            var config = configManager.GetModem(id);
            if (config == null)
                return Results.NotFound();

            return Results.Json(new
            {
                config,
                state = stateManager.GetSnapshot(id)
            }, JsonOptions);
        });

        // Modems - stream all modem state updates via Server-Sent Events (SSE)
        // Note: Auth is checked by the endpoint filter, but for SSE we also support query param
        app.MapGet("/api/modems/stream", async (HttpContext ctx, string? token, CancellationToken cancellationToken) =>
        {
            // Check authentication via query param (since EventSource doesn't support custom headers)
            if (!string.IsNullOrEmpty(token))
            {
                if (!configManager.ValidatePassword(token))
                {
                    ctx.Response.StatusCode = 401;
                    return;
                }
            }
            else if (!IsAuthenticated(ctx, configManager))
            {
                ctx.Response.StatusCode = 401;
                return;
            }

            ctx.Response.Headers.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers.Connection = "keep-alive";

            // StateChanged fires from several threads (serial reader, TCP reader) and concurrent
            // writes to the response body corrupt the stream, so one loop does all the writing.
            // Changes are coalesced: each modem marked dirty is sent once with its latest state,
            // at most every SseMinInterval, rather than one full snapshot per frame.
            var dirty = new ConcurrentDictionary<string, ModemState>();
            var wake = Channel.CreateBounded<bool>(
                new BoundedChannelOptions(1) { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });
            using var detailedTracking = stateManager.EnableDetailedFrameTracking();

            void OnStateChanged(string id, ModemState state)
            {
                dirty[id] = state;
                wake.Writer.TryWrite(true);
            }

            // Subscribe before taking the initial state, so no change falls in between
            // (a duplicate is harmless: coalescing sends it once)
            stateManager.StateChanged += OnStateChanged;

            foreach (var modem in configManager.Modems)
            {
                var state = stateManager.GetSnapshot(modem.Id);
                if (state != null)
                    OnStateChanged(modem.Id, state);
            }

            try
            {
                await foreach (var _ in wake.Reader.ReadAllAsync(cancellationToken))
                {
                    foreach (var id in dirty.Keys)
                    {
                        if (dirty.TryRemove(id, out var state))
                            await SendSseEvent(ctx, id, state, cancellationToken);
                    }
                    await Task.Delay(SseMinInterval, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown, don't log
            }
            finally
            {
                stateManager.StateChanged -= OnStateChanged;
                wake.Writer.TryComplete();
            }
        });

        // Modems - update
        api.MapPut("/modems/{id}", async (string id, HttpContext ctx) =>
        {
            try
            {
                var config = await ctx.Request.ReadFromJsonAsync<Config>(JsonOptions);
                if (config == null)
                    return Results.BadRequest("Invalid config");

                if (config.Id != id)
                    return Results.BadRequest("ID mismatch");

                // The running proxy picks this up via ModemConfigChanged (restarting it if a
                // port setting changed); parameters reach the modem only on an explicit Apply
                configManager.UpdateModem(config);

                return Results.Ok();
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
            catch (Exception ex)
            {
                return Results.BadRequest(ex.Message);
            }
        });

        // Modems - add new
        api.MapPost("/modems", async (HttpContext ctx) =>
        {
            try
            {
                var config = await ctx.Request.ReadFromJsonAsync<Config>(JsonOptions);
                if (config == null)
                    return Results.BadRequest("Invalid config");

                configManager.AddModem(config);
                return Results.Created($"/api/modems/{config.Id}", config);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(ex.Message);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(ex.Message);
            }
        });

        // Modems - delete
        api.MapDelete("/modems/{id}", (string id) =>
        {
            try
            {
                // The runner stops the proxy and drops its state (ModemConfigChanged)
                configManager.RemoveModem(id);
                return Results.Ok();
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
        });

        // Modems - apply config NOW (send params to modem)
        // Returns 202 Accepted if proxy not running yet (params will be sent when it starts)
        api.MapPost("/modems/{id}/apply", (string id) =>
        {
            var config = configManager.GetModem(id);
            if (config == null)
                return Results.NotFound();

            if (!proxyInstances.TryGetValue(id, out var proxy))
            {
                // Proxy not running - config will be applied when it starts
                return Results.Accepted(value: new { message = "Modem not running. Settings will be applied when modem starts." });
            }

            proxy.OnConfigChanged(config);
            return Results.Ok(new { message = "Settings applied to modem." });
        });

        // Modems - send test transmission (AX.25 UI frame)
        api.MapPost("/modems/{id}/transmit", async (string id, HttpContext ctx) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<TransmitRequest>(JsonOptions);
                if (request == null)
                    return Results.BadRequest(new { message = "Invalid request" });

                // Validate MyCallsign is configured
                var myCallsign = configManager.Config.MyCallsign;
                if (string.IsNullOrWhiteSpace(myCallsign))
                    return Results.BadRequest(new { message = "My Callsign is not configured in Global Settings" });

                // Validate destination callsign
                var toCallsign = string.IsNullOrWhiteSpace(request.ToCallsign) ? "CQ" : request.ToCallsign.Trim().ToUpper();

                // Validate message
                if (string.IsNullOrEmpty(request.Message))
                    return Results.BadRequest(new { message = "Message cannot be empty" });

                var messageBytes = Encoding.ASCII.GetBytes(request.Message);
                if (messageBytes.Length > 256)
                    return Results.BadRequest(new { message = "Message too long (max 256 bytes)" });

                // Find proxy
                if (!proxyInstances.TryGetValue(id, out var proxy))
                    return Results.NotFound(new { message = $"Modem '{id}' not found or not running" });

                // Build AX.25 UI frame and wrap in KISS
                var ax25Frame = KissFrameBuilder.BuildAx25UiFrame(myCallsign, toCallsign, messageBytes);
                var kissFrame = KissFrameBuilder.BuildKissDataFrame(ax25Frame);

                // Send to modem
                if (!proxy.SendRawFrame(kissFrame))
                    return Results.Json(new { message = "Modem serial port is not open" }, statusCode: 503);

                return Results.Ok(new { message = "Transmission sent" });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        // Save config to file
        api.MapPost("/save", () =>
        {
            try
            {
                configManager.Save();
                return Results.Ok();
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(ex.Message);
            }
        });

        // Serial ports enumeration
        api.MapGet("/serial-ports", () =>
        {
            var ports = SerialPortEnumerator.EnumerateNinoTncPorts().ToList();
            return Results.Json(ports, JsonOptions);
        });

        // NinoTNC modes
        api.MapGet("/nino-modes", () =>
        {
            var modes = KissFrameBuilder.NinoModes.Select(kv => new
            {
                mode = kv.Key,
                name = kv.Value
            });
            return Results.Json(modes, JsonOptions);
        });

        // Link timeline: one row per link (or heard-only station) for a window, default the last 24 h
        api.MapGet("/timeline", (long? from, long? to) =>
        {
            var now = DateTime.UtcNow;
            var (start, end) = Window(from, to, now);
            var timeline = TimelineBuilder.Build(linkMonitor, start, end, now);
            return Results.Json(new
            {
                from = FrameStore.ToMs(timeline.From),
                to = FrameStore.ToMs(timeline.To),
                now = FrameStore.ToMs(now),
                bucketMs = (long)timeline.Bucket.TotalMilliseconds,
                persistent = linkMonitor.Store.IsPersistent,
                rows = timeline.Rows.Select(r => new
                {
                    modem = r.Modem,
                    local = r.Local,
                    remote = r.Remote,
                    segments = r.Segments.Select(g => new { s = FrameStore.ToMs(g.Start), e = FrameStore.ToMs(g.End), state = g.State, detail = g.Detail, silent = g.Silent }),
                    markers = r.Markers.Select(m => new { t = FrameStore.ToMs(m.Time), kind = m.Kind, count = m.Count, details = m.Details }),
                    activity = r.Activity.Select(a => new { t = FrameStore.ToMs(a.Start), rx = a.Received, tx = a.Transmitted }),
                }),
            }, SseJsonOptions);
        });

        // Frames in a window, oldest first; local="" selects heard-only frames
        api.MapGet("/frames", (long? from, long? to, string? modem, string? local, string? remote, int? offset, int? limit) =>
        {
            var (start, end) = Window(from, to, DateTime.UtcNow);
            var frames = linkMonitor.Store.QueryFrames(start, end, modem, local, remote,
                Math.Max(0, offset ?? 0), Math.Clamp(limit ?? 500, 1, 2000), out var total);
            return Results.Json(new
            {
                total,
                frames = frames.Select(f =>
                {
                    var (frame, text) = SplitSummary(f.Summary);
                    return new
                    {
                        t = FrameStore.ToMs(f.Timestamp),
                        modem = f.Modem,
                        dir = f.Transmitted ? "TX" : "RX",
                        src = f.Source,
                        dest = f.Dest,
                        path = f.Path,
                        frame,
                        text,
                        snr = f.Snr,
                        raw = Convert.ToHexString(f.Raw),
                    };
                }),
            }, SseJsonOptions);
        });

        // Global config (web port, password, MQTT settings, callsign)
        api.MapGet("/global", () => new
        {
            webPort = configManager.Config.WebPort,
            hasPassword = !string.IsNullOrEmpty(configManager.Config.Password),
            myCallsign = configManager.Config.MyCallsign,
            mqttServer = configManager.Config.MqttServer,
            mqttUsername = configManager.Config.MqttUsername,
            hasMqttPassword = !string.IsNullOrEmpty(configManager.Config.MqttPassword)
        });

        api.MapPut("/global", async (HttpContext ctx) =>
        {
            try
            {
                var update = await ctx.Request.ReadFromJsonAsync<GlobalConfigUpdate>(JsonOptions);
                if (update == null)
                    return Results.BadRequest("Invalid update");

                configManager.UpdateGlobal(
                    update.WebPort,
                    update.Password,
                    update.MyCallsign,
                    update.MqttServer,
                    update.MqttUsername,
                    update.MqttPassword,
                    update.ClearMyCallsign,
                    update.ClearMqttServer,
                    update.ClearMqttUsername,
                    update.ClearMqttPassword);
                return Results.Ok();
            }
            catch (Exception ex)
            {
                return Results.BadRequest(ex.Message);
            }
        });
    }

    // A window within the store's retention: default the last 24 h, at least a minute
    private static (DateTime from, DateTime to) Window(long? from, long? to, DateTime now)
    {
        var end = to is { } t ? FrameStore.FromMs(t) : now;
        var start = from is { } f ? FrameStore.FromMs(f) : end - TimeSpan.FromHours(24);
        if (start < end - FrameStore.DefaultRetention)
            start = end - FrameStore.DefaultRetention;
        if (end - start < TimeSpan.FromMinutes(1))
            start = end - TimeSpan.FromMinutes(1);
        return (start, end);
    }

    // "SRC>DEST <RR C P R3> text" -> ("<RR C P R3>", "text")
    private static (string frame, string text) SplitSummary(string summary)
    {
        int open = summary.IndexOf(" <", StringComparison.Ordinal);
        int close = open < 0 ? -1 : summary.IndexOf('>', open + 2);
        if (close < 0)
            return (summary, "");
        return (summary[(open + 1)..(close + 1)], summary[(close + 1)..].Trim());
    }

    private static IResult HandleLogin(HttpContext ctx, ConfigManager configManager)
    {
        var authHeader = ctx.Request.Headers.Authorization.FirstOrDefault();
        if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return Results.Unauthorized();
        }

        var password = authHeader.Substring("Bearer ".Length);
        if (configManager.ValidatePassword(password) || configManager.TrySetInitialPassword(password))
        {
            return Results.Ok(new { success = true });
        }

        return Results.Unauthorized();
    }

    private static bool IsAuthenticated(HttpContext ctx, ConfigManager configManager)
    {
        var authHeader = ctx.Request.Headers.Authorization.FirstOrDefault();
        if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var password = authHeader.Substring("Bearer ".Length);
        return configManager.ValidatePassword(password);
    }

    private static async Task SendSseEvent(HttpContext ctx, string modemId, ModemState state, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(new { id = modemId, state }, SseJsonOptions);
        await ctx.Response.WriteAsync($"data: {json}\n\n", cancellationToken);
        await ctx.Response.Body.FlushAsync(cancellationToken);
    }

    private record GlobalConfigUpdate(
        int? WebPort,
        string? Password,
        string? MyCallsign,
        string? MqttServer,
        string? MqttUsername,
        string? MqttPassword,
        bool ClearMyCallsign = false,
        bool ClearMqttServer = false,
        bool ClearMqttUsername = false,
        bool ClearMqttPassword = false);

    private record TransmitRequest(string? ToCallsign, string? Message);
}
