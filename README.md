# YokWare Branch

**Darkwood co-op multiplayer** — Path B: **Horde remaster host-authoritative sync** as the shippable base, product shell and credit under Warexpor & Yokyy.

| | |
|--|--|
| **Product** | YokWare Branch **0.9.2** (Path B — audit fixes; 1.0-class, not labeled 1.0) |
| **Sync base** | DWMP Horde Remaster (host-authoritative LAN) |
| **Wire** | Horde protocol **19** (LiteNetLib) |
| **Loader** | **BepInEx** 5.x (Unity Mono) |
| **License** | **GPLv3** — see [LICENSE](LICENSE) |
| **Authors** | Warexpor & Yokyy |

> Path A (Yokyy structure + partial Horde ports) failed brief testing (Yokyy bugs, wrecked sound, thin combat).  
> **Path B replaces the load path with Horde.** Prior merge is under `archive/yokyy-merge-0.9/` — do not load it.

Deep audit: **[docs/DARKWOOD_MP_AUDIT.md](docs/DARKWOOD_MP_AUDIT.md)** · Feature inventory: **[docs/PATH_B_FEATURE_INVENTORY.md](docs/PATH_B_FEATURE_INVENTORY.md)**

---

## Install (BepInEx)

1. Install [BepInEx](https://docs.bepinex.dev/) 5.x for Darkwood (match game arch).
2. Build or take `DarkwoodMP.Mod.dll` (+ `LiteNetLib.dll` if not already present).
3. Copy into `Darkwood/BepInEx/plugins/`.
4. Launch — log banner shows **YokWare Branch**, **Path B**, protocol **19**, version **0.9.2**.
5. **F2** multiplayer menu · **F3** manual save UI · **F4** spectator · **Ctrl+C** chat.

All peers need the **same** mod build. Prefer **new games** with host world share (slot 5 on clients).

---

## Build

```text
<!-- DarkwoodMP.Mod/GamePath.local.props (local only, never commit) -->
<Project><PropertyGroup>
  <GameDir>C:\Program Files (x86)\Steam\steamapps\common\Darkwood</GameDir>
</PropertyGroup></Project>
```

```bash
dotnet build DarkwoodMP.sln -c Release
# → DarkwoodMP.Mod/bin/Release/DarkwoodMP.Mod.dll
# Auto-deploys to Steam + SecondDarkwood plugins if present

dotnet test DarkwoodMP.PathB.Tests -c Release
dotnet test DarkwoodMP.Protocol.Tests -c Release   # Ironbark codec (research tree)
```

`libs/LiteNetLib.dll` is required for the mod build (shipped next to the plugin on deploy).

---

## What Path B is / is not

**Is (0.9.2):** Horde combat redirect, entity broadcast, client AI mute, containers, dreams, spectator, world save share, **host-only time**, **world-only remote dialog outcomes**, **dialogue tree consumed-node sync (Yokyy DialogueSync port)**, **client→host flags**, **NPC talk lock**, **partial night-death world-mutation suppress**, **chapter auto rehost/reconnect**.

**Is not (yet / residual):**

| Topic | Status |
|-------|--------|
| Live 2-box campaign playtest | Open residual |
| Location/landmark *placement* determinism | Known limit (TODO #5) — identity is host world-share, not dual gen |
| Ironbark live client wire / dedicated Horde bridge | Deferred (`DarkwoodMP.Server` ≠ LAN peers) |
| Host migration after host drop | Unsupported |
| Continuous co-op through **credits** | Network stops at credits (by design residual); chapter mid-campaign **does** auto-resume |
| SyncCheck digest heal, full InteractionLock, ItemState upgrades | Deferred |
| MelonLoader dual pack | Out of scope for this release |

---

## Layout

| Path | Role |
|------|------|
| `DarkwoodMP.Mod/` | **Ship** — Horde-based BepInEx plugin |
| `DarkwoodMP.Protocol/` | Ironbark codec (tests / future bridge) |
| `DarkwoodMP.Server/` | Dedicated Ironbark relay (**not** live Horde peer) |
| `DarkwoodMP.PathB.Tests/` | Structural + policy Path B gates |
| `archive/yokyy-merge-0.9/` | Frozen failed merge — reference only |
| `docs/` | Audit, inventory, protocol notes |

---

## Credits & license

- **Warexpor** — product owner / Horde remaster sync design  
- **Yokyy** — original house, protocol ideas, product collaboration  
- Third-party: BepInEx, Harmony, LiteNetLib, Darkwood (Acid Wizard)  

GPLv3 — see [LICENSE](LICENSE), [COPYRIGHT](COPYRIGHT), [CONTRIBUTORS](CONTRIBUTORS.md).

---

## Changelog

See [CHANGELOG.md](CHANGELOG.md).
