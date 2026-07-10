# DWMP HORDE — Logging for public testers

## Defaults

| Setting | Default | Meaning |
|---------|---------|---------|
| `LogPreset` | **Trace** | **Full logging** — all categories + high-frequency Trace |
| `LogMinLevel` | Trace | Nothing filtered by level under Trace preset |
| `LogRedactIPs` | true | Mask IPv4 in log lines |
| `LogRedactPaths` | true | Absolute paths → filename only |
| `LogIncludeStacks` | false | Full stacks if true (also on for Dev/Trace errors) |
| `VerboseLogging` | false | **Deprecated** — forces Trace if preset is Public |
| `VerboseLightSync` | false | Extra light-transition logs (optional) |

Config file: `BepInEx/config/com.darkwood.horde.cfg` section **`[Logging]`**.  
**Restart the game after changing LogPreset** (filters apply at plugin load).

**Note:** Existing configs that already have `LogPreset=Public` keep that until you change or delete the line. New installs get Trace.

## Presets

| Preset | Who | Categories at Event |
|--------|-----|---------------------|
| **Trace** | **Default** — full debug for testers | Everything + high-frequency Trace (large logs) |
| **Public** | Quiet play (opt-in) | Core, Network, Session, Dream, Death, Save |
| **Support** | Mid-size bug packs | Public + Combat, Entity, World, Container |
| **Dev** | All Event, less spam than Trace | All categories at Event only |

Optional: `LogExtraCategories=Combat,AI` when using Public/Support.

## What you get on Trace (default)

Everything: session lifecycle, combat, entities, physics traces, legacy verbose gates, etc. Logs can get **large** after long sessions — trim or zip when uploading.

For quieter play: set `LogPreset=Public`.

## How to file a bug

1. Leave default **Trace** (or confirm banner `LogPreset=Trace`) on **both** installs, same mod version.  
2. Reproduce once.  
3. Quit cleanly if possible.  
4. Attach **both**:
   - **Host:** `C:\Program Files (x86)\Steam\steamapps\common\Darkwood\BepInEx\LogOutput.log`
   - **Client:** `C:\MyProjects\SecondDarkwood\Darkwood\BepInEx\LogOutput.log` (or your second install’s `BepInEx\LogOutput.log`)
5. Note: steps, host vs client who saw the bug, day/chapter if known.

## Tags in the log

Lines look like `[DWMP/Net] Handshake OK — assigned PlayerId=2`.

| Tag | Category |
|-----|----------|
| `[DWMP]` | Core |
| `[DWMP/Net]` | Network |
| `[DWMP/Session]` | Session / chapter |
| `[DWMP/Combat]` | Combat (Support+) |
| `[DWMP/Entity]` | Entities (Support+) |
| `[DWMP/Dream]` | Dreams |
| `[DWMP/Death]` | Death / night resolve |
| `[DWMP/Save]` | Save share / backups |

## Dev notes

- Prefer `ModLog.Event/Warn/Error/Trace(LogCat, …)` over raw `Log.LogInfo`.  
- `ModRuntime.LegacyInfo` is **Dev/Trace only** (legacy uncategorized paths).  
- `ModRuntime.VerboseLogging` is set true only under Trace (or Dev+Trace min level).  
- Light transition logs: `VerboseLightSync=true` (independent).
