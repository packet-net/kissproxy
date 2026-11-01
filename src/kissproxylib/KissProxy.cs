using kissproxy;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Extensions.ManagedClient;
using MQTTnet.Protocol;
using NAx25;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using static NAx25.KissFraming;

namespace kissproxylib;

public class KissProxy
{
    private readonly string instance;
    private readonly ILogger logger;
    private readonly ISerialPortFactory serialPortFactory;
    private readonly List<byte> inboundBufferData = [];
    private readonly List<byte> outboundBufferData = [];
    private readonly AckModeTracker ackModeTracker = new();
    private IManagedMqttClient? mqttClient;
    private string? mqttTopicPrefix;
    private Config? currentConfig;
    private ModemState? modemState;
    private ISerialPort? activeSerialPort;
    private Timer? parameterResendTimer;
    private readonly object timerLock = new();
    private Channel<(bool outbound, byte[] frame, bool emitAsBase64String)>? frameProcessingQueue;
    private readonly object serialPortLock = new();
    private readonly SemaphoreSlim serialWriteLock = new(1, 1);
    private NetworkStream? activeTcpStream;
    private TcpClient? activeTcpClient;
    private readonly object tcpLock = new();

    public KissProxy(ILogger logger) : this("", logger) { }

    public KissProxy(string instance, ILogger logger)
        : this(instance, logger, new SerialPortFactory()) { }

    public KissProxy(string instance, ILogger logger, ISerialPortFactory serialPortFactory)
    {
        this.instance = instance;
        this.logger = logger;
        this.serialPortFactory = serialPortFactory;
    }

    private Task MqttClient_ConnectedAsync(MqttClientConnectedEventArgs arg)
    {
        logger.LogInformation("Connected to MQTT broker");
        return Task.CompletedTask;
    }

    private IDisposable? BeginLoggingScope() =>
        !string.IsNullOrWhiteSpace(instance)
            ? logger.BeginScope(instance)
            : null;

    private Task MqttClient_DisconnectedAsync(MqttClientDisconnectedEventArgs arg)
    {
        logger.LogWarning("Disconnected from MQTT broker: {reason}", arg?.ReasonString);
        return Task.CompletedTask;
    }

    private Task MqttClient_ConnectingFailedAsync(ConnectingFailedEventArgs arg)
    {
        logger.LogWarning("Failed to connect to MQTT broker: {reason}", arg?.Exception?.Message);
        return Task.CompletedTask;
    }

    private async Task EnqueueString(string topic, string? payload)
    {
        if (mqttClient == null || payload == null)
            return;

        var messageBuilder = new MqttApplicationMessageBuilder()
            .WithTopic(BuildTopicName(topic))
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .WithPayload(payload);

        await mqttClient.EnqueueAsync(messageBuilder.Build());
    }

    private string BuildTopicName(string topic)
    {
        if (string.IsNullOrWhiteSpace(mqttTopicPrefix))
            return topic;

        return $"{mqttTopicPrefix}/{topic}";
    }

    private async Task SendBytesToMqttTopic(string topic, IList<byte> bytes, bool emitAsBase64String)
    {
        if (mqttClient == null)
            return;

        var messageBuilder = new MqttApplicationMessageBuilder()
            .WithTopic(BuildTopicName(topic))
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce);

        messageBuilder = emitAsBase64String
            ? messageBuilder.WithPayload(Convert.ToBase64String(bytes.ToArray(), Base64FormattingOptions.InsertLineBreaks))
            : messageBuilder.WithPayload(bytes.ToArray());

