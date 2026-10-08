# DarkwoodMP-YokWare — agent memory (survives compact)

This file is auto-loaded when the session workspace is this repo (or a child of it).
Keep **machine paths** here so post-compact agents do not re-ask.

## Machine paths (authoritative)

**Host OS:** Linux (Omarchy / Arch). Dual-box = native Steam host + Wine GOG client.

| Role | Path |
|------|------|
| **Project (repo)** | `/home/warexpor/Work/MyProjects/DarkwoodMP-YokWare` |
| **Host game (Steam)** | `/home/warexpor/.local/share/Steam/steamapps/common/Darkwood` (native `Darkwood.x86_64`) |
| **Client game (SecondDarkwood)** | `/home/warexpor/Work/MyProjects/SecondDarkwood/Darkwood` (**GOG** Windows build — Wine) |
| **Vanilla decompile** | `/home/warexpor/Archive/Windows-Desktop/Dev/Darkwood DECOMPILED` |
| **Decompile C#** | `/home/warexpor/Archive/Windows-Desktop/Dev/Darkwood DECOMPILED/Scripts/Assembly-CSharp` |
| **Scene/prefab data (YAML)** | `/home/warexpor/Archive/Windows-Desktop/Dev/Darkwood DECOMPILED/Project/ExportedProject/Assets` (AssetRipper 2.0.0 "Unity Project" export from Steam `Darkwood_Data`: 190 `.unity`, 8414 `.prefab`, 2033 `.asset`, materials, decompiled shaders, every component's serialized fields). Query with `scripts/unity-yaml.py` (below). AssetRipper binary: `~/Tools/AssetRipper/AssetRipper.GUI.Free` (headless web API) |
| **Host BepInEx / log** | `…/Darkwood/BepInEx/` → `LogOutput.log` (BepInEx **5.4.23.5** linux-x64 + `run_bepinex.sh`) |
| **Client BepInEx / log** | `…/SecondDarkwood/Darkwood/BepInEx/` → `LogOutput.log` (Windows BepInEx + Doorstop `winhttp`) |
| **Host plugin deploy** | `…/Darkwood/BepInEx/plugins/DarkwoodMP.Mod.dll` |
| **Client plugin deploy** | `…/SecondDarkwood/Darkwood/BepInEx/plugins/DarkwoodMP.Mod.dll` |
| **Host launch** | `darkwood-host` / `scripts/run-darkwood-host.sh` → `./run_bepinex.sh` — **or** Steam Play with Launch Options `./run_bepinex.sh %command%` (bare Steam Play skips Doorstop; mod will not load). Steam Play has no TTY: do **not** `exec` the Doorstop script into a terminal (breaks SteamLaunch / LD_PRELOAD). Game-dir `run_bepinex.sh` instead opens a background **foot** that `tail -F`s `BepInEx/LogOutput.log` on the Steam entry only. Re-apply after BepInEx reinstall: `scripts/install-host-console-wrap.sh`. `DARKWOOD_NO_FOOT=1` skips. Prefer `darkwood-host` for a real BepInEx console TTY. |
| **Third box (2nd client)** | `/home/warexpor/Work/MyProjects/ThirdDarkwood/Darkwood` (copy of the GOG install, PlayerName `Player3`), Proton prefix `…/steamapps/compatdata/thirddarkwood`, saves auto-isolate to `LocalLow/…/Darkwood_Third`. Deploy via `ThirdPlugins` in `GamePath.local.props`. Launch: `SECOND_DARKWOOD_DIR=<third dir> STEAM_COMPAT_DATA_PATH=<thirddarkwood prefix> scripts/run-seconddarkwood.sh`; pilot: `CLIENTS=2 scripts/pilot/pilot-run.sh`, `pcmd.sh c2 …` |
| **Client launch** | `seconddarkwood` / `scripts/run-seconddarkwood.sh` (Steam **Proton Experimental** as Wine runtime + `WINEDLLOVERRIDES=winhttp=n,b`; system `wine` optional if you install it later) |

**Dual-box cursor (machine note):** `FreeCursorForDualBox` defaults to **false** (vanilla confine). On this Linux box (Hyprland/Wayland + Wine/Proton client) both installs' `BepInEx/config/com.yokware.branch.cfg` must set `FreeCursorForDualBox = true`, or the Confined cursor traps the mouse and the Wine window can freeze on blur. Existing cfg files keep their old value; only fresh files get the new default.

Dual-box saves: SecondDarkwood auto-isolates to a sibling `Darkwood_Second` product folder under Unity’s persistent root (do not share the host Steam save tree). Under Proton/Wine (verified): `…/steamapps/compatdata/seconddarkwood/pfx/drive_c/users/steamuser/AppData/LocalLow/Acid Wizard Studio/Darkwood_Second`.

**Steamworks co-op:** SecondDarkwood is **GOG** (`Galaxy64.dll` / `goggame-1578751181.*`) — `SteamManager` never inits there. Dual-box Steam host/join is impossible; use **LAN** for Steam+SecondDarkwood, or two real Steam installs/accounts for SNS. **0.7.47:** Steam↔Steam UI timeout 35s, early invite callbacks, host SNS accept without lobby-member race, relay warm retry. **0.7.48:** host rejects forwarded messages from non-owners and non-lobby Steam SNS connections; FF-off spares host player; LiteNetLib via NuGet. **0.7.77:** `archive/`, `research/`, and local scratch trees deleted from repo.

## Vanilla scene / prefab data (`scripts/unity-yaml.py`)

The C# decompile has the code; the YAML export has the editor-set values (GameEvents wiring, NPC/Character setup, light/sound ranges, spawners, item and door setups). Look there before guessing at runtime. The tool resolves script GUIDs to class names, fileID refs to GameObject paths, external GUIDs to asset paths, and int enums to C# member names. Index cache: `…/Darkwood DECOMPILED/Project/yokware-index.json` (`--reindex` rebuilds).

```bash
scripts/unity-yaml.py find door_bunker_ch1_01 -r --scenes        # GameObjects by name (+ authored world pos)
scripts/unity-yaml.py grep 'name: door_underground$'              # any component field line
scripts/unity-yaml.py tree dream_bunker_underground_01 --root KORYTARZ -d 2 -c
scripts/unity-yaml.py show dream_bunker_underground_01 onLeaveDoorDialogue_dream_underground
scripts/unity-yaml.py refs dream_bunker_underground_01 door_bunker_ch1_01_dream/open   # who targets it
scripts/unity-yaml.py uses GameEvents --paths --in dreams
scripts/unity-yaml.py enum GameEvent.Type ; scripts/unity-yaml.py fields GameEvent
```

Positions are as authored in the scene file (locations are moved at runtime, e.g. the dream pad to ~-75000). Location scenes also exist as `Resources/locationpresets|dreampresets/*.prefab`; a bare file name prefers the `.unity`.

## Dream bunker “dialogue door” (fact)

Not a normal hinged `Door` → do **not** use `[DoorSync]` / `Door.open` as the success signal.

| Piece | Role |
|-------|------|
| `door_underground` | Dialogue NPC (talk target), not the blocker mesh. `door_underground` is the `NPC.name` field; the GameObject is `Door_talkable_outside_bunker_underground_02` (dream scene has it twice: root and `Characters/`) |
| `door_bunker_ch1_01` / `door_bunker_ch1_01_dream` | Closed visual / collider state |
| `door_bunker_ch1_01_open` | Open visual / passable state |
| `onLeaveDoorDialogue_dream_underground` | GameEvents fired on dialogue close (`onCloseDialogue`) that swaps closed↔open |

Sync path is **GameEventsFired** (host fires leave-door GE → clients apply), plus any setActive/swap children — not DoorOpen fan-out.

**Clone trap:** `door_underground` (NPC.name), `door_bunker_ch1_01` and `door_bunker_ch1_01_open` also exist in the **overworld bunker** (`outside_bunker_underground_02`), where the swap is driven by `goThroughDoor_outside_bunker_underground_02` / `ifDoctorTrapFailed_outside_bunker_underground_02`. `door_bunker_ch1_01_dream` and `onLeaveDoorDialogue_dream_underground` exist only in the dream scene. The dream pad is placed at ~`(-75000,…)` at runtime; `(-6342,…)` is the dream scene's authored position, not the overworld's. Name-only `FindObjectsOfType` / soft GE match will happily hit the wrong world. Always resolve under `DreamSyncManager.GetDreamLocationTransform()` (IsChildOf / distance-to-pad) when `IsDreamActive`. `UniqueObjects` is first-wins — remap pad instances after remote load (`RemapDreamUniqueObjects`). Remote load must set `OutsideLocations.loading` or Cullables register onto World and get hidden behind the door.

## Product snapshot

- **Mod:** YokWare Branch / Path B Horde LAN, host-auth LiteNetLib
- **Product version:** **0.8.x** (current version: README table / `PluginInfo`). **0.7.81** is the last pre-rewrite ship. Older docs/changelogs saying **0.9.x** were too ambitious — treat as historical mislabels.
- **Transport:** LAN LiteNetLib + SteamNetworkingSockets (lobby join); voice/walkie optional (msg 129).
- **Protocol:** see README table / `PluginInfo.ProtocolVersion` (keep both installs same DLL)
- **How the co-op rules fit together** (philosophy, shared vs personal, per-area rules, limits): `DarkwoodMP.Mod/docs/HOW_COOP_WORKS.md`. Update it when a rule in it changes.
- **Game engine:** **Unity 2021.3.30f1** (`b4360d7cdac4`) — verified from Steam
  `Darkwood.exe` / `Darkwood_Data/globalgamemanagers` (both boxes). Not Unity 5.
  → `Object.FindObjectsOfType<T>(includeInactive: true)` is valid; prefer it for
  dialogue NPCs / doors / GameEvents that may be deactivated after first use.
  → Target framework `net471` is correct for this player build.
- **Loader:** BepInEx 5.x (default ship); MelonLoader optional dual-build
- **Build + dual deploy:**
  ```bash
  dotnet build DarkwoodMP.Mod -c Release
  # csproj DeployToGameDirs → Steam + SecondDarkwood plugins when present
  ```
- **GameDir props:** `DarkwoodMP.Mod/GamePath.local.props` → Steam `GameDir` + `SecondPlugins` (gitignored)

## Working rules for this repo

- Free rewrite / online research OK when it unblocks playtest bugs.
- Prefer vanilla parity via decompile over guessing.
- **Never crutch.** No magic tighter ranges, forced 2D, swallow-and-hope, or “paper over the edge” audio/sync hacks. Find the real cause (wrong settings, double path, lifecycle kill, bad gate vs `maxDistance` mismatch) and fix that. Symptom patches ship as bugs.
**Build SDK on this Linux host:** system `dotnet` is runtime-only. Use Unity-bundled SDK:

```bash
export DOTNET_ROOT=/home/warexpor/Unity/Hub/Editor/6000.6.0f1/Editor/Data/DotNetSdk
export PATH="$DOTNET_ROOT:$PATH"
dotnet build DarkwoodMP.Mod -c Release
```

PathB.Tests targets **net8.0** (matches available SDK 8.0.318).

- After light/flare/torch work: check **both** host + client `LogOutput.log`.
- Logging guide: `DarkwoodMP.Mod/docs/LOGGING.md`; config keys: `DarkwoodMP.Mod/docs/CONFIG.md`
- Playtest checklist: `DarkwoodMP.Mod/docs/PLAYTEST.md`
- No `Co-Authored-By: Claude` in commits (user preference).

## Full-game hunt

Bug hunts cover the whole mod for a real game with 3 or more players, including the prologue. The opening movie and the first-play tutorial are part of that, not a later extra.

## Stay in this repo

This session is already at `/home/warexpor/Work/MyProjects/DarkwoodMP-YokWare`. Do not call `move_agent_to_root`. Do not invent another project root. Reviewers and subagents stay in this tree.

## Fix bugs when found

If a bug shows up in review or while implementing, fix it in the same turn. Small ones too. Do not park a known bug for later, and do not leave it only in the changelog as deferred.

## Client must feel like the host

The host runs the world. A client should barely notice they are not the host: enemies, deaths, corpses, and the rest of the simulation should look and behave the way they do for the host. The client shows that result. It does not run a second, thinner copy of the game.

## Changelog discipline (mandatory)

**Always** update root `CHANGELOG.md` in the **same turn** you ship playtest fixes, features, or intentional behavior changes — not “later,” not only in chat.

- Add a new **`## 0.8.x — …`** section at the **top** (newest first), under the Versioning blurb. Historical `0.7.x` entries stay as history. Do **not** revive `0.9.x` labels.
- Cover **what broke / what changed / key files or systems** in plain language (player-facing symptoms + root cause when known).
- Include **parked / deferred** items explicitly so the next session does not rediscover them as “missing changelog.” A bug that was found is not parked — it is fixed first (see above). Park only something the user explicitly chose to leave.
- Protocol bumps, new message IDs, config keys, and join/save UX changes are always changelog-worthy.
- Do **not** leave the only record in session notes, plans, or `COOP_COVERAGE` alone — CHANGELOG is the public ship log.
- If the user asks to deploy/test without committing: still write CHANGELOG before saying done.
- Skip only pure no-op chores (typo-only doc polish with no behavior change, path-only AGENTS edits that already describe themselves).

## Studio rules (shared with SyncRADation)

- **Reverse-check both arrows.** A playtest report ("A did X, B saw Y") is one direction. Before calling a sync fix done, swap initiator/observer (host↔client) and presence (in-room vs late-join) and trace the real send/apply/authority path for each.
- **Shared world.** Items are unique (no per-player copies); anything one player opens is open for all; 3+ players must really work.
- **"Works" = dual-box playtest**, not a green build. Check both `LogOutput.log` files.
- **Git:** commit locally at each deploy; push only playtest-confirmed batches (one push per confirmed set). The GitHub account was flagged before for frequent activity.
- **Subagents:** read-only digging (surveys, decompile/log lookups, report-only audits) on Sonnet; code-writing on Opus. Worktree workers start on a stale base: reset to the working branch `dev` first (`main` holds merged, playtest-confirmed batches).
- No emojis in replies.
- **Playtest loop:** user playtests → agent reads both `LogOutput.log` files (user reports bugs, if any) → fix, build/deploy, md5-check both plugin DLLs, CHANGELOG, local commit → next playtest. The user quits the game right when something goes wrong, so the bug sits at the **end** of the logs: read the tails first. An abrupt log end is the user quitting, not a crash, unless an exception shows. BepInEx overwrites `LogOutput.log` on every launch, so read it before the next run.

## When paths change

Update **this file** (and any personal agent memory that still points at old Windows paths).
