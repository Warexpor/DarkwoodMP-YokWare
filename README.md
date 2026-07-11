# YokWare Branch

**Darkwood co-op multiplayer** — Path B: **Horde remaster host-authoritative sync** as the shippable load path, product shell and credit under Warexpor & Yokyy.

| | |
|--|--|
| **Product** | YokWare Branch **0.9.2** (Path B; unreleased **0.9.2+** playtest/audit work in [CHANGELOG](CHANGELOG.md)) |
| **Sync base** | DWMP Horde Remaster (host-authoritative LAN) |
| **Live wire** | Horde protocol **19** (LiteNetLib `NetMessageType`, IDs through **126**, optional **112–126**) |
| **Research wire** | **Ironbark v2** (`DarkwoodMP.Protocol` / dedicated server tree) — not the live LAN peer |
| **Loaders** | **BepInEx** 5.x · **MelonLoader** 0.7 — two first-class build variants of the same mod |
| **License** | **GPLv3** — see [LICENSE](LICENSE) |
| **Co-authors** | Warexpor & Yokyy |

> Path A (Yokyy structure + partial Horde ports) failed brief testing.  
> **Path B is the load path.** Frozen Path A sources: `archive/yokyy-merge-0.9/` — **do not load**.

Deep audit: **[docs/DARKWOOD_MP_AUDIT.md](docs/DARKWOOD_MP_AUDIT.md)** · Join: **[docs/JOIN_HOST_AUDIT.md](docs/JOIN_HOST_AUDIT.md)** · Ironbark: **[docs/IRONBARK_PROTOCOL.md](docs/IRONBARK_PROTOCOL.md)** · Inventory: **[docs/PATH_B_FEATURE_INVENTORY.md](docs/PATH_B_FEATURE_INVENTORY.md)**

---

## Two wires — one ships, one doesn’t

### Ship: Horde protocol 19

What peers actually speak in co-op:

- LiteNetLib UDP, connection key (`HostPassword` / open LAN)
- `NetMessageType : byte` message IDs (through **126**; reserved holes; optional trailers **112–126**)
- Host-authoritative simulation; clients mute local AI/time where patched
- `[Forwardable]` attribute for fan-out, handlers in `LanNetworkManager`
- **Same mod build on every peer** (same protocol 19 + feature msgs)

### Redundant: Ironbark v2

Ironbark is a typed-packet protocol sitting in `DarkwoodMP.Protocol/` with
`IronbarkRegistry`, `ITransport` abstraction, `u16` message IDs (~156 types),
capability handshake bits, and a dedicated server (`DarkwoodMP.Server/`).

It has **no live bridge** to Horde 19. None of it runs in co-op. It’s ~3k lines
of codecs, tests, and server plumbing that do nothing at runtime.

| | Horde 19 (ship) | Ironbark v2 (redundant) |
|--|-----------------|------------------------|
| Message IDs | `byte` (through 126) | `u16` (156 typed packets) |
| Code footprint | ~40k LOC in Mod | ~3k LOC in Protocol + ~2k in Server |
| Transport | LiteNetLib direct | `ITransport` abstraction |
| Routing | `[Forwardable]` attributes | `IronbarkRegistry` entries |
| Capability negotiation | Protocol version only | Capability bits at handshake |
| Live co-op play | Yes | No bridge exists |
| Status | Ship | Green tests, zero gameplay use |

Still in-tree because the dedicated server tree shares the Protocol project,
and removing it is more churn than it’s worth. Don’t mistake it for the live wire.

Details: **[docs/IRONBARK_PROTOCOL.md](docs/IRONBARK_PROTOCOL.md)** · message table: **[docs/IRONBARK_MESSAGES.md](docs/IRONBARK_MESSAGES.md)**

---

## Install

Pick **one** loader per game process. Both variants are the same Path B mod; build with `-p:Loader=…`.

### BepInEx

