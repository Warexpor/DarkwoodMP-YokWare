## Batch 42 — NO-SHIP (stay on 0.8.73)

- Trap `id=0` spam + junk ContainerTakeDenied: dig only; stale 0.8.34 logs;
  junk path already fixed in 0.8.36; id=0 needs fresh 0.8.73 dual-box repro.
- No code change / no redeploy. Live md5 `a7abd68ab8a68db8b03ccb36c93087d5`.

## 0.8.73 Batch 41 (code-only until dual-box)

- Hideout tutorial / far-GE `displayMessage`: Prefix-null no longer NREs
  `GameEvent.fire` MoveNext (Postfix-hide like HelpMessage).
- Regression: 0.8.72 door/window getHit Finalizer OK; HelpMessage Postfix-hide OK.

# Dual-box playtest (0.8.73)

1. Deploy same DLL to Steam host + SecondDarkwood client (md5 match).
2. Menu shows **0.8.73** and protocol **25** on both boxes.
3. Host stands away from Hideout1 tutorial volume; client walks in —
   host LogOutput has **no** `GameEvent+<fire>d__77` NullReferenceException;
   client near the volume still sees the tutorial tip.
4. Optional: host examines object client already examined — host gets no
   flavor flash (create-then-hide).

## Host-ready join (0.8.73)

Unchanged from 0.8.72 — wait for HostWorldReady (139) before Enter World.

## 0.8.72 Batch 41 (code-only until dual-box)

- Door/window board smash: getHit throw no longer leaves IsInsideGetHit sticky
  (destroyBarricade sync works again on that board).
- Nesting-aware BeginGetHit depth — outer board suppress survives nested hit.
- Regression: 0.8.71 explosion/trap/pause Finalizers OK; 0.8.70 HostCheckStuff /
  EntitySound / ClientProjectile Finalizers OK.

# Dual-box playtest (0.8.72)

1. Deploy same DLL to Steam host + SecondDarkwood client (md5 match).
2. Menu shows **0.8.72** and protocol **25** on both boxes.
3. Enter world; board a door/window; smash boards on night — peer sees destroy;
   no silent “boards gone on host only” after an edge throw mid-getHit.
4. Optional: rapid multi-board smash — no stuck suppress on later boards.

## Host-ready join (0.8.72)

Unchanged from 0.8.71 — wait for HostWorldReady (139) before Enter World.

## 0.8.71 Batch 38 (code-only until dual-box)

- Grenade/explosion onActivate throw — IsInsideSpawnObjects / ActivationDepth no
  longer sticky (host AddPrefab not permanently treated as explosion FX).
- Local explode / FastProjectile FixedUpdate throw — IsInsideLocalExplosion /
  IsInsideFastProjectileRaycast no longer stuck (peer damage + impact sync OK).
- Banshee agitated throw — SuppressHostScreamForward no longer stuck off.
- Trap place progressBar throw — InsideTrapPlacement no longer suppresses harvest.
- Map/journal/dialogue open throw — SuppressPause no longer stuck (co-op pause OK).
- Regression: 0.8.70 HostCheckStuff / EntitySound / ClientProjectile Finalizers OK;
  0.8.69 EmptyRoutine OK; trade upgrades from 0.8.68 still OK.

# Dual-box playtest (0.8.71)

1. Deploy same DLL to Steam host + SecondDarkwood client (md5 match).
2. Menu shows **0.8.71** and protocol **25** on both boxes.
3. Enter world; throw molotov/grenade + shoot + place beartrap + open map — no
   stuck explosion FX spam / damage misroute / pause lock.
4. Optional: banshee near remote peer — scream still forwards after any edge throw.

## Host-ready join (0.8.71)

Unchanged from 0.8.70 — wait for HostWorldReady (139) before Enter World.

## 0.8.70 Batch 37 (code-only until dual-box)

- NPC near remote peer: checkStuff throw no longer leaves temporarySpawned /
  wantToDespawn / forestSpirit permanently wrong.
- Creature growl/idle/escape sound throw — InsideCharacterSounds no longer stuck
  (peer audio forward works again).
- Client bullet onCollide throw — IsInsidePlayerBulletCollision no longer stuck
  (peer projectile damage routing OK).
- Regression: 0.8.69 EmptyRoutine prepareDream/onDeath/waitToSpawn* still OK;
  trade upgrades from 0.8.68 still OK.

# Dual-box playtest (0.8.70)

1. Deploy same DLL to Steam host + SecondDarkwood client (md5 match).
2. Menu shows **0.8.70** and protocol **25** on both boxes.
3. Enter world; night spawn + shoot + hear enemy growl — no stuck audio/damage.
4. Optional: force an NPC checkStuff edge near a remote — despawn still happens later.

## Host-ready join (0.8.70)

Unchanged from 0.8.69 — wait for HostWorldReady (139) before Enter World.

## 0.8.69 Batch 36 (code-only until dual-box)

- Client waits for host DreamStarted with empty prepareDream — no Unity
  "routine is null" / StartCoroutine NRE.
- Shared-dream death (host or client) — onDeath Prefix skip no longer NREs.
- Client connected: CharacterSpawner worm wait + CharacterSpawnPoint wait
  coroutines skip cleanly (no StartCoroutine null).
- Regression: door scrape / beartrap / HelpMessage proximity / trade upgrades
  from 0.8.68 still OK.

# Dual-box playtest (0.8.69)

1. Deploy same DLL to Steam host + SecondDarkwood client (md5 match).
2. Menu shows **0.8.69** and protocol **25** on both boxes.
3. Enter world; client near hideout tutorial GE; no GameEvent/prepareDream
   StartCoroutine null spam in either log.
4. Optional: shared dream death once — both sides stay up without routine-null.

## Host-ready join (0.8.69)

Unchanged from 0.8.68 — wait for HostWorldReady (139) before Enter World.

## 0.8.68 Batch 33 (code-only until dual-box)

- Sell workbench-upgraded melee / gun to NightTrader → peer opens trade → stock
  shows same upgrade names (getModdedDamage / SaveState.upgrades parity).
- Sell flashlight with light ON (`shouldBeActive`) → peer trader stock keeps ON
  flag; buy-back restores active light.
- Regression: empty-mag + 0-dur sold guns from 0.8.67 still OK; recipe stock from
  0.8.62 still OK.

# Dual-box playtest (0.8.68)

1. Deploy same DLL to Steam host + SecondDarkwood client (md5 match).
2. Menu shows **0.8.68** and protocol **25** on both boxes.
3. Host upgrades axe → sells to trader → client opens same trader → upgrades
   present. Repeat with flashlight ON.
4. Buy-back on either peer restores upgrades / active flag.

## Host-ready join (0.8.68)

Unchanged from 0.8.67 — wait for HostWorldReady (139) before Enter World.