        await mqttClient.EnqueueAsync(messageBuilder.Build());
    }

    /// <summary>
    /// Publishes ACKMODE timing information to MQTT.
    /// Topic: kissproxy/{machine}/{instance}/timing/ackmode
    /// </summary>
    private async Task PublishAckModeTiming(AckModeTiming timing)
    {
        var topic = $"kissproxy/{LastDelimitation(Environment.MachineName, '/')}/{instance}/timing/ackmode";

        logger.LogDebug("ACKMODE timing: seq=0x{SeqHi:X2}{SeqLo:X2}, total={TotalMs:F1}ms, txDuration={TxDurationMs:F1}ms, txDelay={TxDelayMs:F0}ms",
            timing.SeqHi, timing.SeqLo, timing.TotalMs, timing.TxDurationMs ?? 0, timing.TxDelayMs ?? 0);

        await EnqueueString(topic, timing.ToJson());
    }

    /// <summary>
    /// Runs the proxy with the specified Config and GlobalConfig objects.
    /// MQTT settings come from GlobalConfig, per-modem settings from Config.
    /// </summary>
    public Task Run(Config config, GlobalConfig globalConfig, ModemState state, CancellationToken cancellationToken = default)
    {
        currentConfig = config;
        modemState = state;
        return Run(
            config.ComPort,
            cancellationToken,
            config.Baud,
            config.TcpPort,
            config.AnyHost,
            globalConfig.MqttServer,
            globalConfig.MqttUsername,
            globalConfig.MqttPassword,
            config.MqttTopicPrefix,
            config.Base64);
    }

    /// <summary>
    /// Runs the proxy with the specified Config object.
    /// For backwards compatibility when GlobalConfig is not available.
    /// </summary>
    [Obsolete("Use Run(Config, GlobalConfig, ModemState, CancellationToken) instead")]
    public Task Run(Config config, ModemState state, CancellationToken cancellationToken = default)
    {
        currentConfig = config;
        modemState = state;
        return Run(
            config.ComPort,
            cancellationToken,
            config.Baud,
            config.TcpPort,
            config.AnyHost,
            null,  // No MQTT without GlobalConfig
            null,
            null,
            config.MqttTopicPrefix,
            config.Base64);
    }

    /// <summary>
    /// Original Run method for backwards compatibility.
    /// </summary>
    public Task Run(
        string modemComPort,
        int modemSerialBaud = 57600,
        int listenForNodeOnTcpPort = 8910,
        bool allowTcpConnectFromOtherHosts = false,
        string? mqttServer = null,
        string? mqttUsername = null,
        string? mqttPassword = null,
        string? mqttTopicPrefix = null,
        bool emitAsBase64String = false) =>
        Run(modemComPort, CancellationToken.None, modemSerialBaud, listenForNodeOnTcpPort,
            allowTcpConnectFromOtherHosts, mqttServer, mqttUsername, mqttPassword, mqttTopicPrefix, emitAsBase64String);

    public async Task Run(
        string modemComPort,
        CancellationToken cancellationToken,
        int modemSerialBaud = 57600,
        int listenForNodeOnTcpPort = 8910,
        bool allowTcpConnectFromOtherHosts = false,
        string? mqttServer = null,
        string? mqttUsername = null,
        string? mqttPassword = null,
        string? mqttTopicPrefix = null,
        bool emitAsBase64String = false)
    {
        this.mqttTopicPrefix = mqttTopicPrefix;
        using var scope = BeginLoggingScope();
        logger.LogDebug("Starting");

        if (cancellationToken.IsCancellationRequested)
            return;

        if (!string.IsNullOrWhiteSpace(mqttServer))
        {
            var (server, port) = SplitMqttServer(mqttServer);

            logger.LogInformation("Connecting to MQTT broker {server}:{port}...", server, port);

            // Bounded, so a broker that is down for days can't grow memory without limit
            var options = new ManagedMqttClientOptionsBuilder()
                .WithAutoReconnectDelay(TimeSpan.FromSeconds(5))
                .WithMaxPendingMessages(10000)
                .WithPendingMessagesOverflowStrategy(MQTTnet.Server.MqttPendingMessagesOverflowStrategy.DropOldestQueuedMessage)
                .WithClientOptions(new MqttClientOptionsBuilder()
                    .WithClientId(LastDelimitation(Environment.MachineName, '/') + "_kissproxy_" + instance)
                    .WithTcpServer(server, port)
                    .WithCredentials(mqttUsername, mqttPassword)
                    .Build())
                .Build();

            mqttClient = new MqttFactory().CreateManagedMqttClient();
            await mqttClient.StartAsync(options);
            mqttClient.ConnectedAsync += MqttClient_ConnectedAsync;
            mqttClient.ConnectingFailedAsync += MqttClient_ConnectingFailedAsync;
            mqttClient.DisconnectedAsync += MqttClient_DisconnectedAsync;
        }

        // One ordered consumer for MQTT publishing and parameter logging, instead of a
        // Task.Run per frame (which reordered them); bounded so a slow broker drops the oldest
        frameProcessingQueue = Channel.CreateBounded<(bool, byte[], bool)>(
            new BoundedChannelOptions(1000) { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
        var frameProcessingTask = Task.Run(async () =>
        {
            await foreach (var (outbound, frame, base64) in frameProcessingQueue.Reader.ReadAllAsync())
            {
                try { await ProcessFrame(outbound, frame, base64); }
                catch (Exception ex) { logger.LogError("Frame processing failed: {error}", ex.Message); }
            }
        });

        try
        {
            // Open serial port immediately at startup
            int serialRetryCount = 0;
            string? lastTriedComPort = null;

            while (!cancellationToken.IsCancellationRequested)
            {
                // Read port/baud from currentConfig so web UI changes take effect on next retry
                var comPortToTry = currentConfig?.ComPort ?? modemComPort;
                var baudToTry = currentConfig?.Baud ?? modemSerialBaud;

                // Reset backoff counter when the target port changes (e.g. user corrected it in web UI)
                if (comPortToTry != lastTriedComPort)
                {
                    serialRetryCount = 0;
                    lastTriedComPort = comPortToTry;
                }

                ISerialPort? serialPort = null;
                TcpListener? tcpListener = null;
                string opening = $"TCP port {listenForNodeOnTcpPort}";
                try
                {
                    // Listen before opening the modem: if the TCP port can't be bound, retrying
                    // must not reopen the modem and resend its parameters (SETHW is a flash
                    // write with PersistNinoMode) every time round
                    tcpListener = new TcpListener(allowTcpConnectFromOtherHosts ? IPAddress.Any : IPAddress.Loopback, listenForNodeOnTcpPort);
                    tcpListener.Start();

                    opening = comPortToTry;
                    serialPort = serialPortFactory.Create(comPortToTry, baudToTry);
                    serialPort.Open();
                    serialPort.ReadTimeout = 1000; // 1 second timeout for graceful shutdown
                    serialPort.DiscardInBuffer();
                    serialRetryCount = 0; // Reset backoff on successful open

                    lock (serialPortLock)
                    {
                        activeSerialPort = serialPort;
                    }

                    if (modemState != null)
                        modemState.SetConnectionState(serialOpen: true);

                    logger.LogInformation("Opened serial port {comPort}", comPortToTry);

                    // Send configured parameters to modem on connect
                    await SendConfiguredParametersAsync(serialPort);

                    // Start periodic parameter resend timer if configured
                    StartParameterResendTimer(serialPort);

                    // Clear buffers
                    inboundBufferData.Clear();
                    outboundBufferData.Clear();

                    // The serial session: ends on shutdown, or when the modem stops answering
                    // (read or write failure), so the port gets reopened
                    using var serialCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    var serialReaderTask = Task.Run(() =>
                    {
                        try { RunSerialReader(serialPort, emitAsBase64String, serialCts.Token); }
                        finally { try { serialCts.Cancel(); } catch (ObjectDisposedException) { } }
                    });

                    // Run TCP listener loop (accepts connections, bridges to serial)
                    await RunTcpListenerLoop(tcpListener, serialPort, listenForNodeOnTcpPort, emitAsBase64String, serialCts);

                    serialCts.Cancel();
                    try { await serialReaderTask; } catch { }

                    if (!cancellationToken.IsCancellationRequested)
                    {
                        logger.LogWarning("Lost {comPort}, reopening", comPortToTry);
                        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (serialRetryCount == 0)
                        logger.LogError("Could not open {port}: {reason}", opening, ex.Message);
                    else
                        logger.LogWarning("Still unable to open {port}: {reason}", opening, ex.Message);

                    if (modemState != null)
                        modemState.SetConnectionState(serialOpen: false);

                    // Exponential backoff: 5s, 10s, 20s, 40s, then 60s cap
                    var delaySeconds = Math.Min(5 * (1 << Math.Min(serialRetryCount, 4)), 60);
                    serialRetryCount++;
                    try { await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken); }
                    catch (OperationCanceledException) { break; }
                    continue;
                }
                finally
                {
                    StopParameterResendTimer();

                    try { tcpListener?.Stop(); }
                    catch { }

                    lock (serialPortLock)
                    {
                        activeSerialPort = null;
                    }

                    lock (tcpLock)
                    {
                        activeTcpClient = null;
                        activeTcpStream = null;
                    }

                    try { serialPort?.Close(); }
                    catch { }

                    if (modemState != null)
                    {
                        modemState.SetConnectionState(serialOpen: false, nodeConnected: false);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Top level exception handled");
        }
        finally
        {
            StopParameterResendTimer();
            frameProcessingQueue.Writer.TryComplete();
            try { await frameProcessingTask.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException) { }

            // A proxy can now be stopped or restarted while the process runs; a leftover
            // client would fight its replacement, which uses the same client id
            if (mqttClient != null)
            {
                try { await mqttClient.StopAsync(); }
                catch (Exception ex) { logger.LogWarning("Stopping MQTT client failed: {error}", ex.Message); }
                mqttClient.Dispose();
                mqttClient = null;
            }
        }
    }

    private void QueueFrameProcessing(bool outbound, byte[] frame, bool emitAsBase64String)
    {
        if (frameProcessingQueue == null || !frameProcessingQueue.Writer.TryWrite((outbound, frame, emitAsBase64String)))
            Task.Run(async () => await ProcessFrame(outbound, frame, emitAsBase64String));
    }

    private async Task<TcpClient?> AcceptTcpClientAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        try
        {
            return await listener.AcceptTcpClientAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            logger.LogError("Error accepting TCP connection: {error}", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Runs the TCP listener loop, accepting connections and bridging to serial.
    /// Serial port stays open between TCP connections.
    /// </summary>
    private async Task RunTcpListenerLoop(
        TcpListener tcpListener,
        ISerialPort serialPort,
        int tcpPort,
        bool emitAsBase64String,
        CancellationTokenSource serialCts)
    {
        var cancellationToken = serialCts.Token;

        while (!cancellationToken.IsCancellationRequested)
        {
            if (modemState != null)
                modemState.SetConnectionState(nodeConnected: false);

            logger.LogInformation("Awaiting node connection on port {port}", tcpPort);

            using var tcpClient = await AcceptTcpClientAsync(tcpListener, cancellationToken);
            if (tcpClient == null)
                continue;

            using var tcpStream = tcpClient.GetStream();
            tcpStream.ReadTimeout = 1000; // 1 second timeout for graceful shutdown
            logger.LogInformation("Accepted TCP node connection on port {port}", tcpPort);

            // Register the TCP connection so serial reader can forward to it
            lock (tcpLock)
            {
                activeTcpClient = tcpClient;
                activeTcpStream = tcpStream;
            }

            if (modemState != null)
                modemState.SetConnectionState(nodeConnected: true);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            // Run node-to-modem (reads from TCP, writes to serial)
            Task nodeToModem = Task.Run(() => RunNodeToModem(tcpStream, serialPort, emitAsBase64String, cts, serialCts));

            // Wait for node to disconnect or cancellation
            while (!cancellationToken.IsCancellationRequested && !nodeToModem.IsCompleted)
            {
                await Task.Delay(500, CancellationToken.None);
            }

            cts.Cancel();

            // Unregister TCP connection
            lock (tcpLock)
            {
                activeTcpClient = null;
                activeTcpStream = null;
            }

            try { tcpStream.Socket.Shutdown(SocketShutdown.Both); }
            catch { }

            // Let the node thread finish before its token sources are disposed
            await Task.WhenAny(nodeToModem, Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None));

            if (modemState != null)
                modemState.SetConnectionState(nodeConnected: false);

            if (cancellationToken.IsCancellationRequested)
                logger.LogInformation("Node session ended");
            else
                logger.LogInformation("Node disconnected, serial port remains open");
        }
    }

    /// <summary>
    /// Reads from serial port continuously, forwarding to TCP if connected.
    /// Updates stats and publishes to MQTT regardless of TCP connection.
    /// </summary>
    private void RunSerialReader(ISerialPort serialPort, bool emitAsBase64String, CancellationToken cancellationToken)
    {
        var readBuffer = new byte[512];

        while (!cancellationToken.IsCancellationRequested)
        {
            int read;
            try
            {
                var bytesToRead = Math.Min(readBuffer.Length, Math.Max(serialPort.BytesToRead, 1));
                read = serialPort.Read(readBuffer, 0, bytesToRead);
            }
            catch (TimeoutException)
            {
                // Timeout allows us to check cancellation token periodically
                continue;
            }
            catch (Exception ex)
            {
                if (!cancellationToken.IsCancellationRequested)
                    logger.LogError("Reading byte from modem blew up with \"{error}\"", ex.Message);
                return;
            }

            if (read < 0)
            {
                logger.LogError("Modem read returned {read}", read);
                return;
            }

            for (int i = 0; i < read; i++)
            {
                ProcessByte(inboundBufferData, false, readBuffer[i], emitAsBase64String);
            }

            // Forward to TCP if connected
            NetworkStream? tcpStream;
            TcpClient? tcpClient;
            lock (tcpLock)
            {
                tcpStream = activeTcpStream;
                tcpClient = activeTcpClient;
            }

            if (tcpStream != null && tcpClient != null)
            {
                try
                {
                    tcpStream.Write(readBuffer, 0, read);
                }
                catch (Exception ex)
                {
                    logger.LogWarning("Writing byte to node failed: \"{error}\"", ex.Message);
                    // Don't return - keep reading from serial even if TCP write fails
                    // The TCP connection will be closed by the listener loop
                    lock (tcpLock)
                    {
                        activeTcpStream = null;
                        activeTcpClient = null;
                    }
                    try { tcpClient.Close(); }
                    catch { }
                }
            }
        }
    }

    /// <summary>
    /// Sends a raw KISS frame to the modem's serial port.
    /// Thread-safe: uses the same serialWriteLock as all other serial writers.
    /// </summary>
    /// <param name="kissFrame">Complete KISS frame including FEND delimiters</param>
    /// <returns>True if the frame was sent, false if serial port is not open</returns>
    public bool SendRawFrame(byte[] kissFrame)
    {
        ISerialPort? serialPort;
        lock (serialPortLock)
        {
            serialPort = activeSerialPort;
        }

        if (serialPort == null)
            return false;

        try
        {
            WriteToSerial(serialPort, kissFrame, 0, kissFrame.Length);
            LogFrame("TX", kissFrame);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError("SendRawFrame failed: {error}", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Writes a KISS frame to the serial port with exclusive access, preventing
    /// interleaved bytes from concurrent writers (node→modem, config apply, timer resend).
    /// </summary>
    private void WriteToSerial(ISerialPort serialPort, byte[] frame, int offset, int count)
    {
        serialWriteLock.Wait();
        try
        {
            serialPort.Write(frame, offset, count);
        }
        finally
        {
            serialWriteLock.Release();
        }
    }

    /// <summary>
    /// Async version of WriteToSerial for use in async paths.
    /// </summary>
    private async Task WriteToSerialAsync(ISerialPort serialPort, byte[] frame, int offset, int count)
    {
        await serialWriteLock.WaitAsync();
        try
        {
            serialPort.Write(frame, offset, count);
        }
        finally
        {
            serialWriteLock.Release();
        }
    }

    private void RunNodeToModem(NetworkStream tcpStream, ISerialPort serialPort, bool emitAsBase64String, CancellationTokenSource cts, CancellationTokenSource serialCts)
    {
        List<byte> frameBuffer = [];
        var readBuffer = new byte[512];

        while (!cts.Token.IsCancellationRequested)
        {
            int read;
            try
            {
                read = tcpStream.Read(readBuffer, 0, readBuffer.Length);
            }
            catch (IOException ex) when (ex.InnerException is SocketException se && se.SocketErrorCode == SocketError.TimedOut)
            {
                // Timeout allows us to check cancellation token periodically
                continue;
            }
            catch (Exception)
            {
                logger.LogInformation("Node disconnected (read threw)");
                return;
            }

            // 0 is a clean close by the node
            if (read <= 0)
            {
                logger.LogInformation("Node disconnected");
                return;
            }

            for (int i = 0; i < read; i++)
            {
                var b = readBuffer[i];
                bool frameComplete = BufferByteForFiltering(frameBuffer, b);

                if (frameComplete && frameBuffer.Count > 0)
                {
                    var frame = frameBuffer.ToArray();
                    frameBuffer.Clear();

                    // Check if frame should be filtered
                    if (ShouldFilterFrame(frame))
                    {
                        logger.LogDebug("Filtered frame from node");
                        modemState?.RecordFilteredFrame();
                        var filtInfo = CreateFrameInfo(frame, outbound: true, includeDetailedFields: modemState?.CaptureDetailedFrameInfo ?? false);
                        if (filtInfo != null) modemState?.RecordNodeParamCommand(filtInfo, filtered: true);
                        continue;
                    }

                    // Forward the frame to modem (locked to prevent interleaving with config writes)
                    try
                    {
                        WriteToSerial(serialPort, frame, 0, frame.Length);
                        ProcessOutboundFrame(frame, emitAsBase64String);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError("Writing frame to modem failed: \"{error}\"", ex.Message);
                        serialCts.Cancel();
                        return;
                    }
                }
                else if (!frameComplete)
                {
                    // Still accumulating frame, don't forward yet
                }
            }
        }
    }

    /// <summary>
    /// Buffers bytes until a complete frame is detected.
    /// Returns true when a complete frame is ready in the buffer.
    /// </summary>
    private static bool BufferByteForFiltering(List<byte> buffer, byte b)
    {
        const byte FEND = KissFrameBuilder.FEND;

        // Discard repeated FENDs
        if (b == FEND && buffer.Count > 0 && buffer[^1] == FEND)
            return false;

        buffer.Add(b);

        // Check if we have a complete frame (ends with FEND, has content)
        if (b == FEND && buffer.Count > 2)
        {
            // Frame is complete if it starts with FEND and ends with FEND
            if (buffer[0] == FEND)
                return true;

            // Or if it just ends with FEND (missing leading FEND)
            return true;
        }

        return false;
    }

    /// <summary>
    /// Checks if a frame from the node should be filtered based on config.
    /// </summary>
    private bool ShouldFilterFrame(byte[] frame)
    {
        if (currentConfig == null)
            return false;

        var cmdByte = KissFrameBuilder.GetCommandByteFromFrame(frame);
        if (!cmdByte.HasValue)
            return false;

        var (command, _) = KissFrameBuilder.ParseCommandByte(cmdByte.Value);

        // Data frames always pass through
        if (command == KissFrameBuilder.CMD_DATAFRAME)
            return false;

        return KissFrameBuilder.ShouldFilter(command, currentConfig);
    }

    private void ProcessOutboundFrame(byte[] frame, bool emitAsBase64String)
    {
        LogFrame("TX", frame);

        // Record in state
        var frameSummary = CreateFrameSummary(frame);
        var captureDetailedFrameInfo = modemState?.CaptureDetailedFrameInfo ?? false;
        var frameInfo = captureDetailedFrameInfo
            ? CreateFrameInfo(frame, outbound: true, includeDetailedFields: true)
            : null;
        modemState?.RecordFrameToModem(frame, frameSummary, frameInfo);

        // Track outbound ACKMODE frames for timing
        var cmdByte = KissFrameBuilder.GetCommandByteFromFrame(frame);
        if (cmdByte.HasValue)
        {
            var (cmd, _) = KissFrameBuilder.ParseCommandByte(cmdByte.Value);

            // Track ACKMODE frames for timing calculation
            if (cmd == KissFrameBuilder.CMD_ACKMODE)
            {
                ackModeTracker.TrackOutbound(frame);
            }
            // Update tracker when parameters change
            else if (cmd == KissFrameBuilder.CMD_TXDELAY && frame.Length >= 4)
            {
                int valueIdx = frame[0] == KissFrameBuilder.FEND ? 2 : 1;
                if (valueIdx < frame.Length - 1)
                    ackModeTracker.SetTxDelay(frame[valueIdx]);
            }
            else if (cmd == KissFrameBuilder.CMD_SETHW && frame.Length >= 4)
            {
                int valueIdx = frame[0] == KissFrameBuilder.FEND ? 2 : 1;
                if (valueIdx < frame.Length - 1)
                {
                    // Mode value: if >= 16, subtract 16 (temporary mode)
                    int modeValue = frame[valueIdx];
                    int mode = modeValue >= 16 ? modeValue - 16 : modeValue;
                    ackModeTracker.SetMode(mode);
                }
            }
        }

        if (ShouldProcessFrame(frame))
        {
            QueueFrameProcessing(outbound: true, frame, emitAsBase64String);
        }
    }

    /// <summary>
    /// Sends all configured parameters to the modem.
    /// </summary>
    private async Task SendConfiguredParametersAsync(ISerialPort serialPort)
    {
        if (currentConfig == null)
            return;

        UpdateAckModeTracker(currentConfig);

        var frames = KissFrameBuilder.BuildAllParameterFrames(currentConfig);
        foreach (var frame in frames)
        {
            try
            {
                await WriteToSerialAsync(serialPort, frame, 0, frame.Length);
                var cmdByte = KissFrameBuilder.GetCommandByteFromFrame(frame);
                if (cmdByte.HasValue)
                {
                    var (cmd, _) = KissFrameBuilder.ParseCommandByte(cmdByte.Value);
                    logger.LogInformation("Sent {command} to modem", KissFrameBuilder.GetCommandName(cmd));
                }

                // Small delay between parameter sends to let the TNC process each command
                await Task.Delay(50);
            }
            catch (Exception ex)
            {
                logger.LogError("Error sending parameter to modem: {error}", ex.Message);
            }
        }
    }

    /// <summary>
    /// Sends configured parameters to the modem synchronously.
    /// </summary>
    /// <param name="includeSetHardware">
    /// When false, the SETHW (NinoTNC mode) frame is omitted.
    /// The timer resend path passes false when PersistNinoMode is enabled so the
    /// mode is not written to flash on every resend interval.
    /// </param>
    public void SendConfiguredParameters(bool includeSetHardware = true)
    {
        ISerialPort? serialPort;
        lock (serialPortLock)
        {
            serialPort = activeSerialPort;
        }

        if (serialPort == null || currentConfig == null)
            return;

        var frames = KissFrameBuilder.BuildAllParameterFrames(currentConfig, includeSetHardware: includeSetHardware);
        foreach (var frame in frames)
        {
            try
            {
                WriteToSerial(serialPort, frame, 0, frame.Length);
            }
            catch (Exception ex)
            {
                logger.LogError("Error sending parameter to modem: {error}", ex.Message);
                return; // Stop if serial port is gone
            }
        }

        logger.LogDebug("Periodic parameter resend completed");
    }

    private void StartParameterResendTimer(ISerialPort serialPort)
    {
        if (currentConfig == null || currentConfig.ParameterSendInterval <= 0)
            return;

        var interval = TimeSpan.FromSeconds(currentConfig.ParameterSendInterval);

        // When PersistNinoMode is on, omit SETHW from periodic resends to avoid
        // wearing out the TNC's flash with unnecessary write cycles.
        // The mode was already sent once at connect time (SendConfiguredParametersAsync)
        // and will be re-sent whenever the user explicitly applies a config change.
        lock (timerLock)
        {
            parameterResendTimer?.Dispose();
            parameterResendTimer = new Timer(_ =>
                SendConfiguredParameters(includeSetHardware: !(currentConfig?.PersistNinoMode ?? false)),
                null, interval, interval);
        }

        if (currentConfig.PersistNinoMode && currentConfig.NinoMode.HasValue)
            logger.LogInformation("Started parameter resend timer with {interval}s interval (SETHW excluded, mode is persisted to flash)", currentConfig.ParameterSendInterval);
        else
            logger.LogInformation("Started parameter resend timer with {interval}s interval", currentConfig.ParameterSendInterval);
    }

    private void StopParameterResendTimer()
    {
        lock (timerLock)
        {
            parameterResendTimer?.Dispose();
            parameterResendTimer = null;
        }
    }

    /// <summary>
    /// Called when config changes. Resends all parameters (including SETHW/NinoMode) to modem if connected.
    /// Use this only for explicit "Apply Now" actions.
    /// </summary>
    public void OnConfigChanged(Config newConfig)
    {
        currentConfig = newConfig;
        UpdateAckModeTracker(newConfig);
        SendConfiguredParameters();
        RestartResendTimer();
    }

    /// <summary>
    /// Updates the in-memory config (for filters etc.) without sending anything to the modem.
    /// Use OnConfigChanged for explicit applies that should push params to the modem.
    /// </summary>
    public void UpdateConfig(Config newConfig)
    {
        currentConfig = newConfig;
    }

    /// <summary>
    /// Keeps ACKMODE timing in step with the mode and TxDelay we send ourselves; the node's
    /// own parameter frames update it as they pass through.
    /// </summary>
    private void UpdateAckModeTracker(Config config)
    {
        if (config.NinoMode.HasValue)
            ackModeTracker.SetMode(config.NinoMode.Value);
        if (config.FilterTxDelay && config.TxDelayValue.HasValue)
            ackModeTracker.SetTxDelay(config.TxDelayValue.Value / 10);
    }

    private void RestartResendTimer()
    {
        StopParameterResendTimer();
        ISerialPort? serialPort;
        lock (serialPortLock)
        {
            serialPort = activeSerialPort;
        }
        if (serialPort != null)
        {
            StartParameterResendTimer(serialPort);
        }
    }

    private FrameInfo? CreateFrameInfo(byte[] frame, bool outbound, bool includeDetailedFields)
    {
        var summary = CreateFrameSummary(frame);
        if (!summary.HasValue)
            return null;

        var cmdByte = KissFrameBuilder.GetCommandByteFromFrame(frame);
        if (!cmdByte.HasValue)
            return null;

        var (_, port) = KissFrameBuilder.ParseCommandByte(cmdByte.Value);

        var info = new FrameInfo
        {
            Timestamp = DateTime.UtcNow,
            CommandCode = summary.Value.CommandCode,
            CommandName = KissFrameBuilder.GetCommandName(summary.Value.CommandCode),
            Port = port,
            PayloadLength = Math.Max(0, frame.Length - 3) // Subtract FEND, cmd, FEND
        };

        if (includeDetailedFields)
        {
            var (hexDump, asciiDump) = FrameInfo.CreateDumps(frame);
            info.HexDump = hexDump;
            info.AsciiDump = asciiDump;
        }

        // For parameter frames, extract the value
        if (summary.Value.ParameterValue.HasValue)
        {
            info.ParameterValue = summary.Value.ParameterValue.Value;
        }

        // For data frames, extract source callsign from AX.25 header
        if (includeDetailedFields && summary.Value.IsDataFrame)
        {
            int payloadStart = frame[0] == KissFrameBuilder.FEND ? 2 : 1;
            // AX.25 layout: dest (7 bytes) then src (7 bytes)
            if (frame.Length > payloadStart + 14)
                info.SourceCallsign = ExtractAx25Callsign(frame, payloadStart + 7);
        }

        return info;
    }

    private static string? ExtractAx25Callsign(byte[] frame, int offset)
    {
        if (offset + 7 > frame.Length) return null;
        var sb = new StringBuilder(6);
        for (int i = 0; i < 6; i++)
        {
            char c = (char)(frame[offset + i] >> 1);
            if (c == ' ') break;
            if (!char.IsLetterOrDigit(c)) return null;
            sb.Append(c);
        }
        if (sb.Length == 0) return null;
        int ssid = (frame[offset + 6] >> 1) & 0x0F;
        return ssid > 0 ? $"{sb}-{ssid}" : sb.ToString();
    }

    private void ProcessByte(List<byte> buffer, bool outbound, byte b, bool emitAsBase64String)
    {
        KissHelpers.ProcessBuffer(buffer, b, frame =>
        {
            if (!outbound)
                LogFrame("RX", frame);

            // Record in state
            if (!outbound && modemState != null)
            {
                var frameSummary = CreateFrameSummary(frame);
                var frameInfo = modemState.CaptureDetailedFrameInfo
                    ? CreateFrameInfo(frame, outbound: false, includeDetailedFields: true)
                    : null;
                modemState.RecordFrameFromModem(frame, frameSummary, frameInfo);
            }

            // Check for ACKMODE ACK from modem (inbound)
            if (!outbound)
            {
                var timing = ackModeTracker.ProcessAck(frame);
                if (timing != null)
                {
                    // Publish timing info to MQTT
                    Task.Run(async () => await PublishAckModeTiming(timing));
                }
            }

            if (ShouldProcessFrame(frame))
            {
                QueueFrameProcessing(outbound, frame, emitAsBase64String);
            }
        });
    }

    /// <summary>
    /// The frame log: one line per AX.25 frame to or from the modem. TX means handed
    /// to the modem, not necessarily on air (an ACKMODE ack line shows that).
    /// </summary>
    private void LogFrame(string direction, byte[] kissFrame)
    {
        if (!logger.IsEnabled(LogLevel.Information))
            return;

        string? description;
        try
        {
            description = Ax25Decoder.DescribeKissFrame(kissFrame);
        }
        catch (Exception ex)
        {
            description = $"decode failed ({ex.Message}), {kissFrame.Length} bytes";
        }

        if (description != null)
            logger.LogInformation("{direction} {frame}", direction, description);
    }

    private bool ShouldProcessFrame(byte[] frame)
    {
        if (mqttClient != null || logger.IsEnabled(LogLevel.Debug))
            return true;

        if (!logger.IsEnabled(LogLevel.Information))
            return false;

        var cmdByte = KissFrameBuilder.GetCommandByteFromFrame(frame);
        if (!cmdByte.HasValue)
            return false;

        var (command, _) = KissFrameBuilder.ParseCommandByte(cmdByte.Value);
        return command == KissFrameBuilder.CMD_PERSISTENCE
            || command == KissFrameBuilder.CMD_SLOTTIME
            || command == KissFrameBuilder.CMD_TXDELAY
            || command == KissFrameBuilder.CMD_TXTAIL
            || command == KissFrameBuilder.CMD_FULLDUPLEX;
    }

    private static FrameSummary? CreateFrameSummary(byte[] frame)
    {
        var cmdByte = KissFrameBuilder.GetCommandByteFromFrame(frame);
        if (!cmdByte.HasValue)
            return null;

        var (command, _) = KissFrameBuilder.ParseCommandByte(cmdByte.Value);
        int? parameterValue = null;

        if (command != KissFrameBuilder.CMD_DATAFRAME && frame.Length >= 4)
        {
            int valueIdx = frame[0] == KissFrameBuilder.FEND ? 2 : 1;
            if (valueIdx < frame.Length - 1)
            {
                parameterValue = frame[valueIdx];
            }
        }

        return new FrameSummary(command, parameterValue);
    }

    private static string LastDelimitation(string topic, char delimiter)
    {
        var parts = topic.Split(delimiter);
        return parts.Last();
    }

    private async Task ProcessFrame(bool outbound, byte[] kissFrame, bool emitAsBase64String)
    {
        string? description = null;

        var topic = $"kissproxy/{LastDelimitation(Environment.MachineName, '/')}/{instance}/{(outbound ? "to" : "from")}Modem";

        await SendBytesToMqttTopic($"{topic}/framed", kissFrame, emitAsBase64String);

        try
        {
            var (ax25Frame, portId, commandCode) = Unkiss(kissFrame);
            await SendBytesToMqttTopic($"{topic}/unframed/port{portId}/{commandCode}KissCmd", ax25Frame, emitAsBase64String);

            static string ToNumbers(byte[] bytes) => string.Join(" ", bytes.Select(b => (int)b));

            if (commandCode == KissCommandCode.Persistence || commandCode == KissCommandCode.SlotTime ||
                commandCode == KissCommandCode.TxDelay || commandCode == KissCommandCode.TxTail ||
                commandCode == KissCommandCode.FullDuplex)
            {
                var cmdByte = (byte)(int)commandCode;
                var displayValue = ax25Frame.Length >= 1
                    ? KissFrameBuilder.FormatParameterValue(cmdByte, ax25Frame[0])
                    : ToNumbers(ax25Frame);
                logger.LogInformation("{command} set to {value}", commandCode, displayValue);
                return;
            }

            if (commandCode == KissCommandCode.DataFrame || commandCode == KissCommandCode.AckMode)
            {
                if (mqttClient != null)
                {
                    description = Ax25Decoder.DescribeKissFrame(kissFrame);
                    if (description != null)
                    {
                        await EnqueueString($"{topic}/decoded/port{portId}/", description);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError("Exception: {message}", ex.Message);
        }
    }

    private static (string server, int port) SplitMqttServer(string mqttServer)
    {
        var parts = mqttServer.Split(':');
        if (parts.Length == 2 && int.TryParse(parts[1], out var port))
        {
            return (parts[0], port);
        }
        return (parts[0], 1883);
    }
}
