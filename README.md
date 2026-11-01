# kissproxy / kissproxylib

kissproxy is a serial-to-TCP proxy for serial KISS modems with a **web-based management interface**, KISS parameter filtering/override, NinoTNC mode control, and MQTT tracing support.

Connect your serial KISS modem, make it available on a TCP port for node software, and manage everything through a modern web UI - including real-time statistics, NinoTNC status monitoring, and KISS parameter control.

For a tool which can subscribe to MQTT topics and produce Wireshark-readable output, see [Ax25Mqtt2pcap](https://github.com/M0LTE/Ax25Mqtt2pcap).

kissproxylib is a .NET library version which can be used to integrate a KISS proxy into other applications.

[![ko-fi](https://ko-fi.com/img/githubbutton_sm.svg)](https://ko-fi.com/Y8Y8KFHA0)

## Features

### Web Management Interface
- **Password-protected web UI** for configuration and monitoring
- **Real-time statistics**: frame counts, bytes transferred, connection status
- **Live frame activity**: see last frames to/from modem with hex and ASCII dumps
- **NinoTNC status panel**: firmware version, mode, uptime, packet counts (from TX Test frames)
- **Full configuration editing**: all settings editable via the browser - no config files to edit
- **Multi-modem support**: manage multiple TNCs from a single interface

### KISS Parameter Control
- **Filter commands from node**: block TxDelay, Persistence, SlotTime, TxTail, FullDuplex, or SetHardware commands
- **Override parameter values**: send your own value to the modem instead of the node's; a value is only sent while its filter is on
- **Periodic parameter refresh**: optionally resend parameters at configurable intervals

### NinoTNC Support
- **Mode selection**: set any of the 16 NinoTNC modes (0-15) via the web UI
- **Software control detection**: when DIP switches are set to 1111, shows actual operating mode decoded from firmware
- **TX Test frame parsing**: automatically extracts and displays NinoTNC status information
- **Persist to flash option**: choose whether mode changes are temporary or saved to TNC flash

### Frame Log
- **One line per AX.25 frame** to and from each modem, decoded in-process (AX.25, XID parameters, NET/ROM L3/L4 and NODES), in the console output, so `journalctl -u kissproxy` shows it
- Lines look like `[6m] RX GB7WIN>M0LTE <RR C P R3>`; TX means handed to the modem, which is not proof it went on air

### MQTT Integration
- **Frame tracing**: publish all KISS traffic to MQTT topics
- **Multiple topic formats**: framed, unframed, and decoded
- **Per-modem topics**: organized by hostname, port, and direction

## Quick Start

### Prerequisites
- .NET 10 SDK or later

### Build and Run

```bash
git clone https://github.com/M0LTE/kissproxy.git
cd kissproxy/src/kissproxy
dotnet run
```

On first run, you'll be prompted to set a password. Then access the web UI at `http://localhost:8080` and configure everything from there - add modems, set serial ports, configure KISS parameters, and more. All configuration is done through the browser.

## Configuration Options

All options are configurable via the web UI:

| Field | Description | Default |
|-------|-------------|---------|
| `webPort` | Port for the web management interface | 8080 |
| `password` | Password for web UI authentication | (set on first run) |
| `id` | Unique identifier for each modem | (required) |
| `comPort` | Serial port path | (required) |
| `baud` | Serial baud rate | 57600 |
| `tcpPort` | TCP port for node connections | 8910 |
| `anyHost` | Accept connections from any host | false |
| `mqttServer` | MQTT server (host:port) | null |
| `mqttUsername` | MQTT username | null |
| `mqttPassword` | MQTT password | null |
| `mqttTopicPrefix` | Custom MQTT topic prefix | null |
| `base64` | Publish as base64 instead of raw bytes | false |
| `filterTxDelay` | Block TxDelay commands from node | false |
| `filterPersistence` | Block Persistence commands from node | false |
| `filterSlotTime` | Block SlotTime commands from node | false |
| `filterTxTail` | Block TxTail commands from node | false |
| `filterFullDuplex` | Block FullDuplex commands from node | false |
| `filterSetHardware` | Block SetHardware commands from node | false |
| `txDelayValue` | Override TxDelay in ms (sent only while `filterTxDelay` is on) | null |
| `persistenceValue` | Override Persistence, 0-255 (sent only while `filterPersistence` is on) | null |
| `slotTimeValue` | Override SlotTime in ms (sent only while `filterSlotTime` is on) | null |
| `txTailValue` | Override TxTail in ms (sent only while `filterTxTail` is on) | null |
| `fullDuplexValue` | Override FullDuplex, true/false (sent only while `filterFullDuplex` is on; not in the web UI) | null |
| `ninoMode` | NinoTNC mode to set (0-14) | null |
| `persistNinoMode` | Save mode to TNC flash memory | false |
| `parameterSendInterval` | Resend parameters every N seconds (0=on connect only) | 0 |

## NinoTNC Modes

| Mode | DIP Switches | Description |
|------|--------------|-------------|
| 0 | 0000 | 9600 GFSK AX.25 |
| 1 | 0001 | 19200 4FSK |
| 2 | 0010 | 9600 GFSK IL2P+CRC |
| 3 | 0011 | 9600 4FSK |
| 4 | 0100 | 4800 GFSK IL2P+CRC |
| 5 | 0101 | 3600 QPSK IL2P+CRC |
| 6 | 0110 | 1200 AFSK AX.25 |
| 7 | 0111 | 1200 AFSK IL2P+CRC |
| 8 | 1000 | 300 BPSK IL2P+CRC |
| 9 | 1001 | 600 QPSK IL2P+CRC |
| 10 | 1010 | 1200 BPSK IL2P+CRC |
| 11 | 1011 | 2400 QPSK IL2P+CRC |
| 12 | 1100 | 300 AFSK AX.25 |
| 13 | 1101 | 300 AFSKPLL IL2P |
| 14 | 1110 | 300 AFSKPLL IL2P+CRC |
| 15 | 1111 | Set from KISS (software control) |

When DIP switches are set to 1111, the TNC accepts mode changes via KISS SETHW commands. The web UI will show "(via KISS = X / XXXX)" to indicate software-controlled mode.

## MQTT Topics

Traffic is published to topics named:

```
kissproxy/$hostname/$id/$direction
```

Where `$direction` is `toModem` or `fromModem`.

Sub-topics:
- `/framed` - Raw KISS traffic with framing intact
- `/unframed/$kissport/$kisscommand` - Unpacked frame data
- `/decoded/$kissport` - Human-readable, one line per frame, same text as the frame log

## Linux Installation

### Install as systemd service

```bash
# Build
git clone https://github.com/M0LTE/kissproxy.git
cd kissproxy
dotnet publish src/kissproxy/kissproxy.csproj --configuration Release -p:PublishProfile=src/kissproxy/Properties/PublishProfiles/Linux-arm32.pubxml

# Install
sudo mkdir -p /opt/kissproxy
sudo mv src/publish/* /opt/kissproxy/

# Create service
sudo sh -c 'echo "[Unit]
After=network.target
[Service]
ExecStart=/opt/kissproxy/kissproxy
WorkingDirectory=/opt/kissproxy
Restart=always
[Install]
WantedBy=multi-user.target" > /etc/systemd/system/kissproxy.service'

sudo systemctl enable kissproxy
sudo systemctl start kissproxy
```

Then access the web UI to set up your password and configure modems.

### Configure LinBPQ

In your `bpq32.cfg`, change your KISS port from direct serial:

```
PORT
  PORTNUM=1
  ID=VHF
  TYPE=ASYNC
  PROTOCOL=KISS
  IPADDR=127.0.0.1
  TCPPORT=8910
  ...
ENDPORT
```

## kissproxylib

A .NET library for integrating KISS proxy functionality into other applications.

```bash
dotnet add package m0lte.kissproxylib
```

Basic usage:

```csharp
var proxy = new KissProxy("mytnc", logger);
await proxy.Run(modemConfig, globalConfig, modemState, cancellationToken);
```

## Web UI Features

The web interface provides:
- Connection status indicators (node connected, serial open)
- Live frame counters and byte statistics
- Last frame activity with direction, command type, and payload details
- NinoTNC status panel (when TX Test frames are received)
- Full configuration editing for all parameters
- Serial port dropdown with auto-detected ports
- Add/remove modems dynamically
- Save configuration with one click

## Licence

GNU Affero General Public License v3.0 or later. See [LICENSE](LICENSE).
