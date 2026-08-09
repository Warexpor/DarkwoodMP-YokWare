# YokWare Branch

**Darkwood co-op multiplayer** — Path B: **Horde remaster host-authoritative sync** as the shippable load path, product shell and credit under Warexpor & Yokyy.

| | |
|--|--|
| **Product** | YokWare Branch **0.7.76** (Path B; pre-1.0 — see [CHANGELOG](CHANGELOG.md). Older **0.9.x** labels were too ambitious.) |
| **Sync base** | DWMP Horde Remaster (host-authoritative LAN) |
| **Live wire** | Horde protocol **23** (LiteNetLib `NetMessageType`; optional Steam SNS lobby path) |
| **Loaders** | **BepInEx** 5.x · **MelonLoader** 0.7 — two first-class build variants of the same mod |
| **License** | **GPLv3** — see [LICENSE](LICENSE) |
| **Co-authors** | Warexpor & Yokyy |

> Path A (Yokyy structure + partial Horde ports) failed brief testing.  
> **Path B is the load path.** Legacy Path A / Ironbark trees were removed from the repo (see **CHANGELOG**).

---

## Wire

### Horde protocol 23

What peers speak in co-op:

- LiteNetLib UDP + optional SteamNetworkingSockets (friends lobby); connection key (`HostPassword` / open LAN)
- `NetMessageType : byte` message IDs (through current set; voice **129**, etc.)
- Host-authoritative simulation; clients mute local AI/time where patched
- Same Horde framing on LAN and Steam; backend is exclusive per session
- **Same mod build on every peer** (same protocol **23**)

---

## Install

Pick **one** loader per game process. Both variants are the same Path B mod; build with `-p:Loader=…`.

### BepInEx

1. Install [BepInEx](https://docs.bepinex.dev/) 5.x for Darkwood (match game arch).
2. Build: `dotnet build DarkwoodMP.Mod -c Release -p:Loader=BepInEx`  
   (or take `DarkwoodMP.Mod/bin/Release/BepInEx/DarkwoodMP.Mod.dll` + `LiteNetLib.dll`).
3. Copy into `Darkwood/BepInEx/plugins/`.
4. Launch — banner: **YokWare Branch**, Path B, protocol **23**, version **0.7.76**.

### MelonLoader

1. Install MelonLoader 0.7.x for Darkwood.
2. Point `MelonLoaderDir` / local refs at your MelonLoader `net35` folder (see `DarkwoodMP.Mod` csproj; refs are not vendored in git).
3. Build: `dotnet build DarkwoodMP.Mod -c Release -p:Loader=MelonLoader`.
4. Copy `DarkwoodMP.Mod/bin/Release/MelonLoader/DarkwoodMP.Mod.dll` + `LiteNetLib.dll` into `Darkwood/Mods/`.
5. Config lands under Melon UserData (`YokWare/com.yokware.branch.cfg`).

**In-game (both loaders):** title **MULTIPLAYER** · **F2** settings · **F3** manual save · **F4** spectator · **Ctrl+C** chat (off by default).

All peers need the **same** mod build and the **same loader family** (do not mix BepInEx plugin + Melon Mods DLL on one process). Host enters chapter first; clients JOIN → world share → offline load → co-op reconnect.

**Dual-box (Steam + SecondDarkwood):** SecondDarkwood is GOG — use **LAN** for Steam↔GOG. Steam HOST/JOIN needs two Steam clients. SecondDarkwood auto-isolates saves to `LocalLow/.../Darkwood_Second`.

---

## Build

```text
<!-- DarkwoodMP.Mod/GamePath.local.props (local only, never commit) -->
<Project><PropertyGroup>
  <GameDir>C:\Program Files (x86)\Steam\steamapps\common\Darkwood</GameDir>
</PropertyGroup></Project>
```

```bash
# Whole Visual Studio / dotnet solution (mod + tests + optional F5 spawner)
dotnet build DarkwoodMP.sln -c Release

# Loader variants (same product; different entry + output folder)
dotnet build DarkwoodMP.Mod -c Release -p:Loader=BepInEx      # → bin/Release/BepInEx/ ; dual-deploys if game dirs present
dotnet build DarkwoodMP.Mod -c Release -p:Loader=MelonLoader  # → bin/Release/MelonLoader/

dotnet test DarkwoodMP.PathB.Tests -c Release
```

**`DarkwoodMP.sln`** is the Visual Studio / `dotnet` **solution file**: a thin project list that groups `DarkwoodMP.Mod`, `DarkwoodMP.PathB.Tests`, and `DarkwoodMP.EntitySpawner` so one `dotnet build DarkwoodMP.sln` builds them together. It is not a second codebase.

**LiteNetLib 1.3.5** comes from NuGet (`PackageReference`); ship `LiteNetLib.dll` from the build output alongside the mod DLL.

---

## What Path B is / is not (0.7.x)

**Is:** Horde combat/entity/AI mute, containers (host take-deny), **dreams** (bunker dialogue door / forest-spirit sticky aggro still soak), spectator, **world save share**, join pipeline **share → ENTER WORLD → offline load → co-op reconnect**, late-join sticky bulk (FOOT-split), host-only time, dialogue tree sync (host world-only suppress for peer overlays), traps/lights/scrape audio, **host grant (LAN + Steam)**, dual-box save root isolation, BepInEx + MelonLoader. **Not** near-1.0 — product line is **0.7.x**. Detail: **CHANGELOG**.

**Is not (yet / residual):**

| Topic | Status |
|-------|--------|
| Live campaign polish / full 2-box soak | Ongoing playtest |
| Dream dialogue UI / leave-door audio edge cases | Hardening in **0.7.75–0.7.76**; keep soak |
| Location/landmark *placement* without successful share | Mitigated (client new-gen blocked); full seed lock is L |
| Ironbark live client ↔ Horde LAN bridge | Removed (was research-only) |
| Continuous co-op through **credits** | Network stops at credits (by design); mid-campaign chapter **does** resume |
| SyncCheck digest heal, full InteractionLock matrix, ItemState upgrades | Deferred |

---

## Layout (public tree)

| Path | Role |
|------|------|
| `DarkwoodMP.Mod/` | **Ship** — Horde Path B (BepInEx + MelonLoader variants) |
| `DarkwoodMP.PathB.Tests/` | Product / wire / NetWriter Path B gates |
| `DarkwoodMP.EntitySpawner/` | F5 spawner plugin (BepInEx) |
| `DarkwoodMP.sln` | Solution wrapper for the three projects above |
| `CHANGELOG.md` | Ship log |

Local checkouts may also have gitignored folders (`docs/`, `scripts/`, `libs/`, `AGENTS.md`) for playtest notes and agent memory — not required to build or play.

---

## Credits & license

**Warexpor** and **Yokyy** co-author YokWare Branch. See [CONTRIBUTORS.md](CONTRIBUTORS.md).

- **Warexpor** — Path B Horde remaster load path; public repo; co-op hardening  
- **Yokyy** — original co-op house; structure, reliability hop, chat/HUD lineage  
- Third-party: BepInEx, MelonLoader, Harmony, LiteNetLib, Darkwood (Acid Wizard)  

GPLv3 — see [LICENSE](LICENSE), [COPYRIGHT](COPYRIGHT), [CONTRIBUTORS](CONTRIBUTORS.md).

---

## Changelog

See [CHANGELOG.md](CHANGELOG.md) (current ship **0.7.76**).
