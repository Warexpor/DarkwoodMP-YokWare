# DarkwoodMP-YokWare — agent memory (survives compact)

This file is auto-loaded when the session workspace is this repo (or a child of it).
Keep **machine paths** here so post-compact agents do not re-ask.

## Machine paths (authoritative)

| Role | Path |
|------|------|
| **Project (repo)** | `C:\MyProjects\DarkwoodMP-YokWare` |
| **Host game (Steam)** | `C:\Program Files (x86)\Steam\steamapps\common\Darkwood` |
| **Client game (SecondDarkwood)** | `C:\MyProjects\SecondDarkwood\Darkwood` |
| **Vanilla decompile** | `C:\Users\amicu\Desktop\Darkwood DECOMPILED` |
| **Decompile C#** | `C:\Users\amicu\Desktop\Darkwood DECOMPILED\Scripts\Assembly-CSharp` |
| **Host BepInEx / log** | `C:\Program Files (x86)\Steam\steamapps\common\Darkwood\BepInEx\` → `LogOutput.log` |
| **Client BepInEx / log** | `C:\MyProjects\SecondDarkwood\Darkwood\BepInEx\` → `LogOutput.log` |
| **Host plugin deploy** | `...\Darkwood\BepInEx\plugins\DarkwoodMP.Mod.dll` |
| **Client plugin deploy** | `C:\MyProjects\SecondDarkwood\Darkwood\BepInEx\plugins\DarkwoodMP.Mod.dll` |

Dual-box saves: SecondDarkwood auto-isolates to `LocalLow\Acid Wizard Studio\Darkwood_Second` (do not share Steam AppData with host).

## Product snapshot

- **Mod:** YokWare Branch / Path B Horde LAN, host-auth LiteNetLib
- **Protocol:** 19 (keep both installs same DLL)
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
- After light/flare/torch work: check **both** host + client `LogOutput.log`.
- Logging guide: `DarkwoodMP.Mod\docs\LOGGING.md`
- Playtest checklist: `docs\PLAYTEST.md`
- No `Co-Authored-By: Claude` in commits (user preference).

## Changelog discipline (mandatory)

**Always** update root `CHANGELOG.md` in the **same turn** you ship playtest fixes, features, or intentional behavior changes — not “later,” not only in chat.

- Add a new **`## 0.9.2+ — …`** section at the **top** (newest first), under the intro blurb.
- Cover **what broke / what changed / key files or systems** in plain language (player-facing symptoms + root cause when known).
- Include **parked / deferred** items explicitly so the next session does not rediscover them as “missing changelog.”
- Protocol bumps, new message IDs, config keys, and join/save UX changes are always changelog-worthy.
- Do **not** leave the only record in session notes, plans, or `COOP_COVERAGE` alone — CHANGELOG is the public ship log.
- If the user asks to deploy/test without committing: still write CHANGELOG before saying done.
- Skip only pure no-op chores (typo-only doc polish with no behavior change, path-only AGENTS edits that already describe themselves).

## When paths change

Update **this file** and the short block in `C:\Users\amicu\.grok\Agents.md` (global fallback if workspace is home).
