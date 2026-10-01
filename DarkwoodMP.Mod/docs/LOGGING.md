# DWMP Horde logging guide

## Defaults

| Setting | Default | Meaning |
|---------|---------|---------|
| `LogPreset` | **Support** | Session/join/combat Events without Legacy flood (`[Perf]` needs `PerfProbe`) |
| `LogMinLevel` | Event | Drop Info/Trace under Support |
| `LogRedactIPs` | **true** | Mask IPv4 in log lines (privacy-safe for shared packs; set false to see full IPs on your own LAN) |
| `LogRedactPaths` | false (local dual-box) | Absolute paths → filename only |
| `LogIncludeStacks` | true | Full stacks on Error |
| `VerboseLogging` | false | Deprecated compatibility switch; forces Trace when the preset is Public |
| `VerboseLightSync` | false | Extra light-transition logs (optional) |
| `VerboseEntitySync` | false | Deep entity anim/interp/reaction/damage Trace+Event (rate-limited). Turn on for dual-box diagnosis |
| `PerfProbe` | false | Co-op frame-cost probe and the `[Perf]` lines. Always on under Dev/Trace; turn on to get them under Support/Public |

Config file (section **`[Logging]`**, plus **`[Debug]`** for the `Verbose*` and `PerfProbe` switches):

- BepInEx: `BepInEx/config/com.yokware.branch.cfg`
- MelonLoader: `UserData/YokWare/com.yokware.branch.cfg`

Every key is listed in [`CONFIG.md`](CONFIG.md).  
**Restart the game after changing LogPreset.**

## Presets

| Preset | Who | What you get |
|--------|-----|----------------|
| **Public** | Quiet play | Core, Network, Session, Dream, Death, Save Events |
| **Support** | **Default** playtest / bug packs | Public + Combat, Entity, World, Container (+ **`[Perf]`** with `PerfProbe=true`) |
| **Dev** | Dual-box deep debug | All Event cats + **`LegacyInfo`** dumps + **`[Perf]`** (large logs) |
| **Trace** | **Max capture** | All categories + Trace + **`LegacyInfo`** + **`VerboseLogging` gates** (largest logs; dual-box FPS cost) |

**Important:** `ModRuntime.LegacyInfo` runs on `LogPreset=Dev` or **`Trace`**
(below that its messages are not even formatted). Use **Trace on both** installs
for maximum dual-box capture.

## Stutter / hitch triage (dual-box)

1. Prefer **Trace** (max), or **Support** with `PerfProbe = true`, on **both** installs, same mod build.  
2. Reproduce; quit cleanly.  
3. Attach **both** `BepInEx/LogOutput.log` files.  
4. Or run the gate script (defaults to this machine’s Steam + SecondDarkwood paths):

```bash
./scripts/check-dualbox-perf.sh
```

5. Look for `[Perf] role=Host|Client` every ~2s while co-op connected.
   Immediate single-frame hitches also emit `[PerfCliff]` (rate-limited ~0.5s).
   Hot Update segments emit `[PerfSeg]` when ≥25ms.

| Tag | When |
|-----|------|
| `[Perf]` | Healthy 2s report |
| `[PerfCliff]` | Window fps~ &lt;15 **or** maxMs / single-frame ≥100ms |
| `[PerfSeg]` | Named Update segment spike (≥25ms) |

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
| `segMax` | Hottest Update segment in the window |

**Host clean / client hitch:** compare `role=Host` vs `role=Client` Perf lines.

Full dual-box soak steps: [`PLAYTEST.md`](PLAYTEST.md).

## How to file a bug

1. Same mod version on host + client.  
2. Support for join bugs; **Trace** for maximum capture (Legacy + Verbose).  
3. Quit cleanly.  
4. Attach both LogOutput.log files + steps.

| Install | Linux | Windows |
|---------|-------|---------|
| Host (Steam) | `~/.local/share/Steam/steamapps/common/Darkwood/BepInEx/LogOutput.log` | `...\Steam\steamapps\common\Darkwood\BepInEx\LogOutput.log` |
| Client (second) | `.../SecondDarkwood/Darkwood/BepInEx/LogOutput.log` | `...\SecondDarkwood\Darkwood\BepInEx\LogOutput.log` |

MelonLoader installs write `MelonLoader/Latest.log` in the game folder instead.

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
- `LegacyInfo` = Dev or Trace.  
- Join bulk one-shots should use `ModLog.Event(LogCat.Session, …)` so Support packs still work.  
- Perf probe: `CoopPerfProbe` / `ClientPerfProbe` alias on both roles when connected.
