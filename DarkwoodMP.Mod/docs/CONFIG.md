# YokWare Branch configuration

One INI file, same section/key shape on both loaders. It is created on first launch
with every key and its description; edit it with the game closed.

| Loader | File |
|--------|------|
| BepInEx | `BepInEx/config/com.yokware.branch.cfg` |
| MelonLoader | `UserData/YokWare/com.yokware.branch.cfg` |

Values are read once at startup (restart after changing them). Bool values are
`true` / `false`; numbers use `.` as the decimal separator. Defaults below are the
values written into a fresh file; an existing file keeps what it already has.

The table is checked against `Config/ModConfig.cs` by the `ConfigDocTests` unit
test, so every bound key must be listed here.

## Network

| Key | Default | Meaning |
|-----|---------|---------|
| `ConnectAddress` | `127.0.0.1` | Default host IP shown in the F2 connect field and used by JOIN LAN. |
| `ConnectPort` | `7788` | Default UDP port for hosting and joining on LAN (1-65535; out-of-range values are clamped with a warning). |
| `HostPassword` | empty | Optional join password. Empty means open LAN. Host and every client must match. Also used as the Steam lobby connection key. |
| `SteamLobbyId` | empty | Steam lobby id (ulong) for JOIN STEAM. The host fills it in when creating a Steam lobby. Friends can also join through the Steam invite overlay. |
| `SteamLobbyType` | `friends` | Steam host lobby visibility: `friends`, `public` or `private`. |
| `PlayerName` | `Player` | Name shown in co-op chat and speech bubbles. |
| `ChatEnabled` | `true` | Co-op text chat. Ctrl+C opens the input, Enter sends, Esc closes. Gameplay input (movement, hotbar, walkie) is held while the box is open. |
| `MaxPlayers` | `8` | Maximum players including the host. |
| `AllowJoinDuringDream` | `false` | When false, joins are rejected while a dream session is running. |
| `HostMigrationEnabled` | `true` | When the host crashes or times out, the survivors elect the lowest remaining player id as the new host (LAN and Steam) and reconnect to it. |

## Saves

| Key | Default | Meaning |
|-----|---------|---------|
| `SaveRootOverride` | empty | Absolute path for save data (`1_4Save/profs`). Empty means the Unity default; a SecondDarkwood install auto-isolates to `LocalLow/.../Darkwood_Second`. Set it manually if a dual-box setup still shares a save tree. |
| `PreferredCoopCopySlot` | `0` | Last local profile slot (1-5) used for a permanent co-op world copy; 0 means none. The join slot picker highlights it; empty slots are still preferred when free. |

## Gameplay

| Key | Default | Meaning |
|-----|---------|---------|
| `FriendlyFireEnabled` | `true` | Players can damage each other. |
| `DoubleItemsEnabled` | `true` | Master switch for loot sharing (hideout furnace fuels and `isExpItem` items). |
| `LootShareMode` | `ScaleWithPlayers` | `Off` or `ScaleWithPlayers` (1 + remote peers). Scales hideout fuels (mushrooms, odd meat, eggs and similar); not wood, nails or regular dog meat. |
| `NamedNpcScaleEnabled` | `true` | Host: multiply allowlisted dream NPC presence by the party multiplier. |
| `NamedNpcAllowlist` | `ChomperBlack` | Comma-separated character short names scaled in dreams only (not night hideout trash). |
| `MaxPeerDamage` | `200` | The host clamps peer-reported attack / friendly-fire damage to this maximum per hit (anti-grief). Separately, each peer has a fixed hit budget (20 hits/s with a burst of 40, 1000 damage/s with a burst of 3000): bursts such as shotgun pellets apply in full, sustained spam is rejected. |
| `PeerMovementVolume` | `0.75` | Volume of other players' movement on this machine: footsteps, clothes rustle, dodge and landing steps (0..1). Their other sounds (shots, hits, tools) are not affected. |

## Voice

Steam Voice needs a running, logged-on Steam client; transport is independent
(LAN or Steam sessions both work).

