# YokWare Branch

**Darkwood co-op multiplayer** — Path B: **Horde remaster host-authoritative sync** as the shippable load path, product shell and credit under Warexpor & Yokyy.

| | |
|--|--|
| **Product** | YokWare Branch **0.9.2** (Path B — audit + join/playtest fixes; 1.0-class, not labeled 1.0) |
| **Sync base** | DWMP Horde Remaster (host-authoritative LAN) |
| **Live wire** | Horde protocol **19** (LiteNetLib `NetMessageType`) |
| **Research wire** | **Ironbark v2** (`DarkwoodMP.Protocol` / dedicated server tree) — not the live LAN peer |
| **Loaders** | **BepInEx** 5.x · **MelonLoader** 0.7 — two first-class build variants of the same mod |
| **License** | **GPLv3** — see [LICENSE](LICENSE) |
| **Co-authors** | Warexpor & Yokyy |

> Path A (Yokyy structure + partial Horde ports) failed brief testing.  
> **Path B is the load path.** Frozen Path A sources: `archive/yokyy-merge-0.9/` — **do not load**.

Deep audit: **[docs/DARKWOOD_MP_AUDIT.md](docs/DARKWOOD_MP_AUDIT.md)** · Join: **[docs/JOIN_HOST_AUDIT.md](docs/JOIN_HOST_AUDIT.md)** · Ironbark: **[docs/IRONBARK_PROTOCOL.md](docs/IRONBARK_PROTOCOL.md)** · Inventory: **[docs/PATH_B_FEATURE_INVENTORY.md](docs/PATH_B_FEATURE_INVENTORY.md)**

---

## Two wires (do not mix)

### Live: Horde protocol 19

What peers speak **today** in co-op:

- LiteNetLib UDP, connection key (`HostPassword` / open LAN)
- `NetMessageType` byte ids (handshake, PlayerState, containers, world share, chat, …)
- Host-authoritative simulation; clients mute local AI/time where patched
- **Same mod build on every peer** (same protocol 19 + feature msgs)

### Research: Ironbark (IBP) v2

**Ironbark** is **Warexpor’s** typed packet protocol (IBP), still in-tree for codecs, tests, and a **dedicated server** path — **not** what Path B LAN clients use to talk to each other.

| | Ironbark v2 | Horde 19 (live) |
|--|-------------|-----------------|
| Framing | Outer reliable envelope `0xE0`/`0xE1` + inner **`MessageId : u16`** + payload | LiteNetLib + **`NetMessageType : byte`** body |
| Registry | `IronbarkRegistry` (codec, reliability, fan-out) | Horde handlers + `[Forwardable]` |
| Handshake | `ConnectRequest`/`Response` + **`IronbarkVersion == 2`** + **capabilities u32** | Horde Handshake + product protocol 19 |
| Home | `DarkwoodMP.Protocol/`, `DarkwoodMP.Server/` | `DarkwoodMP.Mod/` LAN |
| Status | Green unit tests; **no live client↔client bridge to Horde 19** | Ship |

Details: **[docs/IRONBARK_PROTOCOL.md](docs/IRONBARK_PROTOCOL.md)** · message table: **[docs/IRONBARK_MESSAGES.md](docs/IRONBARK_MESSAGES.md)**.

Connecting Ironbark and Horde without a deliberate re-protocol is **unsafe** — keep them separate until a bridge is designed.

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

**Is:** Horde combat/entity/AI mute, containers (host take-deny H6), dreams, spectator, **world save share** (dual-box safe), join pipeline **share → offline load → co-op reconnect**, host-only time, world-only remote dialog outcomes, dialogue tree sync, client→host flags, NPC talk lock, night-death mutation suppress, chapter auto rehost/reconnect, chat, drag/push scrape motion-gated, dual-box save root isolation, BepInEx + MelonLoader dual entry.

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
