# Ironbark Protocol

**Codename:** Ironbark · **Abbrev:** IBP · **Product:** YokWare Branch  
**Status:** **v2 complete** + **wave G** (burn, explosion secondary FX, reliable EntitySpawn, join locks, Caps honesty)  
**Product version stays `0.9`** — IBP wire is **`Ironbark.Version = 2`**.

---

## One-liner

**Ironbark v2** is the united wire for YokWare: **u16 MessageId** framing, **single registry** (codec + reliability + forward), **no ActionEvent**, versioned handshake with **honest capabilities**, hop reliability on LNL 1.3.5, dedicated server shared Protocol.

---

## Wire (v2)

### Outer UDP framing (unchanged)

```text
[ 0xE0 ][ seq:u32 LE ][ inner… ]   # ReliableEnvelope
[ 0xE1 ][ seq:u32 LE ]             # ReliableAck
```

### Inner game datagram (v2 — breaking)

```text
[ MessageId : u16 LE ][ Payload… ]
```

`MessageId` defaults to `(ushort)PacketType` for existing packet classes (`Packet.MessageId`).

### Handshake

`ConnectRequest` / `ConnectResponse` fields:

- Name, Password, product Version string (`0.9`)
- **`IronbarkVersion`** (must equal **2**)
- **`Capabilities : u32`** bitset (`Ironbark.Caps`)

Mismatch → reject: `IBP mismatch: need 2, peer sent …`

**Caps.Local** = `SpectateFull | ClientBackup`. **`PhysicsState` is not advertised** until free-body emitters ship (lane MessageId 0x90 remains registered/reserved).

---

## Registry

`IronbarkRegistry` maps MessageId → name, reliability, forward, factory.

- Critical resend: `IronbarkRegistry.IsCritical`
- Host fan-out: `IronbarkRegistry.ShouldFanOut`
- Encode: `IronbarkRegistry.Encode(packet)`

`ForwardPolicy` is a thin facade.

---

## ActionEvent

**Removed** in v2. All domains emit typed packets. Residual string receive path deleted.

---

## Enemies

| Path | Role |
|------|------|
| `EntitySpawn` 0x96 | Reliable one-shot dynamic spawn (authority → peers) |
| `EntityState` 0x28 | Continuous motion/anim (~10 Hz unreliable) |
| `EnemyUpdate` 0x22 | **Legacy** — dedicated server relays only; **not** join-time enemy truth |

---

## Burn / explosion (wave G)

| MessageId | Role |
|-----------|------|
| EntityBurning 0x93 | Character fire start/stop; mirrors visual only (DoT on authority) |
| PlayerBurning 0x94 | Local player fire on remote proxy |
| ExplosionSpawnObject 0x95 | Secondary prefabs from `Explodes` (not main explosion VFX) |
| LiquidStopBurn 0x97 | Gas puddle extinguish |

---

## PhysicsState lane

`PacketType.PhysicsStateBatch = 0x90` — packet class registered; **no product emit**. Practical world objects use doors / interactive / movable. Free-body depth = deferred-product.

---

## Transport

`ITransport` abstraction; `NetworkLayer` implements hop reliability. NetManager not required for v2.

---

## Tests

`DarkwoodMP.Protocol.Tests` (net9): codec round-trips (incl. EntitySpawn / burn / explosion), version, registry, capabilities honesty.

```text
dotnet test DarkwoodMP.Protocol.Tests -c Release
dotnet build DarkwoodMP.sln -c Release
```

---

## v1 → v2

| | v1 | v2 |
|--|----|----|
| Version | 1 | **2** |
| Frame | `[u8 type][payload]` | **`[u16 MessageId][payload]`** |
| ActionEvent | residual receive | **gone** |
| Capabilities | no | **yes** (honest bits) |
| Registry | IronbarkMeta only | **IronbarkRegistry** |
| Tests | no | **yes** |

Peers on v1 cannot join v2 hosts (hard reject). All peers must update together.
