using kissproxy;
using kissproxylib;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
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

    /// <summary>
    /// Maps all API endpoints to the web application.
    /// </summary>
    public static void MapEndpoints(
        IEndpointRouteBuilder app,
        ConfigManager configManager,
        ModemStateManager stateManager,
        Dictionary<string, KissProxy> proxyInstances)
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
            var modems = configManager.Config.Modems.Select(m => new
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

            // Use a channel to serialize SSE writes — StateChanged fires from multiple threads
            // (serial reader + TCP reader) and concurrent writes to the response body corrupt
            // the SSE stream, causing the browser to drop events.
            var channel = Channel.CreateUnbounded<(string id, ModemState state)>(
                new UnboundedChannelOptions { SingleReader = true });
            using var detailedTracking = stateManager.EnableDetailedFrameTracking();

            // Queue initial state for all modems
            foreach (var modem in configManager.Config.Modems)
            {
                var state = stateManager.GetSnapshot(modem.Id);
                if (state != null)
                    channel.Writer.TryWrite((modem.Id, state));
            }

            void OnStateChanged(string id, ModemState state)
            {
                channel.Writer.TryWrite((id, state));
            }

            stateManager.StateChanged += OnStateChanged;

            try
            {
                await foreach (var (id, state) in channel.Reader.ReadAllAsync(cancellationToken))
                {
                    await SendSseEvent(ctx, id, state, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown, don't log
            }
            finally
            {
                stateManager.StateChanged -= OnStateChanged;
                channel.Writer.TryComplete();
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

                configManager.UpdateModem(config);

                // Update the proxy's in-memory config; excludes SETHW so NinoMode
                // is only sent to the modem on an explicit Apply (POST .../apply).
                if (proxyInstances.TryGetValue(id, out var proxy))
                {
                    proxy.UpdateConfig(config);
                }

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
                configManager.RemoveModem(id);
                stateManager.Remove(id);
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
        // Use compact JSON for SSE (no indentation) to avoid multi-line issues
        var sseJsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false // Must be false for SSE single-line format
        };
        var json = JsonSerializer.Serialize(new { id = modemId, state }, sseJsonOptions);
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
