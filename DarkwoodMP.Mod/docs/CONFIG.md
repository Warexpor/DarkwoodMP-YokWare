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

In game, **Multiplayer > Settings** edits `PlayerName`, `ChatEnabled`, `ShowPlayerNames`,
`VoiceEnabled` / `VoiceMode`, `VoicePttKey`, `VoiceVolume` and `PeerMovementVolume`;
**Multiplayer > Host settings** edits `FriendlyFireEnabled`, `LootShareMode`, `NightMonsterMultiplier`, `MaxPlayers`,
`SteamLobbyType`, `ConnectPort` and `HostPassword`; **Multiplayer > Join** edits
`ConnectAddress`, `ConnectPort`, `HostPassword` and `SteamLobbyId`. A change applies
at once and is written to the file.

## Network

| Key | Default | Meaning |
|-----|---------|---------|
| `ConnectAddress` | `127.0.0.1` | Host address of Multiplayer > Join (Address), used by Connect. |
| `ConnectPort` | `7788` | Default UDP port for hosting and joining on LAN (1-65535; out-of-range values are clamped with a warning). |
| `HostPassword` | empty | Optional join password. Empty means open LAN. Host and every client must match. Also used as the Steam lobby connection key. |
| `SteamLobbyId` | empty | Steam lobby id (ulong) for Multiplayer > Join > Join the lobby. The host fills it in when creating a Steam lobby. Friends can also join through the Steam invite overlay. |
| `SteamLobbyType` | `friends` | Steam host lobby visibility: `friends`, `public` or `private`. |
| `PlayerName` | `Player` | Name the other players see: over your character, in chat and on map pins. Left at `Player`, a Steam player goes by their Steam name. |
| `ChatEnabled` | `true` | Co-op text chat. Ctrl+C opens the input, Enter sends, Esc closes. Gameplay input (movement, hotbar, walkie) is held while typing. |
| `ShowPlayerNames` | `pointed` | When other players' names show over them: `pointed` (when the cursor is on them), `always` (while you can see them) or `off`. |
| `MaxPlayers` | `8` | Maximum players including the host. |
| `AllowJoinDuringDream` | `false` | When false, joins are rejected while a dream session is running. |
| `HostMigrationEnabled` | `true` | When the host crashes or times out, the survivors elect the lowest remaining player id as the new host (LAN and Steam) and reconnect to it. |

## Saves

| Key | Default | Meaning |
|-----|---------|---------|
| `SaveRootOverride` | empty | Absolute path for save data (`1_4Save/profs`). Empty means the Unity default; a SecondDarkwood install auto-isolates to `LocalLow/.../Darkwood_Second` (a ThirdDarkwood one to `Darkwood_Third`). Set it manually if a dual-box setup still shares a save tree. |
| `PreferredCoopCopySlot` | `0` | Last local profile slot (1-5) used for a permanent co-op world copy; 0 means none. The join profile picker marks it "last used"; empty slots are still preferred when free. |

## Gameplay

| Key | Default | Meaning |
|-----|---------|---------|
| `FriendlyFireEnabled` | `true` | Players can damage each other. |
| `DoubleItemsEnabled` | `true` | Master switch for loot sharing (hideout furnace fuels and `isExpItem` items). |
| `LootShareMode` | `ScaleWithPlayers` | `Off` or `ScaleWithPlayers` (1 + remote peers). Scales hideout fuels (mushrooms, odd meat, eggs and similar); not wood, nails or regular dog meat. |
| `NamedNpcScaleEnabled` | `true` | Host: multiply allowlisted dream NPC presence by the party multiplier. |
| `NamedNpcAllowlist` | `ChomperBlack` | Comma-separated character short names scaled in dreams only (not night hideout trash). |
| `MaxPeerDamage` | `200` | The host clamps peer-reported attack / friendly-fire damage to this maximum per hit (anti-grief). Separately, each peer has a fixed hit budget (20 hits/s with a burst of 40, 1000 damage/s with a burst of 3000): bursts such as shotgun pellets apply in full, sustained spam is rejected. |
| `NightMonsterMultiplier` | `1` | Host: how many night monsters come to each hideout compared with vanilla (1 = vanilla, up to 10; Host settings offers x1, x1.5, x2, x3, x4, x5, x7 and x10). Raises how many of each kind may be out at once and how fast they come. Applies at once, also mid-night; works in a solo game too. Worms, shadows and night events are not changed. |
| `PeerMovementVolume` | `0.85` | Volume of other players' movement on this machine: footsteps, clothes rustle, dodge and landing steps (0..1). Their other sounds (shots, hits, tools) are not affected. |

## Voice

Voice is recorded with the game's own microphone input and sent in the mod's own codec,
so it needs no Steam client and works in LAN and Steam sessions alike. Most of these are
set in Multiplayer > Settings > Voice.

| Key | Default | Meaning |
|-----|---------|---------|
| `VoiceEnabled` | `true` | Proximity and walkie voice chat. |
| `VoiceMode` | `ptt` | `ptt` is push-to-talk (`VoicePttKey`); `open` sends whenever you speak (a gate that follows the room's own noise). |
| `VoicePttKey` | `V` | Unity `KeyCode` name for push-to-talk. |
| `VoiceVolume` | `1` | Playback volume multiplier for remote voice. |
| `VoiceGain` | `1.4` | Gain applied to received voice. |
| `VoiceRadioPowerKey` | `B` | Unity `KeyCode` name for the walkie's on/off knob; it turns only with the walkie in hand. |
| `VoiceMicDevice` | empty | Microphone to talk into, by the name the game lists it under; empty for the system default. A device that is not plugged in falls back to the default. |
| `VoiceMicVolume` | `1` | Microphone volume, 0 to 4 (1 as recorded; the Voice screen sets 0 to 2). It is also how loud you count for how far your voice carries. |
| `VoicePlayerVolumes` | empty | How loud each other player is heard, by the name they go by: `name=volume` entries (0 muted to 2) separated by `|`. Set in Settings > Voice > Players. |
| `VoiceFullVolumeDistance` | `150` | Distance (game units; a body is about 40 across) within which a shout is at full volume; quieter speech a shorter way. |
| `VoiceMaxDistance` | `650` | Distance (game units) a shout carries before it is silent, the same range as other sounds from peers. Normal speech carries about three quarters of it, a whisper about a third (the loudness is measured on the talker's machine). |
| `WalkieItemName` | `walkie_talkie` | Inventory item type for the walkie radio. Hold it and press RMB to transmit (not while a menu, container, dialogue, map, journal or chat box is open); switched on and charged, it receives anywhere it is carried, and other live walkies play the talk out loud for players near their carriers. |
| `VoiceAlertsEnemies` | `true` | Host: creatures hear players talk. Speech louder than a murmur is a sound at the talker's position (vanilla `Character.alertInArea`): normal speech about as far as a walking step, a shout farther than running; a whisper is not heard. A walkie playing radio talk is a small sound (160 units) where its carrier stands. Also in Host settings. |

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
