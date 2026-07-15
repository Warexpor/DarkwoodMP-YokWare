# DWMP HORDE — Logging for public testers

## Defaults

| Setting | Default | Meaning |
|---------|---------|---------|
| `LogPreset` | **Support** | Session/join/combat Events + Core `[Perf]` without Legacy flood |
| `LogMinLevel` | Event | Drop Info/Trace under Support |
| `LogRedactIPs` | false (local dual-box) | Mask IPv4 in log lines when sharing packs |
| `LogRedactPaths` | false (local dual-box) | Absolute paths → filename only |
| `LogIncludeStacks` | true | Full stacks on Error |
| `VerboseLogging` | false | **Deprecated** — forces Trace if preset is Public |
| `VerboseLightSync` | false | Extra light-transition logs (optional) |

Config: `BepInEx/config/com.yokware.branch.cfg` (or `com.darkwood.horde.cfg`) section **`[Logging]`**.  
**Restart the game after changing LogPreset.**

## Presets

| Preset | Who | What you get |
|--------|-----|----------------|
| **Public** | Quiet play | Core, Network, Session, Dream, Death, Save Events |
| **Support** | **Default** playtest / bug packs | Public + Combat, Entity, World, Container + **`[Perf]`** |
| **Dev** | Dual-box deep debug | All Event cats + **`LegacyInfo`** dumps (large logs) |
| **Trace** | High-freq | All cats + Trace; **`VerboseLogging` gates** — *not* LegacyInfo |

**Important:** `ModRuntime.LegacyInfo` runs **only when LogPreset=Dev**. Trace does **not** enable LegacyInfo.

## Stutter / hitch triage (dual-box)

1. Prefer **Support** or **Dev** on **both** installs, same mod build.  
2. Reproduce; quit cleanly.  
3. Attach **both** `BepInEx/LogOutput.log` files.  
4. Look for `[Perf] role=Host|Client` every ~2s while co-op connected:

| Field | Meaning |
|-------|---------|
| `maxMs` | Worst frame in window |
| `poll` | Network poll + handlers (ms summed) |
| `upd` / `physBuild` | Update rest / physics snapshot build |
| `entApply` / `skip` | Entity snapshot apply cost |
| `pktRx` + `top=` | Packet count + top message types |
| `footN` / `footMs` / `footType` | FindObjectsOfType cost |
| `pend lure=… lock=…` | Pending apply queues |
| `hostEntSend` | Host only: entity broadcast volume |

**Host clean / client hitch:** compare `role=Host` vs `role=Client` Perf lines.

## How to file a bug

1. Same mod version on host + client.  
2. Support for join bugs; Dev if you need Legacy bulk dumps.  
3. Quit cleanly.  
4. Attach both LogOutput.log files + steps.

| Install | Typical path |
|---------|----------------|
| Host (Steam) | `…\Steam\steamapps\common\Darkwood\BepInEx\LogOutput.log` |
| Client (second) | `…\SecondDarkwood\Darkwood\BepInEx\LogOutput.log` |

## Tags

| Tag | Category |
|-----|----------|
| `[YokWare]` | Core (includes `[Perf]`) |
| `[YokWare/Net]` | Network |
| `[YokWare/Session]` | Session / join / bulk |
| `[YokWare/Combat]` | Combat |
| `[YokWare/Entity]` | Entities |
| `[YokWare/World]` | World |
| `[YokWare/Dream]` | Dreams |
| `[YokWare/Death]` | Death |
| `[YokWare/Save]` | Save share |

## Dialog / dream co-op (parity notes)

| Path | Authority |
|------|-----------|
| Personal dialog rewards (items/journal) | Speaking **client** applies; host suppresses on DialogOutcome |
| World flags / world events / dialogue dreams / transport | **Host** only (client defers during `displayNextBoard`) |
| Tree alreadyShown | Client + host; flush every choice + close |
| Dream start | Host `prepareDream`; client sleep → DreamStartRequest; dialogue dreams → DialogOutcome (client clears wantToDream) |
| Dream end rewards | Host `endDreaming` + peers `ApplyRemoteDreamCleanup` |

## Dev notes

- Prefer `ModLog.Event/Warn/Error/Trace(LogCat, …)`.  
- `LegacyInfo` = Dev only.  
- Join bulk one-shots should use `ModLog.Event(LogCat.Session, …)` so Support packs still work.  
- Perf probe: `CoopPerfProbe` / `ClientPerfProbe` alias — Host + Client when connected.
