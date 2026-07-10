# Plan: In-world push audio + mushroom secondary FX

**Date:** 2026-07-10  
**Scope:** Body-push scrape double / 2s stop lag; mushroom stomp missing remote white secondary (`spawnObject`).  
**Out of scope:** WorldSaveShare dual-box; drag claim polish; new protocol version unless required.

## Root causes (from code + decompile)

### A. Mushroom secondary white FX missing on remote

Vanilla `Explodes.onActivate()`:

1. `spawnObjects()` → `Core.AddPrefab(spawnObject, …)` × `objectAmount` (white secondary / “cum”)
2. `Core.AddPrefab(explosionPrefab, …)` (main boom VFX)
3. `explode()` (damage + sound)

Remote path today:

- `ExplosionTrigger` → client `SpawnExplosionVisual` only spawns **`explosionPrefab`**, never `spawnObjects()`.
- Secondary was supposed to come from host `ExplosionSpawnObject` (AddPrefab postfix during `onActivate`).
- Playtest: stomper sees secondary (local `onActivate`); remote does not → **remote visual path incomplete**. Relying only on `ExplosionSpawnObject` is fragile (prefab name resolve, host-only send, timing).

**Fix:** `SpawnExplosionVisual` must call private `spawnObjects()` when local `Explodes` exists and `spawnObject != null`, then main prefab, mark `activated`, honor `destroyOnExplode` without full `explode()` damage. Keep `ExplosionSpawnObject` as fallback when no local component. Dedupe: if already `activated`, skip re-spawn.

### B. Push scrape doubled on pusher

Likely layering of **native `ItemSounds.movingSoundAO`** (local RB velocity > 11 / angVel > 0.25) **and** network scrape (**MOS** / `PlayerAudio` loop) on the same peer.

Paths that can double:

- Host applies client `PhysicsState` → `NotifyBodyPushStarted` → `MOS.NoteMoving` + `PlayerAudio` broadcast (exclude pusher — verify exclude always works).
- Client applies host `PhysicsState` → `MOS.NoteMoving` while local RB still non-kinematic and `ItemSounds.Update` also arms.
- Residual: both `AudioController` AO and MOS `AudioSource` on same GO.

**Fix:** Single owner per object per peer:

1. **Local authority scrape** (local player is pushing / free non-network kinematic body): **native `ItemSounds` only** — never start MOS / ignore body-push `PlayerAudio` for that object name while local-owned.
2. **Remote scrape** (interp / client-kinematic / drag remote): **MOS only** — suppress native `ItemSounds` moving branch (Harmony prefix or sleep/suppress flag).
3. Ensure `BroadcastBodyPushSound` always excludes the pushing peer; never `Broadcast` when receive id is known.

### C. Push stop delay ≥ 2s

Stop is gated on **network position deltas** at 10 Hz + `BodyPushSoundHold` (0.1s) + residual slide (`posDelta ≥ 0.02` keeps extending). Native `ItemSounds` also keeps loop while `vel > 11` / `angVel > 0.25` after the player walks away.

**Fix overhaul:**

1. **Local intent stop:** when local player no longer contacts the object (no collision / push) or player horizontal speed ~0 while object was local-pushed, `ForceStop` **this frame** (0.5s vanilla fade only).
2. **Remote MOS stop:** first quiet net tick (`posDelta < 0.02`) → stop immediately (drop multi-hold / long kinematic gate coupling). `ForceStop` + reliable stop signal.
3. Cap residual: if object still micro-slides after local release, **sleep RB** on ForceStop (already does) so native cannot re-arm; keep `PostStopSuppressSec` but shorten if it blocks legitimate re-push feel (keep ~0.35–0.45s).
4. Remove dead legacy push-sound dictionaries if unused after MOS unification (optional cleanup, same PR if small).

## Tasks

| ID | Task | Files (primary) | Done when |
|----|------|-----------------|-----------|
| T1 | Mushroom / Explodes secondary on remote | `WorldPhysicsSyncService.SpawnExplosionVisual`, `ExplosionSpawnSyncPatches`, `HandleExplosionSpawnObject` | Remote peer sees `spawnObject` debris/FX on stomp; no double on stomper |
| T2 | Single scrape owner (kill double) | `ItemMovingSoundHelper`, `MovingObjectSoundService`, `WorldPhysicsSyncService`, `LanNetworkManager.Handlers` NotifyBodyPush*, optional `ItemSounds` patch | Pusher hears one scrape |
| T3 | Stop-delay overhaul | Same audio + physics files | Stop → fade starts ≤ ~0.15s after player stops pushing (plus 0.5s fade tail only) |
| T4 | Build + deploy both plugins | csproj / Steam + SecondDarkwood | DLL timestamps updated |

## Non-goals

- Separate AppData dual-box save isolation
- Changing PhysicsState rate globally
- New NetMessageType (reuse existing)

## Verification (playtest)

1. Host stomps explosive mushroom → client sees main boom **and** white secondary.
2. Client stomps → host sees both; stomper no double pile of secondaries.
3. Body-push crate: pusher **one** scrape; peer hears scrape.
4. Stop walking into crate → scrape dies ~immediately (≤0.5s fade), not multi-second tail.
5. E-drag still starts/stops clean (no regression).

## Status 2026-07-10

**Implemented + deployed** (Steam + SecondDarkwood plugins, DLL ~596480).

Review fixes applied:
- `HandleExplosionSpawnObject` skips when local `Explodes.spawnObject` exists (SpawnExplosionVisual owns secondaries; kills remote double-pile race).
- `TickLocalPushScrapeStop` / `ForceStopLocalPush` ignore E-drag claims (`beingDragged`, local drag, `_dragClaims` / `_remoteDragItemNames`).