1. Install [BepInEx](https://docs.bepinex.dev/) 5.x for Darkwood (match game arch).
2. Build: `dotnet build DarkwoodMP.Mod -c Release -p:Loader=BepInEx`  
   (or take `bin/Release/BepInEx/DarkwoodMP.Mod.dll` + `LiteNetLib.dll`).
3. Copy into `Darkwood/BepInEx/plugins/`.
4. Launch — banner: **YokWare Branch**, Path B, protocol **19**, version **0.9.2**.

### MelonLoader

1. Install MelonLoader 0.7.x for Darkwood.
2. Once: `pwsh scripts/fetch-melonloader-refs.ps1` (refs under `libs/MelonLoader`, not committed).
3. Build: `dotnet build DarkwoodMP.Mod -c Release -p:Loader=MelonLoader`.
4. Copy `bin/Release/MelonLoader/DarkwoodMP.Mod.dll` + `LiteNetLib.dll` into `Darkwood/Mods/`.
5. Config lands under Melon UserData (`YokWare/com.yokware.branch.cfg`).

**In-game (both loaders):** title **MULTIPLAYER** · **F2** settings · **F3** manual save · **F4** spectator · **Ctrl+C** chat.

All peers need the **same** mod build and the **same loader family** (do not mix BepInEx plugin + Melon Mods DLL on one process). Host enters chapter first; clients JOIN → world share → offline load → co-op reconnect.

**Dual-box (Steam + SecondDarkwood):** SecondDarkwood auto-isolates saves to `LocalLow/.../Darkwood_Second`. Optional `Saves.SaveRootOverride` in config.

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

# Loader variants (same product; different entry + output folder)
dotnet build DarkwoodMP.Mod -c Release -p:Loader=BepInEx      # → bin/Release/BepInEx/ ; deploys Steam + SecondDarkwood if present
dotnet build DarkwoodMP.Mod -c Release -p:Loader=MelonLoader  # → bin/Release/MelonLoader/

dotnet test DarkwoodMP.PathB.Tests -c Release
dotnet test DarkwoodMP.Protocol.Tests -c Release   # Ironbark codec
```

`libs/LiteNetLib.dll` is required for both loader builds.

---

## What Path B is / is not (0.9.2)

**Is:** Horde combat/entity/AI mute (attack range + FF residual 0.9.2+), containers (host take-deny H6), dreams, spectator (night-dead proxy stay dead), **world save share** (dual-box; client slot-5 merge keeps full profile index), join pipeline **share → ENTER WORLD → offline load → co-op reconnect**, late-join sticky bulk, host-only time, dialogue tree sync, silent trap harvest (no peer boom/vanish), traps/lights occupancy+remain, host grant (PeerRoster/HostHandoff), dual-box save root isolation, BepInEx + MelonLoader. Detail: **CHANGELOG 0.9.2+**.

**Is not (yet / residual):**

| Topic | Status |
|-------|--------|
| Live campaign polish / full 2-box soak | Ongoing playtest |
| Location/landmark *placement* without successful share | Mitigated (client new-gen blocked); full seed lock is L |
| Ironbark live client ↔ Horde LAN bridge | Deferred |
| Host migration after host drop | Unsupported |
| Continuous co-op through **credits** | Network stops at credits (by design); mid-campaign chapter **does** resume |
| SyncCheck digest heal, full InteractionLock matrix, ItemState upgrades | Deferred |

---

## Layout

| Path | Role |
|------|------|
| `DarkwoodMP.Mod/` | **Ship** — Horde Path B (BepInEx + MelonLoader variants) |
| `DarkwoodMP.EntitySpawner/` | F5 spawner plugin (BepInEx) |
| `DarkwoodMP.Protocol/` | Ironbark codec + packets (tests / future bridge) |
| `DarkwoodMP.Server/` | Dedicated Ironbark relay (**not** a Horde LAN peer) |
| `DarkwoodMP.PathB.Tests/` | Structural + policy Path B gates |
| `archive/yokyy-merge-0.9/` | **Path A freeze** — Yokyy-core merge; reference only |
| `docs/` | Audit, join, Ironbark protocol, inventory |

---

## Path A (archived source)

**Yes — Path A is still in this repo.** It is frozen, not deleted.

| | |
|--|--|
| **Location** | [`archive/yokyy-merge-0.9/`](archive/yokyy-merge-0.9/) |
| **What it is** | Pre–Path B product: **Yokyy structure** + partial Horde ports + **Ironbark** wire (mod, Protocol, Server, tests) |
| **Why archived** | Brief testing showed Yokyy-style bugs and worse sound/sync vs pure Horde remaster — so Path B became the shippable load path |
| **Load path?** | **No.** Do **not** build/install this tree for play. Archive README: [do not load](archive/yokyy-merge-0.9/README.md) |
| **Ship path** | Repo root [`DarkwoodMP.Mod/`](DarkwoodMP.Mod/) (Path B Horde base) |
| **Why keep it** | Feature reference for deliberate ports (e.g. SyncCheck, Yokyy UI/server patterns). Chat and other pieces already ported into Path B where useful. Ironbark sources also live under root `DarkwoodMP.Protocol/` (Warexpor). |

Solution/CI default targets **Path B only**. Opening projects under `archive/yokyy-merge-0.9/` is for archaeology, not shipping.

---

## Credits & license

**Warexpor** and **Yokyy** co-author YokWare Branch. See [CONTRIBUTORS.md](CONTRIBUTORS.md).

- **Warexpor** — Path B Horde remaster load path; public repo; **Ironbark** protocol; co-op hardening  
- **Yokyy** — original co-op house; structure, reliability hop, dedicated server path, SyncCheck, chat/HUD lineage  
- Third-party: BepInEx, MelonLoader, Harmony, LiteNetLib, Darkwood (Acid Wizard)  

GPLv3 — see [LICENSE](LICENSE), [COPYRIGHT](COPYRIGHT), [CONTRIBUTORS](CONTRIBUTORS.md).

---

## Changelog

See [CHANGELOG.md](CHANGELOG.md).
