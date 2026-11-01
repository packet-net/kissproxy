using System.Text.Json;
using System.Text.Json.Nodes;
using kissproxy;

namespace kissproxylib;

/// <summary>
/// Manages loading, saving, and modifying the kissproxy configuration file.
/// Supports migration from old array format to new GlobalConfig format.
/// </summary>
public class ConfigManager
{
    private readonly string configPath;
    private GlobalConfig config;
    private bool isWriteable;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Event fired when a modem's configuration changes.
    /// The string parameter is the modem ID.
    /// </summary>
    public event Action<string>? ModemConfigChanged;

    /// <summary>
    /// Event fired when the global configuration changes.
    /// </summary>
    public event Action? GlobalConfigChanged;

    public string ConfigPath => configPath;
    public bool IsWriteable => isWriteable;
    public GlobalConfig Config => config;

    public ConfigManager(string path)
    {
        configPath = path;
        config = new GlobalConfig { Password = "" };
        isWriteable = false;
    }

    /// <summary>
    /// Loads the configuration from file, detecting and migrating old format if necessary.
    /// </summary>
    public GlobalConfig Load()
    {
        if (!File.Exists(configPath))
        {
            // No config file - return default with empty password (will need to be set)
            config = new GlobalConfig { Password = "", Modems = [] };
            isWriteable = CheckWriteable();
            return config;
        }

        var json = File.ReadAllText(configPath);

        // Try to detect format by parsing as JSON
        var node = JsonNode.Parse(json);

        if (node is JsonArray)
        {
            // Old format: array of modem configs
            var modems = JsonSerializer.Deserialize<List<Config>>(json, JsonOptions) ?? [];

            // MQTT settings were per-modem in the old format; the first modem that has them wins
            string? OldSetting(string name) => node.AsArray()
                .OfType<JsonObject>()
                .Select(m => m.FirstOrDefault(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase)).Value)
                .Select(v => v is JsonValue jv && jv.TryGetValue<string>(out var str) && !string.IsNullOrWhiteSpace(str) ? str : null)
                .FirstOrDefault(v => v != null);

            config = new GlobalConfig
            {
                Password = "",  // Set by the first login
                WebPort = 8080,
                MqttServer = OldSetting("mqttServer"),
                MqttUsername = OldSetting("mqttUsername"),
                MqttPassword = OldSetting("mqttPassword"),
                Modems = modems
            };
        }
        else if (node is JsonObject)
        {
            // New format: GlobalConfig object
            config = JsonSerializer.Deserialize<GlobalConfig>(json, JsonOptions)
                ?? new GlobalConfig { Password = "", Modems = [] };
        }
        else
        {
            throw new InvalidOperationException("Invalid config file format");
        }

