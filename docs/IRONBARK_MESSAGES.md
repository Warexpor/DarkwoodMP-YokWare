# Ironbark Message Table

**Protocol:** Ironbark **v2** · **Product:** YokWare `0.9`  
See `IRONBARK_PROTOCOL.md` for doctrine.

## v2 frame

Inner: **`MessageId : u16 LE`** + payload. Outer reliability still `0xE0`/`0xE1`.

`MessageId` currently equals historical `PacketType` numeric values (widened to u16).

## Handshake

| Field | Wire |
|-------|------|
| ConnectRequest | name, password, productVersion string, **IronbarkVersion:int**, **Capabilities:u32** |
| ConnectResponse | clientId, accepted, message, **IronbarkVersion:int**, **Capabilities:u32** |

Strict equality: peer must send **`IronbarkVersion == 2`** (reject: `IBP mismatch: need 2, peer sent …`).

## MessageId map (v2)

| Id | Name | Rel | Forward |
|----|------|-----|---------|
| 0x01–0x06, 0xC0 | Session / list | mix | mix |
| 0x10–0x28 | Continuous / entity | mix | mix |
| 0x30–0x33 | World transfer | Crit | None |
| 0x40–0x8D | Typed campaign domains (A–F) | mix | mix |
| **0x90** | PhysicsStateBatch | Unrel | Direct — **reserved, not emitted** |
| **0x93** | **EntityBurning** | Rel | Direct |
| **0x94** | **PlayerBurning** | Rel | Direct |
| **0x95** | **ExplosionSpawnObject** | Rel | Direct |
| **0x96** | **EntitySpawn** | Rel | Direct |
| **0x97** | **LiquidStopBurn** | Rel | Direct |
| (outer) 0xE0/E1 | Reliable envelope | — | — |

**ActionEvent 0x13 — deleted in v2.**

## Reserved / decode-only (do not treat as live product emit)

| Id | Name | Notes |
|----|------|-------|
| 0x22 | EnemyUpdate | **Legacy.** Mod uses `EntityState` + `EntitySpawn`. Server may relay only; not join enemy truth. |
| 0x91 | EnvironmentEffect | Registered; **no mod emit**. |
| 0x90 | PhysicsStateBatch | Lane registered; **no product emit**; `Caps.Local` omits PhysicsState. |
| 0x21 | EventTrigger | Handler retained; story uses `GameEventFire` + local proxy fire. |

## Gap-closure payloads (wave G)

### EntityBurning (0x93)
`PlayerId:int` · `EntityId:short` · `IsBurning:bool` · `BurnTime:float` · `Modifier:float` · `Interval:float`

### PlayerBurning (0x94)
`PlayerId:int` · `IsBurning:bool` · `BurnTime:float`

### ExplosionSpawnObject (0x95)
`PlayerId` · `PrefabName` · `X,Y,Z` · `Rx,Ry,Rz` (euler)

### EntitySpawn (0x96)
`PlayerId` · `EntityId:short` · `EntityType` · `PrefabPath` · `X,Y,Z` · `RotY`

### LiquidStopBurn (0x97)
`PlayerId` · `X,Y,Z`

## Campaign domains (summary)

| Band | Contents |
|------|----------|
| 0x40–0x4A | Trade, backup, savebeat, location, map, rep, hideout, journal |
| 0x4B–0x57 | Dream*, cutscene, chapter, scene, examine |
| 0x58–0x61 | Container, death drop, barricade, build, vault, harvest, ilock |
| 0x62–0x8D | Flags, combat, death, FX, stations, weather, scenario, seeds, synccheck |

**Muzzle / bullet FX:** `FiredWeapon` (0x6C) + `BulletFx` (0x74) — implemented.

## Status

**Ironbark v2 complete** + **wave G gap closure**. Product `0.9`. Wire version **2**.