| Key | Default | Meaning |
|-----|---------|---------|
| `VoiceEnabled` | `true` | Proximity and walkie voice chat when the Steam client is logged on. |
| `VoiceMode` | `ptt` | `ptt` is push-to-talk (`VoicePttKey`); `open` always transmits while connected. |
| `VoicePttKey` | `V` | Unity `KeyCode` name for push-to-talk. |
| `VoiceVolume` | `1` | Playback volume multiplier for remote voice. |
| `VoiceGain` | `1.4` | Gain applied after Steam `DecompressVoice`. |
| `VoiceFullVolumeDistance` | `150` | Distance (game units; a body is about 40 across) within which proximity voice is at full volume. |
| `VoiceMaxDistance` | `650` | Distance (game units) beyond which proximity voice is silent, the same range as other sounds from peers. |
| `WalkieItemName` | `walkie_talkie` | Inventory item type for the walkie radio. Hold it and press RMB to transmit (not while a menu, container, dialogue, map, journal or chat box is open); carrying one enables radio reception. |

## Logging

See [`LOGGING.md`](LOGGING.md) for what each preset prints.

| Key | Default | Meaning |
|-----|---------|---------|
| `LogPreset` | `Support` | `Public` is quiet; `Support` adds session/join/combat (`[Perf]` only with `Debug.PerfProbe`); `Dev` adds `LegacyInfo` dumps and `[Perf]`; `Trace` is maximum capture. Restart after changing it. |
| `LogMinLevel` | `Event` | `Error`, `Warn`, `Event`, `Info` or `Trace`. `LegacyInfo` runs on `LogPreset` Dev or Trace. |
| `LogExtraCategories` | empty | Extra Event categories on top of the preset, comma separated: Core, Network, Session, Combat, Entity, Physics, Container, World, AI, Dream, Death, Audio, UI, Save. |
| `LogTraceCategories` | `none` | Categories allowed to emit Trace when the preset is not full Trace, or `none`. |
| `LogRateLimitSeconds` | `1` | Minimum seconds between identical `TraceRate` keys (spam guard). |
| `LogRedactPaths` | `false` | Strip absolute paths to file names in log lines (safer for pastebins). |
| `LogRedactIPs` | `true` | Mask IPv4 addresses in log lines. Set false to see full IPs on your own LAN. |
| `LogIncludeStacks` | `true` | Include full exception stacks on Error (always on for Dev and Trace). |

## Debug

| Key | Default | Meaning |
|-----|---------|---------|
| `VerboseLogging` | `false` | Legacy switch: with `LogPreset=Public` it forces Trace. Prefer `LogPreset=Support` for join tests. |
| `VerboseLightSync` | `false` | Transition-only light sync logs (flare/flash on/off, join bulk). |
| `VerboseEntitySync` | `false` | Deep entity sync logs (anim, interpolation, hit reaction, damage, spawn/despawn), rate-limited. Also enables Entity/Combat/AI Trace under Support. Turn on for dual-box diagnosis. |
| `FreeCursorForDualBox` | `false` | Force `Cursor.lockState = None` (skip vanilla Confined). Linux dual-box testers on Hyprland/Wayland with a Wine/Proton SecondDarkwood set this to `true`: Confined ClipCursor traps the mouse and the blur-release can freeze the Wine window. The game's own cursor (camera look, aim, throws) is still held inside the window as the confine would, so the camera cannot run past vanilla's look distance. Leave false for normal single-window play. |
| `PerfProbe` | `false` | Co-op frame-cost probe: per-frame timing plus a `[Perf]` line every 2 s while connected. Always on under `LogPreset` Dev/Trace; set `true` to capture it under Support/Public. |
| `DesyncCheck` | `true` | Desync checker: the host sends each client a fingerprint of the world state they should share (clock, flags, doors, containers, nearby creatures and items, players...). The client compares it with its own and logs a difference that lasts two checks in a row as a `DESYNC` line, and `resolved` when it goes away; the host log gets the same lines tagged with the client. Needs the host's setting on; a client with it off ignores the digests. |
| `DesyncCheckIntervalSec` | `15` | Seconds between desync checks (clamped 5..300). |
