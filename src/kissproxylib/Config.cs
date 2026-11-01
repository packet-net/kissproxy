namespace kissproxy;

/// <summary>
/// Per-modem configuration.
/// </summary>
public record Config
{
    // Existing fields (all editable via UI)
    public required string Id { get; set; }
    public required string ComPort { get; set; }
    public int Baud { get; set; } = 57600;
    public int TcpPort { get; set; } = 8910;
    public bool AnyHost { get; set; } = false;

    // Per-modem MQTT settings (topic prefix can be per-modem, rest uses global)
    public string? MqttTopicPrefix { get; set; }
    public bool Base64 { get; set; } = false;

    // KISS parameter filtering (block commands from node)
    public bool FilterTxDelay { get; set; } = false;
    public bool FilterPersistence { get; set; } = false;
    public bool FilterSlotTime { get; set; } = false;
    public bool FilterTxTail { get; set; } = false;
    public bool FilterFullDuplex { get; set; } = false;
    public bool FilterSetHardware { get; set; } = false;

    // KISS parameter override values (sent to modem instead)
    public int? TxDelayValue { get; set; }        // ms (converted to 10ms units when sent)
    public int? PersistenceValue { get; set; }    // 0-255
    public int? SlotTimeValue { get; set; }       // ms (converted to 10ms units when sent)
    public int? TxTailValue { get; set; }         // ms (converted to 10ms units when sent)
    public bool? FullDuplexValue { get; set; }    // true/false

    // Periodic parameter send interval (seconds, 0 = on connect only)
    public int ParameterSendInterval { get; set; } = 0;

    // NinoTNC mode configuration
    public int? NinoMode { get; set; }            // 0-14
    public bool PersistNinoMode { get; set; } = false;  // false = add 16 to NOT flash
}

/// <summary>
/// Global configuration (not per-modem).
/// </summary>
public record GlobalConfig
{
    public int WebPort { get; set; } = 8080;
    public string Password { get; set; } = "";  // Web UI password (empty = allow any)

    // Station identity (used as source callsign for test transmissions)
    public string? MyCallsign { get; set; }

    // Global MQTT settings (shared by all modems)
    public string? MqttServer { get; set; }
    public string? MqttUsername { get; set; }
    public string? MqttPassword { get; set; }

    public List<Config> Modems { get; set; } = [];
}
