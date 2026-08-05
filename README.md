# YokWare Branch

**Darkwood co-op multiplayer** — Path B: **Horde remaster host-authoritative sync** as the shippable load path, product shell and credit under Warexpor & Yokyy.

| | |
|--|--|
| **Product** | YokWare Branch **0.7.48** (Path B; pre-1.0 — see [CHANGELOG](CHANGELOG.md). Older **0.9.x** labels were too ambitious.) |
| **Sync base** | DWMP Horde Remaster (host-authoritative LAN) |
| **Live wire** | Horde protocol **22** (LiteNetLib `NetMessageType`, IDs through **131**; optional msgs **112–131**) |
| **Research wire** | **Ironbark v2** (`research/DarkwoodMP.Protocol` / dedicated server tree) — not the live LAN peer |
| **Loaders** | **BepInEx** 5.x · **MelonLoader** 0.7 — two first-class build variants of the same mod |
| **License** | **GPLv3** — see [LICENSE](LICENSE) |
| **Co-authors** | Warexpor & Yokyy |

> Path A (Yokyy structure + partial Horde ports) failed brief testing.  
> **Path B is the load path.** Frozen Path A sources: `archive/yokyy-merge-0.9/` — **do not load**.

Deep audit: **[docs/DARKWOOD_MP_AUDIT.md](docs/DARKWOOD_MP_AUDIT.md)** · Join: **[docs/JOIN_HOST_AUDIT.md](docs/JOIN_HOST_AUDIT.md)** · Ironbark: **[docs/IRONBARK_PROTOCOL.md](docs/IRONBARK_PROTOCOL.md)** · Inventory: **[docs/PATH_B_FEATURE_INVENTORY.md](docs/PATH_B_FEATURE_INVENTORY.md)**

---

## Two wires — one ships, one doesn’t

### Ship: Horde protocol 22

What peers actually speak in co-op:

- LiteNetLib UDP + optional SteamNetworkingSockets (lobby join); connection key (`HostPassword` / open LAN)
- `NetMessageType : byte` message IDs (through **131**; optional **112–131**, e.g. voice **129**, `ActivateCursorAction` **130**, `LocationTransport` **131**)
- Host-authoritative simulation; clients mute local AI/time where patched
- `[Forwardable]` attribute for fan-out, handlers in `LanNetworkManager`
- **Same mod build on every peer** (same protocol **22** + feature msgs)

### Redundant: Ironbark v2

Ironbark is a typed-packet protocol sitting in `research/DarkwoodMP.Protocol/` with
`IronbarkRegistry`, `ITransport` abstraction, `u16` message IDs (~156 types),
capability handshake bits, and a dedicated server (`research/DarkwoodMP.Server/`).

It has **no live bridge** to Horde 22. None of it runs in co-op. It’s ~3k lines
of codecs, tests, and server plumbing that do nothing at runtime.

| | Horde 22 (ship) | Ironbark v2 (redundant) |
|--|-----------------|------------------------|
| Message IDs | `byte` (through 131) | `u16` (156 typed packets) |
| Code footprint | ~54k LOC in Mod | ~3k LOC in Protocol + ~2k in Server |
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
   (or take `DarkwoodMP.Mod/bin/Release/BepInEx/DarkwoodMP.Mod.dll` + `LiteNetLib.dll`).
3. Copy into `Darkwood/BepInEx/plugins/`.
4. Launch — banner: **YokWare Branch**, Path B, protocol **22**, version **0.7.48-exp**.

### MelonLoader

1. Install MelonLoader 0.7.x for Darkwood.
2. Once: `pwsh scripts/fetch-melonloader-refs.ps1` (refs under `libs/MelonLoader`, not committed).
3. Build: `dotnet build DarkwoodMP.Mod -c Release -p:Loader=MelonLoader`.
4. Copy `DarkwoodMP.Mod/bin/Release/MelonLoader/DarkwoodMP.Mod.dll` + `LiteNetLib.dll` into `Darkwood/Mods/`.
5. Config lands under Melon UserData (`YokWare/com.yokware.branch.cfg`).

**In-game (both loaders):** title **MULTIPLAYER** · **F2** settings · **F3** manual save · **F4** spectator · **Ctrl+C** chat (off by default).

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
dotnet build DarkwoodMP.Mod -c Release -p:Loader=BepInEx      # → DarkwoodMP.Mod/bin/Release/BepInEx/ ; deploys Steam + SecondDarkwood if present
dotnet build DarkwoodMP.Mod -c Release -p:Loader=MelonLoader  # → DarkwoodMP.Mod/bin/Release/MelonLoader/

dotnet test DarkwoodMP.PathB.Tests -c Release
dotnet test research\DarkwoodMP.Protocol.Tests -c Release   # Ironbark codec (research)
```

**LiteNetLib 1.3.5 comes from NuGet** (`PackageReference`); ship the `LiteNetLib.dll` from the build output alongside the mod DLL — there is no vendored copy under `libs/`.

---

## What Path B is / is not (0.7.x)

**Is:** Horde combat/entity/AI mute, containers (host take-deny), dreams (still hardening), spectator, **world save share**, join pipeline **share → ENTER WORLD → offline load → co-op reconnect**, late-join sticky bulk, host-only time, dialogue tree sync, traps/lights, host grant, dual-box save root isolation, BepInEx + MelonLoader. **Not** near-1.0 — product line is **0.7.x** (earlier **0.9.x** labels were overstated). Detail: **CHANGELOG**.

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
| `research/DarkwoodMP.Protocol/` | Ironbark codec + packets (research / future bridge) |
| `research/DarkwoodMP.Server/` | Dedicated Ironbark relay (**not** a Horde LAN peer) |
| `DarkwoodMP.PathB.Tests/` | Structural + policy Path B gates |
| `archive/yokyy-merge-0.9/` | **Path A freeze** — Yokyy-core merge; reference only |
| `reference/friend-dll-decomp/` | Friend-DLL decompile dump; local-only (gitignored — never build/load) |
| `scripts/` | Release packager (`pack-release.ps1`) + MelonLoader ref fetcher |
| `libs/` | Fetched loader refs (MelonLoader), not committed |
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
| **Why keep it** | Feature reference for deliberate ports (e.g. SyncCheck, Yokyy UI/server patterns). Chat and other pieces already ported into Path B where useful. Ironbark sources also live under `research/DarkwoodMP.Protocol/` (Warexpor). |

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
