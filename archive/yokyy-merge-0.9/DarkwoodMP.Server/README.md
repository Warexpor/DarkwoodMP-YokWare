# YokWare Branch — Dedicated Server

Standalone **reliable relay** for YokWare Branch **0.9** (Ironbark Protocol **v2**).  
Runs on **.NET 8.0**. License: **GPLv3** (same as the mod).

**Default model:** `AuthoritativeWorld=false` — the server does **not** simulate the Darkwood world. In-game **time/sim authority** is the client with the lowest player id (`IsTimeAuthority`). Enemies use `EntityState` / `EntitySpawn` from game peers (relay only).

## Requirements

- **.NET 8.0 SDK or Runtime** ([download](https://dotnet.microsoft.com/download/dotnet/8.0))
- **UDP port 7777** accessible (or your chosen port)
- **world.json** from a Darkwood save game (see Setup)

## Quick Start

### 1. Build

```bash
cd DarkwoodMP.Server
dotnet build
```

### 2. Create Configuration

Create `appsettings.json` in the server directory:

```json
{
  "Port": 7777,
  "MaxPlayers": 8,
  "Password": "",
  "SaveInterval": 60,
  "PositionSyncRate": 10,
  "EnemySyncRate": 5,
  "TickRate": 30
}
```

### 3. Run

```bash
dotnet run
```

The server will start listening on the configured port and log activity to both console and `logs/darkwoodmp-YYYY-MM-DD.txt`.

## Configuration

All settings are in `appsettings.json`. The server also supports **environment variables** and **command-line arguments** for any config value.

### Options

| Setting | Default | Description |
|---------|--------|-------------|
| `Port` | `7777` | UDP port to listen on |
| `MaxPlayers` | `8` | Maximum connected players |
| `Password` | `""` | Session password (empty = no password) |
| `SaveInterval` | `60` | Auto-save world state every N seconds |
| `PositionSyncRate` | `10` | Send position updates every Nth tick |
| `EnemySyncRate` | `5` | Broadcast enemy state every Nth tick |
| `TickRate` | `30` | Server updates per second |

### Environment Variables

Override any setting with an environment variable:

```bash
export PORT=8888
export MAXPLAYERS=4
export PASSWORD=secret123
dotnet run
```

### Command-Line Arguments

```bash
dotnet run -- --Port 8888 --MaxPlayers 4 --Password secret123
```

## Server Behavior

### Tick Loop
- Runs at `TickRate` (default 30 Hz)
- Advances game time and day progression
- Broadcasts sync packets at configured rates
- Auto-saves world state at configured interval

### Player Management
- Players connect via UDP with a `ConnectRequest` packet containing name and password
- Server assigns a numeric ID to each player
- Player IDs are **reused** when a player disconnects and reconnects
- Players with no activity for **15 seconds** are automatically disconnected

### Packet Types

| Packet | Direction | Reliability |
|--------|-----------|-------------|
| ConnectRequest/Response | Client → Server | Reliable |
| PositionUpdate | Bidirectional | Unreliable |
| HealthUpdate | Bidirectional | Unreliable |
| EnemyUpdate | Server → Clients | Unreliable |
| DoorState | Bidirectional | Unreliable |
| PickupState | Bidirectional | Unreliable |
| GameStateSync | Server → Clients | Unreliable |
| DayNightUpdate | Bidirectional | Unreliable |
| EventTrigger | Server → Clients | Unreliable |
| ChatMessage | Bidirectional | Reliable |
| SystemMessage | Server → Clients | Reliable |
| PlayerJoined/Left | Server → Clients | Reliable |
| PlayerList | Server → Clients | Reliable |
| Heartbeat | Server → Clients | Unreliable |
| HeartbeatAck | Client → Server | Reliable |

### World State

The server maintains a `world.json` file in its working directory containing:
- Player positions and states
- Enemy positions and states
- Door open/close states
- Pickup spawn states
- Current day number and time of day
- Total playtime

This file is written automatically on a timer and on shutdown.

### Logs

- **Console**: Real-time output with log levels
- **File**: `logs/darkwoodmp-YYYY-MM-DD.txt` (daily rolling)

Log levels: Information, Warning, Error, Verbose.

## Network Protocol

- **Transport**: UDP via LiteNetLib 2.x
- **Packet format**: 1-byte type tag + serialized payload
- **Serialization**: NetDataWriter (put/get int, float, double, bool, string)
- **No TLS**: Connections are plaintext (LAN-only design)

## Shutdown

Send `Ctrl+C` to gracefully shut down. The server will:
1. Save the current world state
2. Disconnect all players
3. Close the UDP socket
4. Exit cleanly

## Multiplayer Setup

The server runs independently of the game. Clients connect via the DarkwoodMP mod:

1. Run the server on any machine on your network
2. Clients set `ServerIp` to the server machine's local IP
3. Clients set `ServerPort` to match the server's `Port`
4. If `Password` is set, clients must enter the same password in their config

**Port forwarding** is required for clients outside your local network (see MULTIPLAYER_SETUP.md).

## Building

```bash
cd DarkwoodMP.Server
dotnet build -c Release
```

The output DLLs will be in `bin/Release/net8.0/`.

## File structure

```
DarkwoodMP.Server/
├── Program.cs
├── Config/ServerConfig.cs          # AuthoritativeWorld (default false = relay)
├── Models/                         # Connection + optional world cache
├── Services/
│   ├── ServerHostService.cs        # UDP + hop reliability + Ironbark MessageId relay
│   ├── ConnectionService.cs        # Auth, version reject
│   ├── PacketRegistryService.cs    # Shared wire codecs
│   ├── WorldCacheService.cs        # World transfer cache
│   └── …
├── DarkwoodMP.Server.csproj        # Compiles ../DarkwoodMP.Protocol/**/*.cs (single wire truth)
└── README.md
```

Enemy population is **not** simulated here. Live peers use `EntityState` / `EntitySpawn`; legacy `EnemyUpdate` is relay-only.