        isWriteable = CheckWriteable();
        return config;
    }

    /// <summary>
    /// Checks if the config file (or its directory) is writeable.
    /// </summary>
    public bool CheckWriteable()
    {
        try
        {
            if (File.Exists(configPath))
            {
                // Try to open for write
                using var fs = File.Open(configPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                return true;
            }
            else
            {
                // Config doesn't exist - check if directory is writeable
                var dir = Path.GetDirectoryName(configPath);
                if (string.IsNullOrEmpty(dir))
                    dir = ".";

                var testFile = Path.Combine(dir, $".kissproxy-write-test-{Guid.NewGuid()}");
                try
                {
                    File.WriteAllText(testFile, "test");
                    File.Delete(testFile);
                    return true;
                }
                catch
                {
                    return false;
                }
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Saves the current configuration to file.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown if config file is not writeable.</exception>
    public void Save()
    {
        if (!isWriteable)
        {
            throw new InvalidOperationException($"Config file is not writeable: {configPath}");
        }

        var json = JsonSerializer.Serialize(config, JsonOptions);
        File.WriteAllText(configPath, json);
    }

    /// <summary>
    /// Gets a modem configuration by ID.
    /// </summary>
    public Config? GetModem(string id)
    {
        return config.Modems.FirstOrDefault(m => m.Id == id);
    }

    /// <summary>
    /// Updates an existing modem configuration.
    /// </summary>
    /// <exception cref="KeyNotFoundException">Thrown if modem ID doesn't exist.</exception>
    public void UpdateModem(Config modem)
    {
        var index = config.Modems.FindIndex(m => m.Id == modem.Id);
        if (index < 0)
        {
            throw new KeyNotFoundException($"Modem '{modem.Id}' not found");
        }

        config.Modems[index] = modem;
        ModemConfigChanged?.Invoke(modem.Id);
    }

    /// <summary>
    /// Adds a new modem configuration.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown if modem ID already exists.</exception>
    public void AddModem(Config modem)
    {
        if (config.Modems.Any(m => m.Id == modem.Id))
        {
            throw new InvalidOperationException($"Modem '{modem.Id}' already exists");
        }

        config.Modems.Add(modem);
        ModemConfigChanged?.Invoke(modem.Id);
    }

    /// <summary>
    /// Removes a modem configuration by ID.
    /// </summary>
    /// <exception cref="KeyNotFoundException">Thrown if modem ID doesn't exist.</exception>
    public void RemoveModem(string id)
    {
        var index = config.Modems.FindIndex(m => m.Id == id);
        if (index < 0)
        {
            throw new KeyNotFoundException($"Modem '{id}' not found");
        }

        config.Modems.RemoveAt(index);
        ModemConfigChanged?.Invoke(id);
    }

    /// <summary>
    /// Updates global configuration (web port, password, MQTT settings).
    /// </summary>
    public void UpdateGlobal(
        int? webPort = null,
        string? password = null,
        string? myCallsign = null,
        string? mqttServer = null,
        string? mqttUsername = null,
        string? mqttPassword = null,
        bool clearMyCallsign = false,
        bool clearMqttServer = false,
        bool clearMqttUsername = false,
        bool clearMqttPassword = false)
    {
        if (webPort.HasValue)
            config = config with { WebPort = webPort.Value };

        if (password != null)
            config = config with { Password = password };

        if (clearMyCallsign)
            config = config with { MyCallsign = null };
        else if (myCallsign != null)
            config = config with { MyCallsign = myCallsign.ToUpper().Trim() };

        if (clearMqttServer)
            config = config with { MqttServer = null };
        else if (mqttServer != null)
            config = config with { MqttServer = mqttServer };

        if (clearMqttUsername)
            config = config with { MqttUsername = null };
        else if (mqttUsername != null)
            config = config with { MqttUsername = mqttUsername };

        if (clearMqttPassword)
            config = config with { MqttPassword = null };
        else if (mqttPassword != null)
            config = config with { MqttPassword = mqttPassword };

        GlobalConfigChanged?.Invoke();
    }

    /// <summary>
    /// Validates the password against the configured password.
    /// </summary>
    public bool ValidatePassword(string password)
    {
        // No password set yet: nothing validates until the first login sets one (TrySetInitialPassword)
        if (string.IsNullOrEmpty(config.Password))
            return false;

        return config.Password == password;
    }

    /// <summary>
    /// When no password is set (fresh install or migrated old config), makes the given
    /// password the web UI password and saves it if the config file is writeable.
    /// Returns false if a password is already set or the given one is empty.
    /// </summary>
    public bool TrySetInitialPassword(string password)
    {
        lock (initialPasswordLock)
        {
            if (!string.IsNullOrEmpty(config.Password) || string.IsNullOrEmpty(password))
                return false;

            config = config with { Password = password };
        }

        if (isWriteable)
        {
            try { Save(); }
            catch (Exception) { /* stays in memory; the UI's Save button can retry */ }
        }

        return true;
    }

    private readonly object initialPasswordLock = new();

    /// <summary>
    /// Returns true if the config is using the old array format (no password set).
    /// </summary>
    public bool NeedsMigration => string.IsNullOrEmpty(config.Password);
}
