# YokWare Branch merge matrix

**Product:** YokWare Branch **0.9.2** Path B  
**Live LAN wire:** **Horde protocol 19** (`PluginInfo.ProtocolVersion`) — LiteNetLib  
**Ironbark v2:** research / dedicated `DarkwoodMP.Server` only — **not** the BepInEx LAN path  
**Path:** `C:\MyProjects\DarkwoodMP-YokWare`  
**Doctrine:** Path B = Horde host-authoritative sync as ship base; Yokyy product shell.

> **Honesty (0.9.2):** Rows below that mention Ironbark ClockSync / WorldGenSeed / InteractionLock
> snapshot describe archive/Yokyy or aspirational targets. Live Path B truth for time/dialog/chapter/world:
> host TimeSync + client clock suppress; DialogHostApplyGuard; ChapterSessionResume; WorldSaveShare
> (no per-chunk seed in tree). See `CHANGELOG.md` 0.9.2 and `DARKWOOD_MP_AUDIT.md`.

Status key: **done** | **partial** | **yokyy-keep** | **deferred-product**

---

## Layer 0 — Infrastructure

| ID | Domain | Status |
|----|--------|--------|
| 0.0 | YokWare Branch workspace | **done** |
| 0.1 | Session / handshake / IDs | **done** (Horde 19) |
| 0.2 | Save / world share | **partial** — Path B WorldSaveShare fail-loud; not Yokyy WorldTransfer |
| 0.3 | Time / day / night + isAfterNight | **done** (0.9.2 host-only clock; TimeSync + refreshTimeNoLogic) |
| 0.4 | Flags bulk + delta | **done** (0.9.2 bidirectional client→host + host fan-out) |
| 0.5 | Apply guards / reset | **done** |
| 0.6 | Pause / freeze | **done** (+ skill menus) |
| 0.7 | Broadcast / ToPlayer / AllExcept | **done** |
| 0.8 | Fan-out policy | **done** |
| 0.9 | Join bulk registry | **done** (+ InteractionLock snapshot) |

## Layer 1 — Continuous world

| ID | Domain | Status |
|----|--------|--------|
| 1.1 | Player pose / anim / vault | **done** (+ Vault_Patch) |
| 1.2 | Entity AI + multi-center + night redirect | **done** (+ reliable `EntitySpawn` 0x96) |
| 1.3 | Doors / gens / traps pending | **done** |
| 1.4 | Drag / movables | **done** (InteractionLock + MovableSync) |
| 1.5 | Locations enter/exit | **done** (LocationSync) |
| 1.6 | Weather | **done** (pre-existing + join) |
| 1.7 | Map markers / discovery | **done** (MapSync_Patch — shared discovery) |
| 1.8 | Lights | **done** (HeldLightSync + Interactive) |

## Layer 2 — Economy

| ID | Domain | Status |
|----|--------|--------|
| 2.1–2.4 | Containers / drops / death bags / journal | **done** |
| 2.5 | Trade | **done** (absolute stock model C; join bulk) |
| 2.6–2.7 | Workbench / saw / hideout | **done** |
| 2.8 | ItemState | **yokyy-keep** |
| — | Compressor / oxygen | **done** |
| — | Reputation model C | **done** |

## Layer 3 — Combat / night

| ID | Domain | Status |
|----|--------|--------|
| 3.1–3.4 | Combat / FF / ranged / barricades | **done** (+ WorldMelee; muzzle `FiredWeapon` + `BulletFx`) |
| 3.5 | Shadows | **done** |
| 3.6 | Night death + spectate | **done** (NightSpectator + full SpectatorModeController) |
| 3.7 | Scenarios + custom events | **done** |
| — | Entity / player burn | **done** (`EntityBurning` / `PlayerBurning`; mirror DoT skip) |
| — | Explosion secondary objects | **done** (`ExplosionSpawnObject`) |
| — | Morning rep skip | **done** |

## Layer 4 — Story

| ID | Domain | Status |
|----|--------|--------|
| 4.1–4.2 | Dialogs / GameEvents / EventTriggers | **done** |
| 4.3 | Dreams + doors + audio | **done** |
| 4.4 | Final dream / epilogue / credits | **done** |
| 4.5 | Chapter generateChapter | **done** |
| 4.6 | Infection / examine / hide | **done** |
| 4.7 | Locks / interactives | **done** |
| — | Cutscenes | **done** |

## Layer 5–6 — Audio / balance

| ID | Domain | Status |
|----|--------|--------|
| 5.1 | Player / entity / dream audio | **done** |
| 5.2 | Spectator | **done** (SpectatorModeController + WorldGrid culling + F4) |
| 6.1 | CoopBalance loot + named NPC | **done** |
| — | Gas join bulk + liquid stop-burn | **done** |
| — | World harvest destroy | **done** |
| — | Player effect flags | **done** |

## Yokyy-only product (kept by design)

Dedicated server, reliability hop, SyncCheck, chat/HUD, InteractionLock, WorldTransfer, ItemState, Protocol Compile Include, IsTimeAuthority.

## Formerly deferred product (shipped in 0.9)

| Item | Status |
|------|--------|
| Full SpectatorModeController + WorldGrid culling | **done** |
| ClientStateBackup JSON per player | **done** |
| Trade absolute model C rewrite | **done** |
| Transport reliability completion (LNL 1.3.5) | **done** |
| Entity burn + explosion secondary FX | **done** (gap-closure wave G) |
| Reliable dynamic enemy spawn | **done** (`EntitySpawn` + EntityState motion) |
| InteractionLock join bulk | **done** |
| LiteNetLib NetManager transport swap | **deferred** — hop on Utils unless metrics force it |
| Physics free-body full PhysicsState | **deferred** — door/interactive/movable cover practical cases; lane registered but **Caps.Local omits PhysicsState** |

## Wire honesty

- **ActionEvent extinct** — typed MessageIds only.
- **Legacy `EnemyUpdate` (0x22):** dedicated server relays only; join snapshot does **not** invent enemies. Live truth: `EntityState` + `EntitySpawn`.
- **Reserved / unused emit:** `EnvironmentEffect` (0x91), `PhysicsStateBatch` (0x90, no product emit). `EventTrigger` handler retained; story uses `GameEventFire` + proxy.
- Handshake: `IronbarkVersion=2` + capabilities (`SpectateFull | ClientBackup`).

Playtest: `docs/PLAYTEST.md` · Ironbark: `docs/IRONBARK_PROTOCOL.md`
