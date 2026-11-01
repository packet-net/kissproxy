using AwesomeAssertions;
using kissproxy;
using kissproxylib;

namespace kissproxy_tests;

public class ConfigManagerTests : IDisposable
{
    private readonly string tempDir;
    private readonly List<string> tempFiles = [];

    public ConfigManagerTests()
    {
        tempDir = Path.Combine(Path.GetTempPath(), $"kissproxy-tests-{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
    }

    public void Dispose()
    {
        foreach (var file in tempFiles)
        {
            if (File.Exists(file))
                File.Delete(file);
        }
        if (Directory.Exists(tempDir))
            Directory.Delete(tempDir, recursive: true);
    }

    private string CreateTempConfig(string content)
    {
        var path = Path.Combine(tempDir, $"config-{Guid.NewGuid()}.json");
        File.WriteAllText(path, content);
        tempFiles.Add(path);
        return path;
    }

    [Fact]
    public void Load_NewFormat_ParsesCorrectly()
    {
        var configJson = """
        {
            "webPort": 8888,
            "password": "secret123",
            "modems": [
                {
                    "id": "2m",
                    "comPort": "/dev/ttyACM0",
                    "baud": 57600,
                    "tcpPort": 8910
                }
            ]
        }
        """;

        var path = CreateTempConfig(configJson);
        var manager = new ConfigManager(path);
        var config = manager.Load();

        config.WebPort.Should().Be(8888);
        config.Password.Should().Be("secret123");
        config.Modems.Should().HaveCount(1);
        config.Modems[0].Id.Should().Be("2m");
        config.Modems[0].ComPort.Should().Be("/dev/ttyACM0");
        manager.NeedsMigration.Should().BeFalse();
    }

    [Fact]
    public void Load_OldArrayFormat_MigratesCorrectly()
    {
        var configJson = """
        [
            {
                "id": "2m",
                "comPort": "/dev/ttyACM0",
                "baud": 57600,
                "tcpPort": 8910
            }
        ]
        """;

        var path = CreateTempConfig(configJson);
        var manager = new ConfigManager(path);
        var config = manager.Load();

        config.WebPort.Should().Be(8080); // default
        config.Password.Should().BeEmpty();
        config.Modems.Should().HaveCount(1);
        config.Modems[0].Id.Should().Be("2m");
        manager.NeedsMigration.Should().BeTrue();
    }

    [Fact]
    public void Load_NonExistentFile_ReturnsDefaultConfig()
    {
        var path = Path.Combine(tempDir, "nonexistent.json");
        var manager = new ConfigManager(path);
        var config = manager.Load();

        config.WebPort.Should().Be(8080);
        config.Password.Should().BeEmpty();
        config.Modems.Should().BeEmpty();
    }

    [Fact]
    public void GetModem_ExistingId_ReturnsConfig()
    {
        var configJson = """
        {
            "password": "test",
            "modems": [
                { "id": "2m", "comPort": "/dev/ttyACM0" },
                { "id": "70cm", "comPort": "/dev/ttyACM1" }
            ]
        }
        """;

        var path = CreateTempConfig(configJson);
        var manager = new ConfigManager(path);
        manager.Load();

        var modem = manager.GetModem("70cm");
        modem.Should().NotBeNull();
        modem!.ComPort.Should().Be("/dev/ttyACM1");
    }

    [Fact]
    public void GetModem_NonExistingId_ReturnsNull()
    {
        var configJson = """
        {
            "password": "test",
            "modems": [
                { "id": "2m", "comPort": "/dev/ttyACM0" }
            ]
        }
        """;

        var path = CreateTempConfig(configJson);
        var manager = new ConfigManager(path);
        manager.Load();

        var modem = manager.GetModem("nonexistent");
        modem.Should().BeNull();
    }

    [Fact]
    public void UpdateModem_ExistingId_UpdatesConfig()
    {
        var configJson = """
        {
            "password": "test",
            "modems": [
                { "id": "2m", "comPort": "/dev/ttyACM0", "baud": 57600 }
            ]
        }
        """;

        var path = CreateTempConfig(configJson);
        var manager = new ConfigManager(path);
        manager.Load();

        var updated = new Config { Id = "2m", ComPort = "/dev/ttyACM0", Baud = 115200 };
        manager.UpdateModem(updated);

        var result = manager.GetModem("2m");
        result!.Baud.Should().Be(115200);
    }

    [Fact]
    public void UpdateModem_NonExistingId_Throws()
    {
        var configJson = """
        {
            "password": "test",
            "modems": []
        }
        """;

        var path = CreateTempConfig(configJson);
        var manager = new ConfigManager(path);
        manager.Load();

        var action = () => manager.UpdateModem(new Config { Id = "nonexistent", ComPort = "/dev/ttyACM0" });
        action.Should().Throw<KeyNotFoundException>();
    }

    [Fact]
    public void AddModem_NewId_AddsToList()
    {
        var configJson = """
        {
            "password": "test",
            "modems": []
        }
        """;

        var path = CreateTempConfig(configJson);
        var manager = new ConfigManager(path);
        manager.Load();

        manager.AddModem(new Config { Id = "new", ComPort = "/dev/ttyACM0" });

        manager.Config.Modems.Should().HaveCount(1);
        manager.GetModem("new").Should().NotBeNull();
    }

    [Fact]
    public void AddModem_ExistingId_Throws()
    {
        var configJson = """
        {
            "password": "test",
            "modems": [
                { "id": "existing", "comPort": "/dev/ttyACM0" }
            ]
        }
        """;

        var path = CreateTempConfig(configJson);
        var manager = new ConfigManager(path);
        manager.Load();

        var action = () => manager.AddModem(new Config { Id = "existing", ComPort = "/dev/ttyACM1" });
        action.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void RemoveModem_ExistingId_RemovesFromList()
    {
        var configJson = """
        {
            "password": "test",
            "modems": [
                { "id": "2m", "comPort": "/dev/ttyACM0" },
                { "id": "70cm", "comPort": "/dev/ttyACM1" }
            ]
        }
        """;

        var path = CreateTempConfig(configJson);
        var manager = new ConfigManager(path);
        manager.Load();

        manager.RemoveModem("2m");

        manager.Config.Modems.Should().HaveCount(1);
        manager.GetModem("2m").Should().BeNull();
        manager.GetModem("70cm").Should().NotBeNull();
    }

    [Fact]
    public void ValidatePassword_CorrectPassword_ReturnsTrue()
    {
        var configJson = """
        {
            "password": "secret123",
            "modems": []
        }
        """;

        var path = CreateTempConfig(configJson);
        var manager = new ConfigManager(path);
        manager.Load();

        manager.ValidatePassword("secret123").Should().BeTrue();
    }

    [Fact]
    public void ValidatePassword_WrongPassword_ReturnsFalse()
    {
        var configJson = """
        {
            "password": "secret123",
            "modems": []
        }
        """;

        var path = CreateTempConfig(configJson);
        var manager = new ConfigManager(path);
        manager.Load();

        manager.ValidatePassword("wrongpassword").Should().BeFalse();
    }

    [Fact]
    public void ValidatePassword_EmptyConfigPassword_RejectsUntilFirstLoginSetsIt()
    {
        var configJson = """
        {
            "password": "",
            "modems": []
        }
        """;

        var path = CreateTempConfig(configJson);
        var manager = new ConfigManager(path);
        manager.Load();

        manager.ValidatePassword("anything").Should().BeFalse();
        manager.TrySetInitialPassword("").Should().BeFalse();
        manager.TrySetInitialPassword("first").Should().BeTrue();
        manager.TrySetInitialPassword("second").Should().BeFalse();
        manager.ValidatePassword("first").Should().BeTrue();
        manager.ValidatePassword("second").Should().BeFalse();

        // Saved, so it survives a restart
        var reloaded = new ConfigManager(path);
        reloaded.Load().Password.Should().Be("first");
    }

    [Fact]
    public void Load_OldArrayFormat_MovesMqttSettingsToGlobal()
    {
        var configJson = """
        [
            { "id": "2m", "comPort": "/dev/ttyACM0" },
            { "id": "70cm", "comPort": "/dev/ttyACM1", "mqttServer": "mqtt.lan:1883", "mqttUsername": "u", "mqttPassword": "p" }
        ]
        """;

        var path = CreateTempConfig(configJson);
        var manager = new ConfigManager(path);
        var config = manager.Load();

        config.MqttServer.Should().Be("mqtt.lan:1883");
        config.MqttUsername.Should().Be("u");
        config.MqttPassword.Should().Be("p");
        config.Modems.Should().HaveCount(2);
    }

    [Fact]
    public void Save_WriteableFile_SavesCorrectly()
    {
        var configJson = """
        {
            "password": "test",
            "modems": []
        }
        """;

        var path = CreateTempConfig(configJson);
        var manager = new ConfigManager(path);
        manager.Load();

        manager.AddModem(new Config { Id = "new", ComPort = "/dev/ttyACM0" });
        manager.Save();

        // Reload and verify
        var manager2 = new ConfigManager(path);
        var config2 = manager2.Load();
        config2.Modems.Should().HaveCount(1);
        config2.Modems[0].Id.Should().Be("new");
    }

    [Fact]
    public void ModemConfigChanged_Event_FiresOnUpdate()
    {
        var configJson = """
        {
            "password": "test",
            "modems": [
                { "id": "2m", "comPort": "/dev/ttyACM0" }
            ]
        }
        """;

        var path = CreateTempConfig(configJson);
        var manager = new ConfigManager(path);
        manager.Load();

        string? changedId = null;
        manager.ModemConfigChanged += id => changedId = id;

        manager.UpdateModem(new Config { Id = "2m", ComPort = "/dev/ttyACM0", Baud = 115200 });

        changedId.Should().Be("2m");
    }

    [Fact]
    public void Save_KeepsFileModeAndLeavesNoTempFiles()
    {
        if (OperatingSystem.IsWindows())
            return;

        var path = CreateTempConfig("""{ "password": "test", "modems": [] }""");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var manager = new ConfigManager(path);
        manager.Load();

        manager.AddModem(new Config { Id = "6m", ComPort = "/dev/ttyACM2" });
        manager.Save();

        File.GetUnixFileMode(path).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp").Should().BeEmpty();
        new ConfigManager(path).Load().Modems.Should().ContainSingle(m => m.Id == "6m");
    }

    [Fact]
    public void UpdateGlobal_EmptyPassword_DoesNotClearIt()
    {
        var path = CreateTempConfig("""{ "password": "secret", "modems": [] }""");
        var manager = new ConfigManager(path);
        manager.Load();

        manager.UpdateGlobal(password: "");

        manager.ValidatePassword("secret").Should().BeTrue();
        manager.NeedsMigration.Should().BeFalse();
    }

    [Fact]
    public void Modems_IsASnapshot()
    {
        var path = CreateTempConfig("""{ "password": "x", "modems": [ { "id": "2m", "comPort": "a" } ] }""");
        var manager = new ConfigManager(path);
        manager.Load();

        var snapshot = manager.Modems;
        manager.AddModem(new Config { Id = "6m", ComPort = "b" });

        snapshot.Should().HaveCount(1);
        manager.Modems.Should().HaveCount(2);
    }

    [Fact]
    public void Save_ThroughSymlink_UpdatesTheTarget()
    {
        if (OperatingSystem.IsWindows())
            return;

        var real = CreateTempConfig("""{ "password": "x", "modems": [] }""");
        var link = Path.Combine(Path.GetDirectoryName(real)!, "link.conf");
        File.CreateSymbolicLink(link, Path.GetFileName(real));  // relative link
        var manager = new ConfigManager(link);
        manager.Load();

        manager.AddModem(new Config { Id = "6m", ComPort = "/dev/ttyACM2" });
        manager.Save();

        new FileInfo(link).LinkTarget.Should().NotBeNull();
        new ConfigManager(real).Load().Modems.Should().ContainSingle(m => m.Id == "6m");
    }

    [Fact]
    public void Save_FileDeletedWhileRunning_RecreatesIt()
    {
        var path = CreateTempConfig("""{ "password": "x", "modems": [] }""");
        var manager = new ConfigManager(path);
        manager.Load();
        File.Delete(path);

        manager.Save();

        new ConfigManager(path).Load().Password.Should().Be("x");
    }
}
