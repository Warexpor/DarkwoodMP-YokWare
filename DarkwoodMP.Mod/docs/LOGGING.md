# Logging guide

What the mod writes to the game log, how to get more of it, and what to send with a
bug report.

## More detail for a bug report

1. Close the game.
2. Open the settings file (below) and, under `[Logging]`, set `LogPreset = Trace`.
3. Start the game, play until the problem shows, then quit.
4. Send `BepInEx/LogOutput.log` (MelonLoader: `MelonLoader/Latest.log`) from the
   game folder of **every** player, before anyone starts the game again: the file is
   overwritten on each launch.

`Trace` logs are large and cost some frame time; set `LogPreset = Support` again for
normal play.

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
4. Or run the gate script (defaults to a Steam install plus a `SecondDarkwood` copy; see the script for its paths):

```bash
./scripts/check-dualbox-perf.sh
```

5. Look for `[Perf] role=Host|Client` every ~2s while hosting (peers or none) or connected as a client.
   `over=N/B ms` counts frames over 1.5 times the frame budget `B`, `gc=N` the garbage collections,
   `worst[net= log= gc= work= wait= fixed=]` what the longest frame held (network tick ms, log lines written, collections) and how it split: `work` from its first physics step or Update to the end of rendering on the CPU, `wait` from there to the next frame (present, vsync, GPU, compositor), `fixed` the physics steps it ran, with `(phys upd co late rend)` splitting `work` into physics steps, Update, coroutines, LateUpdate and rendering. `avg[work phys upd co late rend workOver]` is the same split averaged per frame over the window; `workOver` counts frames whose work alone ran over the budget. With `DWMP_FRAME_PROFILE=1` in the game's environment (test runs only: it times every call) `[FrameProf]` lines name the Unity methods and cameras that took the most in each slow frame, a per-frame average every minute, and each desync section that took 0.5 ms or more.
   Immediate single-frame hitches also emit `[PerfCliff]` (rate-limited ~0.5s).
   Hot Update segments emit `[PerfSeg]` when ≥25ms.

| Tag | When |
|-----|------|
| `[Perf]` | Healthy 2s report |
| `[PerfCliff]` | Window fps~ &lt;15 **or** maxMs / single-frame ≥100ms |
| `[PerfSeg]` | Named Update segment spike (≥25ms) |
| `[EntTimeline]` | Client, every 5s with the probe or entity tracing on: creature motion health (below) |

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
| `hostEntTick ms avg/max` | Host only: real time between entity snapshot ticks (target 50) |
| `segMax` | Hottest Update segment in the window |

**Host clean / client hitch:** compare `role=Host` vs `role=Client` Perf lines.

`[EntTimeline]` fields (frames of creatures whose samples are arriving; a resting body is not counted):

| Field | Meaning |
|-------|---------|
| `interp%` / `coast%` / `hold%` / `before%` | Drawn between two host samples (want ~100) / coasting past the newest / holding for the next / before the oldest |
| `delayMs avg` | Render delay behind the host clock |
| `gapMs n/avg/max` | Host time between one body's samples (50 near a remote, 100 far) |
| `lateMs n/avg/max`, `jitter mean/dev/margin` | Batch lateness against the best case, and the margin the delay adds for it |
| `clock off/target` | Host clock offset in use and the windowed best case it slews to |
| `worst id=… coast+hold%` | The body that waited most, with its interval and delay |

## How to file a bug

1. Same mod version on host + client.  
2. Support for join bugs; **Trace** for maximum capture (Legacy + Verbose).  
3. Quit cleanly.  
4. Attach both LogOutput.log files + steps.

| Install | Linux (Steam) | Windows (Steam) |
|---------|-------|---------|
| BepInEx | `~/.local/share/Steam/steamapps/common/Darkwood/BepInEx/LogOutput.log` | `C:\Program Files (x86)\Steam\steamapps\common\Darkwood\BepInEx\LogOutput.log` |

A GOG or second install has the same `BepInEx/LogOutput.log` inside its own game
folder. MelonLoader installs write `MelonLoader/Latest.log` in the game folder instead.

## Desync check

With `DesyncCheck` on (default), the host sends each settled client a fingerprint of the
state they should share every `DesyncCheckIntervalSec` seconds; the client compares it with
its own world. A difference seen in two checks in a row is logged once as a warning, and again
when it goes away:

```
[YokWare/Session] [Desync] DESYNC Doors door_wood_01@-1204,2,388: host=d=6|open=1|... client=d=6|open=0|...
[YokWare/Session] [Desync] resolved Doors door_wood_01@-1204,2,388
```

The host log carries the same lines as `[Desync p2] ...` (p2 = the client). Sections: Clock,
Flags, Npcs, Players, Doors, Creatures, Traps, Generators, Pickups, Drops, Containers, Burning,
Traders, Journal, Night, World. Doors, creatures, traps, generators, pickups, containers and
fires are compared only near that client (`d=` is the distance); containers only once the
client has opened them (loot is rolled on the host). No check runs during a dream, a join or
a world share. Every five minutes the client logs a `[Desync] check #N` line with how many
desyncs are open. `grep -n "DESYNC" LogOutput.log` lists them.

## Tags

| Tag | Category |
|-----|----------|
| `[YokWare]` | Core (includes `[Perf]`) |
| `[YokWare/Net]` | Network |
| `[YokWare/Session]` | Session / join / bulk |
| `[YokWare/Combat]` | Combat |
| `[YokWare/Entity]` | Entities |
| `[YokWare/Phys]` | Physics: pushed objects, door swings |
| `[YokWare/Loot]` | Containers |
| `[YokWare/World]` | World |
| `[YokWare/AI]` | Creature targeting |
| `[YokWare/Dream]` | Dreams |
| `[YokWare/Death]` | Death |
| `[YokWare/Audio]` | Sounds and voice |
| `[YokWare/UI]` | Menus and HUD |
| `[YokWare/Save]` | Save share |

`LogExtraCategories` and `LogTraceCategories` add categories on top of a preset by their
names in [`CONFIG.md`](CONFIG.md) (`Physics`, `Container`, `Audio`...).

Host creature targeting (AI category, Trace level, host with remote players only):

| Line | When |
|------|------|
| `[TargetSwitch] <creature> host→p2 d=120→60 beh=chasingTarget via <path> (<reason>)` | A creature's target moved from one player body to another. `via` names the writer: `canSeeEnemy`, `checkForNewEnemyCloserThanTarget`, `attackPlayer`, `sniff`, `reactToCharacter` (hit or bump), `birdArea`, `onBansheeSeePlayer`, `attackCharacter` (a vanilla caller), or `unrouted (between sight checks)` for a writer that bypasses the arbiter (a bug). Reason: `Acquire`, `Keep`, `CurrentLost`, `ClearlyNearer`, `Pinned`. Per creature, at most every 0.25 s with a suppressed count. |
| `[TargetHold] <creature> on host (d=…) over <check>'s p2 (d=…)` | Vanilla's check would have moved the creature to another player and the arbiter kept it on its current one (every 2 s per creature). |
| `[AIChase]` | Once a second per creature chasing a stand-in: how far its chase point lags behind it. |

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
