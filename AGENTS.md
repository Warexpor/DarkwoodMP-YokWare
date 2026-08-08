# DarkwoodMP-YokWare — agent memory (survives compact)

This file is auto-loaded when the session workspace is this repo (or a child of it).
Keep **machine paths** here so post-compact agents do not re-ask.

## Machine paths (authoritative)

| Role | Path |
|------|------|
| **Project (repo)** | `C:\MyProjects\DarkwoodMP-YokWare` |
| **Host game (Steam)** | `C:\Program Files (x86)\Steam\steamapps\common\Darkwood` |
| **Client game (SecondDarkwood)** | `C:\MyProjects\SecondDarkwood\Darkwood` (**GOG** Galaxy build — not Steam) |
| **Vanilla decompile** | `C:\Users\amicu\Desktop\Dev\Darkwood DECOMPILED` |
| **Decompile C#** | `C:\Users\amicu\Desktop\Dev\Darkwood DECOMPILED\Scripts\Assembly-CSharp` |
| **Host BepInEx / log** | `C:\Program Files (x86)\Steam\steamapps\common\Darkwood\BepInEx\` → `LogOutput.log` |
| **Client BepInEx / log** | `C:\MyProjects\SecondDarkwood\Darkwood\BepInEx\` → `LogOutput.log` |
| **Host plugin deploy** | `...\Darkwood\BepInEx\plugins\DarkwoodMP.Mod.dll` |
| **Client plugin deploy** | `C:\MyProjects\SecondDarkwood\Darkwood\BepInEx\plugins\DarkwoodMP.Mod.dll` |

Dual-box saves: SecondDarkwood auto-isolates to `LocalLow\Acid Wizard Studio\Darkwood_Second` (do not share Steam AppData with host).

**Steamworks co-op:** SecondDarkwood is **GOG** (`Galaxy64.dll` / `goggame-1578751181.*`) — `SteamManager` never inits there. Dual-box Steam host/join is impossible; use **LAN** for Steam+SecondDarkwood, or two real Steam installs/accounts for SNS. **0.7.47:** Steam↔Steam UI timeout 35s, early invite callbacks, host SNS accept without lobby-member race, relay warm retry. **0.7.48:** research/ + archive/ quarantined out of the ship path; host rejects forwarded messages from non-owners and non-lobby Steam SNS connections; FF-off spares host player; LiteNetLib via NuGet.

## Dream bunker “dialogue door” (fact)

Not a normal hinged `Door` → do **not** use `[DoorSync]` / `Door.open` as the success signal.

| Piece | Role |
|-------|------|
| `door_underground` | Dialogue NPC (talk target), not the blocker mesh |
| `door_bunker_ch1_01` / `door_bunker_ch1_01_dream` | Closed visual / collider state |
| `door_bunker_ch1_01_open` | Open visual / passable state |
| `onLeaveDoorDialogue_dream_underground` | GameEvents fired on dialogue close (`onCloseDialogue`) that swaps closed↔open |

Sync path is **GameEventsFired** (host fires leave-door GE → clients apply), plus any setActive/swap children — not DoorOpen fan-out.

**Clone trap:** The same names exist on the **overworld bunker** (`door_underground`, `door_bunker_ch1_01`, leave-door GEs). The dream pad is a **clone/copy** under `dream_bunker_underground_01` at ~`(-75000,…)`; overworld twins sit near `(-6342,…)`. Name-only `FindObjectsOfType` / soft GE match will happily hit the wrong world. Always resolve under `DreamSyncManager.GetDreamLocationTransform()` (IsChildOf / distance-to-pad) when `IsDreamActive`. `UniqueObjects` is first-wins — remap pad instances after remote load (`RemapDreamUniqueObjects`). Remote load must set `OutsideLocations.loading` or Cullables register onto World and get hidden behind the door.

## Product snapshot

- **Mod:** YokWare Branch / Path B Horde LAN, host-auth LiteNetLib
- **Product version:** **0.7.x** (current **0.7.61**). Older docs/changelogs saying **0.9.x** were too ambitious — treat as historical mislabels.
- **Transport:** LAN LiteNetLib + SteamNetworkingSockets (lobby join); voice/walkie optional (msg 129).
- **Protocol:** 23 (keep both installs same DLL)
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
- **GameDir props:** `DarkwoodMP.Mod\GamePath.local.props` → Steam install

## Working rules for this repo

- Free rewrite / online research OK when it unblocks playtest bugs.
- Prefer vanilla parity via decompile over guessing.
- **Never crutch.** No magic tighter ranges, forced 2D, swallow-and-hope, or “paper over the edge” audio/sync hacks. Find the real cause (wrong settings, double path, lifecycle kill, bad gate vs `maxDistance` mismatch) and fix that. Symptom patches ship as bugs.
- **Always dual-deploy after shippable code changes:** `dotnet build DarkwoodMP.Mod -c Release` (DeployToGameDirs is automatic). Do not leave playtest boxes on an old DLL.
- After light/flare/torch work: check **both** host + client `LogOutput.log`.
- Logging guide: `DarkwoodMP.Mod\docs\LOGGING.md`
- Playtest checklist: `docs\PLAYTEST.md`
- No `Co-Authored-By: Claude` in commits (user preference).

## Changelog discipline (mandatory)

**Always** update root `CHANGELOG.md` in the **same turn** you ship playtest fixes, features, or intentional behavior changes — not “later,” not only in chat.

- Add a new **`## 0.7.x — …`** section at the **top** (newest first), under the Versioning blurb. Do **not** revive `0.9.x` labels.
- Cover **what broke / what changed / key files or systems** in plain language (player-facing symptoms + root cause when known).
- Include **parked / deferred** items explicitly so the next session does not rediscover them as “missing changelog.”
- Protocol bumps, new message IDs, config keys, and join/save UX changes are always changelog-worthy.
- Do **not** leave the only record in session notes, plans, or `COOP_COVERAGE` alone — CHANGELOG is the public ship log.
- If the user asks to deploy/test without committing: still write CHANGELOG before saying done.
- Skip only pure no-op chores (typo-only doc polish with no behavior change, path-only AGENTS edits that already describe themselves).

## When paths change

Update **this file** and the short block in `C:\Users\amicu\.grok\Agents.md` (global fallback if workspace is home).
