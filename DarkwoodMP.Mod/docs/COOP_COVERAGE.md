# Co-op coverage checklist

This is a compact code-coverage and runtime-verification checklist for the
Path B Horde mod.

**Current baseline:** product `0.8.1`, protocol **25**, message IDs through
`ScenarioStateBulk` (138), host-authoritative N-player LAN. Steam uses the same
message contracts through its separate networking transport.

Code coverage and runtime coverage are different:

- **Code covered** means the current source contains the intended authority and
  apply path.
- **Runtime pending** means a Unity dual-box or three-player check is still
  required.
- **Deferred** means the behavior is intentionally not implemented or is
  outside the current stabilization scope.

---

## Verification rules

For each domain, check:

1. host action observed by every client;
2. client action observed by the host and other clients;
3. a third player joining while the domain is active;
4. disconnect and reconnect cleanup;
5. players in different locations when location state matters;
6. duplicate, delayed, malformed, or out-of-order packets.

State must be keyed by `PlayerId`. Host fan-out must reach every ready peer,
not only the first connection. A late join must receive the relevant bulk
state without changing existing players' state.

---

## Coverage summary

| Domain | Main code paths | Current status |
|---|---|---|
| Session and handshake | `LanNetworkManager`, `ConnectionBackend`, `HostMigration` | Code covered; runtime pending |
| World share and saves | `WorldSaveShareService`, `ClientStateBackup`, `SaveSyncPatches` | Code covered; runtime pending |
| Clock and pause | `ClientTimeAuthorityPatches`, `SleepSyncPatches`, `TimeSync`, `WeatherSync` (Rain/Lightning/fog host→client) | Code covered; runtime pending |
| Flags and reset | `FlagSyncPatches` (story sync; `player_in*` local-only ephemeral), `NetworkApplyGuard`, `NetworkResetRegistry` | Code covered; runtime pending |
| Player state | `PlayerStateMessage`, player proxy and animation paths | Code covered; runtime pending |
| Entity AI and snapshots | `EntityStateBroadcastService`, `ClientEntityInterpolationService`, `ClientAIDisablePatches`, `BirdAreaSyncPatches` (host birds + proxy presence), `PorterSpawnerSyncPatches` (host porter + multi-avatar `InSightOfPlayer`), `CharacterSpawnPointSyncPatches` (host actuallySpawn) | Code covered; runtime pending |
| Physics and world objects | `WorldPhysicsSyncService`, door, generator, trap, drag, ChainParent (`ChainState` 134), ShadowArmor (`ShadowArmorState` 135), world Burn (`WorldBurnState` 137), Infection via `EntitySpawn` 86, RandomObject/Object/ObjectPool/SpawnPrefab/RandomSpawnArea/CharacterSpawnPoint host-auth, `GameEventsBulk` destroyOnFire latch, early-gen `WorldGenerator`/`WorldChunk`/`ObjectPoolSpawnerController` host-auth, EventTriggers sight `AnyInSight` | Code covered; runtime pending |
| Locations and grids | `LocationEnter` / `LocationExit`, location visibility patches | Code covered; split-map runtime pending |
| Map markers and discoveries | Live msg 69 + late-join `MapStateSync` (`isOnMap` scan) | Code covered; runtime pending |
| Inventory and containers | container, dropped-item, death-bag, journal, trade, UniqueItemSpawner TeddyBear, InventoryRandom, Feeder **116** / Lure **117**, ExperienceMachine (hideout oven) enable + flags | Code covered; runtime pending |
| Combat and threats | combat handlers, proxy damage, projectiles, shadows, night death, mid-fight ShadowArmor HP, Flame/molotov world Burn (137; Character/Player still 41/44), night scenario late-join latch (`ScenarioStateBulk` 138) | Code covered; runtime pending |
| Story and dialogue | `DialogOutcome`, `DialogTreeState`, `GameEventsFired` + late-join `GameEventsBulk` (136), Examinable **110** (host onExamine; DescriptionPool draw personal) | Code covered; runtime pending |
| Dreams and epilogue | `DreamSession`, `DreamSyncManager`, dream door and scene paths | Code covered; runtime pending |
| Audio and spectator mode | player/entity audio, culling, spectator listener and grid | Code covered; runtime pending |
| Balance features | loot sharing and allowlisted dream NPC presence | Code covered; runtime pending |

---

## Current stabilization coverage

### Ordered snapshots

`PlayerState`, `EntityState`, and `PhysicsState` carry sequence metadata.
Unreliable duplicate or older snapshots are rejected per sender and session.
Reliable physics events use a separate ordering stream.

Runtime checks still needed: reorder snapshots on both host and client paths,
cross a session reset, and verify a late unreliable update cannot overwrite a
reliable door, trap, or generator event.

### Combat authority

The host derives the attacker from the receiving peer. It uses the host proxy
for attack origin and range checks, validates finite positions and damage,
accepts only known target types, and rejects unknown victims.

Runtime checks still needed: host attacks client, client attacks host, client A
attacks client B, melee and projectile paths, friendly fire on and off, and
dead or missing targets.

### Client AI suppression

Client suppression applies directly to `Sniffer` and `AIPath` components. It
does not require a `Character` component to be present.

Runtime checks still needed: inactive and component-only objects, remote
player proxies, and scene reloads.

### BirdArea presence

Host owns `BirdArea.Start` bird spawn and aggro routines. Clients skip Start
and local trigger side effects. Host trigger presence accepts `Player` and
`RemotePlayerProxy` (refcount so one peer leaving does not clear while another
remains). Proxy enterers are dove via `attackCharacter`, not host-only
`attackPlayer`.

Runtime checks still needed: host walks in, client walks in, both inside then
one exits, and birds visible/diving on the observing peer.

### PorterSpawner / InSightOfPlayer

Host owns Porter NPC spawn (`PorterSpawner.Start` / `waitToSpawn`). Clients
Prefix-skip both so independent out-of-sight timers cannot place a second
Porter. Host `InSightOfPlayer.checkSight` uses `HostPlayerIdentity.AnyInSight`
(local `Player.isInSight` OR each remote proxy as viewer via the same method).
Observation: entity snapshots. No dedicated message.

Runtime checks still needed: client alone near the volume (host treats proxy
FOV as in/out of sight), host-only Porter appear on peers, host near volume
cancels client-driven spawn path.

### Dream and world scoping

When a dream pad is active, dream objects are resolved under
`Dreams.dreamLocation`. Cleanup does not use a global name lookup because the
overworld and dream copies can have the same names.

Runtime checks still needed: dream entry, leave-door dialogue, cleanup,
re-entry, late scene loading, and a peer remaining in the overworld.

### Packet validation

`NetReader` bounds primitive and byte-array reads. Snapshot decoders reject
invalid collection sizes or incomplete payloads where the message contract
requires the data.

Runtime checks still needed: truncated and oversized payloads through both
transports. Unit tests cover the shared reader and policy helpers.

---

## Authority decisions

- The host owns world simulation, combat damage, entity AI, story world
  mutations, and the day/night clock.
- Clients own their personal inventory, skills, and morning-trader reputation.
- Shared journal identity and shared story-NPC reputation are host-authoritative.
- Physical dialogue item rewards remain personal to the speaking player.
- Client AI and autonomous client time progression are suppressed while
  connected.
- **BirdArea:** host-only AreaBird spawn + trigger AI; clients suppress local
  `BirdArea.Start` / triggers. Host presence includes remote proxies; no
  dedicated bird message (entity snapshots). Dual-box runtime pending.
- **PorterSpawner:** host-only `Start` / `waitToSpawn` (clients Prefix-skip).
  Host `InSightOfPlayer` sight considers any session avatar
  (`HostPlayerIdentity.AnyInSight`). Dual-box runtime pending.
- Dream objects and dream cleanup are scoped to the active dream location.
- Host migration elects the lowest positive surviving `PlayerId`; dream
  migration remains deferred.
- **UniqueItemSpawner (TeddyBear):** host-only `spawn` (clients Prefix-skip).
  Observation: host `ContainerItem` PlaceItem when peers are connected at
  spawn time; otherwise client open → `ContainerStateRequest` /
  `ContainerStateSync`. No dedicated TeddyBear message. Runtime dual-box
  pending (host place → client open same chest; reverse open order; join after
  host already spawned).
- **InventoryRandom:** host-only `randomize` + `spawnItems` (clients
  Prefix-skip, set `spawnedItems`). Location difficulty calls `spawnItems`
  directly (must gate both). Observation: host Broadcast `ContainerStateSync`
  (76) when peers are connected at spawn time (multi-slot chests + NPC trader
  new-day refresh, including empty NPC wipe); otherwise client open →
  `ContainerStateRequest` / `ContainerStateSync` (`ContainerSearchedPatch`) —
  same late path as UniqueItemSpawner. No dedicated message. Reverse-check:
  host rolled → client open matches; client must not roll; late joiner open
  still requests host snapshot; trader day-roll with peers present pushes
  snapshot (not open-only). Runtime dual-box pending.
- **RandomSpawnArea:** host-only `spawnPrefab` (clients Prefix-skip).
  Observation via entity / WorldSaveShare — no dedicated message. Runtime
  dual-box pending.
- **RandomObjectSpawner:** host-only `spawnObject` (clients Prefix-skip;
  Offline allowed until Role is Client). Observation: Characters via
  `EntityStateBroadcast` + client pending match / `SpawnEntityLocally`;
  Items/saveables via WorldSaveShare / save load (Awake/worldgen timing).
  No dedicated message. Runtime dual-box pending (host NPC/loot present on
  client; reverse initiator N/A — clients must not roll).
- **ObjectSpawner:** host-only private `spawnObject` (clients Prefix-skip;
  same Role gate as RandomObjectSpawner). Vanilla `Start` → interval routine
  with optional `randomOffset` / `loop` / AddPrefab|AddPooledPrefab.
  Observation rides existing entity / WorldSaveShare paths — no dedicated
  message. Runtime dual-box pending.
- **WormsSpawner / Location.spawnWorm:** host-only (clients Prefix-skip).
  Decompile: `Location.spawnWorm` picks a distant `WormsSpawner` and calls
  `spawn()` → nightMushroom prefab. No other C# callers found (likely
  animation/UnityEvent). No dedicated message. Runtime dual-box pending.
- **CharacterSpawnPoint:** host-only `actuallySpawn` + `waitToSpawnCharacter`
  (clients Prefix-skip). Decompile: `Location.spawnCharacters` →
  `spawnCharacter` (instant or delayed wait); WorldGenerator BigBiome also
  calls `actuallySpawn` directly. Chance roll + `AddPrefab("Characters/"+type)`.
  Observation via `EntityStateBroadcast` — no dedicated message. Runtime
  dual-box pending (host NPC present on client; reverse N/A — clients must
  not roll).
- **SpawnPrefab:** host-only `Start` (clients Prefix-skip). Decompile:
  `Core.AddPrefab(prefab GameObject, …)` then `Destroy(self)`. Both peers
  would place duplicates. Observation via entity / WorldSaveShare —
  GameObject-overload AddPrefab is not string-path PhysicsSpawnSync. No
  dedicated message. Runtime dual-box pending (reverse N/A — clients must
  not Start-spawn).
- **ObjectPoolSpawner:** host-only `spawnObject` + `tryToSpawn` (clients
  Prefix-skip). Awake still registers with `ObjectPoolSpawnerController` on
  all peers so host worldgen can drive spawn. Observation via entity /
  WorldSaveShare — no dedicated message. Runtime dual-box pending.

---

## Deferred or incomplete areas

- **Full dual-box / three-player campaign soak — parked (runtime verification,
  not a static coverage gap).** Every domain row above is **code covered** with
  host↔client send/apply/authority paths (or an explicit parked bullet with
  decompile citation). This soak is the Unity dual-box / three-player playtest
  that flips those rows from "runtime pending" to "runtime verified." It is
  intentionally outside the decompile-loop objective finish bar (implement or
  park with citations). No game processes were available during the static
  pass. Protocol **25** unchanged.
- **`AnimationPlay` — parked (DEFERRED-ok cosmetic).** Decompile
  `AnimationPlay.cs`: local RNG for `randomAnims`, `randomizeStartFrame`,
  twitch frame, and play-delay loops; optional rigidbody Push on anim events.
  No story flags / GE / shared inventory. Peers may desync decorative anim
  phase only. Do **not** sync unless playtest shows physics Push affecting
  co-op. Protocol **25** unchanged.
- **`MagicContainer` — parked (empty stub).** Decompile `MagicContainer.cs`
  has empty `Start`/`Update` only. No co-op surface. Protocol **25** unchanged.
- **`DescriptionPool` / Examinable onExamine — host-auth triggers (code);
  pool draw personal.** Decompile `Examinable.examine` draws
  `DescriptionPool.getDescriptionFromPool` (removes a string) then
  `Core.sendTriggerInfo(..., onExamine)`. Clients keep local HUD + local pool
  draw; client `onExamine` triggers are Prefix-blocked; host re-runs examine
  (HUD suppressed) for GE + broadcasts examined /
  `displayedDescriptionPool` flags (msg **110**). Shared pool depletion is
  not wire-synced (would need the drawn key on the wire). Dual-box still
  runtime-pending. Protocol **25** unchanged.
- **`SpriteRandomizer` — parked (DEFERRED-ok cosmetic).** Decompile
  `SpriteRandomizer.cs`: `init` rolls color / lightness / alpha / rotation /
  mirror / height / anim clip / sprite from local RNG, then `Destroy(this)`.
  Tooltip on `randomizeOnLoad` warns large problems when a non-circle
  collider combines with mirror / rotation randomize. Peers may diverge
  visually; do **not** sync unless a future playtest proves physics/collider
  divergence that affects co-op. Protocol **25** unchanged.
- **`QuestRandomizer` — parked (unused / rare debug Bring-me-X).** Decompile
  `QuestRandomizer.cs`: `onPlayerEnter` rolls `itemAmount` 2–3 and a type from
  `allowedInvItemRequirements`, then `Core.displayMessage("Bring me {0} of
  {1} till the end of the day.")` — hardcoded English (not localization keys).
  `checkContents` marks `questComplete` from a referenced `inventory`;
  `onNewDay` clears that inventory and, if complete, drops
  `weapon_rifle_01_boltAction` + ammo. No other C# type references in the
  Assembly-CSharp tree; no campaign dialogue/localization tied to this
  component (story “bring me” lines are Wolf/Musician dialogue, not this
  MonoBehaviour). Co-op tension if ever placed: personal HUD message + shared
  world inventory would need a deliberate design (not invent sync now).
  Protocol **25** unchanged.
- **`Underwater` — parked (host Character AI / ClientAIDisable).** Decompile
  `Underwater.cs`: no `Update`; submerge/emerge/teleport attack are driven from
  `Character` AI paths (`checkStuff`, attack finish, hit reactions). Clients
  already Prefix-skip those Character methods via `ClientAIDisablePatches`;
  peers observe pose/anim via `EntityStateBroadcast`. Do **not** invent a
  dedicated Underwater message unless a future find shows client-local
  Underwater state that must diverge from host Character snapshots.
  Protocol **25** unchanged.
- **`Player.craftedItems` / `CraftingRecipes.timesCraftedLimit` — parked
  (personal by design, no sync).** Decompile:
  - `CraftingRecipes.reachedMaxNumberOfTimesCrafted` gates on
    `Player.Instance.getCraftedItem(InvItem.type)._int >= timesCraftedLimit`
    (`CraftingRecipes.cs`); `doCraft` only calls `Player.Instance.addToCraftedItems`
    when `timesCraftedLimit > 0`.
  - `Player.craftedItems` (`List<StringAndInt>`) lives on the **Player** and is
    saved/restored in `Player.SaveState` beside personal `recipes`, health,
    skills — not on `Controller` / world save.
  - UI copy is first-person personal (`Playermsg_cantCraftMoreOfThis`: "I can't
    craft more items of this type.").
  - Shared craft progression that *is* world state is
    `Controller.workbenchLevel` (already live + late-join via `WorkbenchLevel` /
    `WorkbenchLevelSync`); the mod `doCraft` Harmony only emits on workbench
    upgrades (`JournalSyncPatches.WorkbenchUpgradePatch`).
  - `removeOnCraft` is a serialized field with **no C# readers** in the
    decompile; the live limit path is timesCraftedLimit → craftedItems.
  Host-gated craft-count sync would wrongly lock peer B out of B's personal
  limit after A crafts. World-unique story items stay on UniqueItemSpawner /
  containers / GameEvents, not craft counts. No new msg; protocol **25**
  unchanged.
- Late-join bulk for night scenarios: **`ScenarioStateBulk` (138)** in light
  phase. Host snapshots scenario name + fired latch flags from decompile
  `CustomEvent.started`, `RandomEvent.startedToday` / `disabled`, and
  `NightScenario.currentEvent` (+ `timeStarted` day/time). Client apply sets
  those fields only — does **not** call `CustomEvent.fire` /
  `RandomEvent.fire` / `checkFrequencies` (avoids re-spawn;
  `ClientBlockRandomEventFirePatch` remains a second line of defense). Live
  path stays host `NightScenario.checkFrequencies` → `ScenarioEventFired`
  (40) → client pending `frequencyMet` → `CustomEvent.fire` (RandomEvent
  body blocked on clients). Spawned night uniques still arrive via entity
  snapshots; GE side effects via `GameEventsBulk` (136). Dual-box late-join
  still runtime-pending.
  **GameEvents late-join is no longer deferred** —
  host heavy phase sends `GameEventsBulk` (136) for components with
  `fired && !multipleFire` (skips `multipleFire`, `isSavedDelayedEvent`,
  ephemeral `def_glow` / `def_shadow`, and `dream_*` when no dream is active).
  Joiner applies via the live `GameEventsFired` path under `NetworkApplyGuard`.
  Host also records one-shot `destroyOnFire` identities at live fire time
  (decompile `GameEvents.fire` destroys the GO after event delays) and merges
  them into the bulk so joiners still apply shells missing from the host scan.
  Dual-box late-join still runtime-pending.
  (MapElement discoveries are no longer deferred — `MapStateSync` populates
  from host `isOnMap` elements; dual-box late-join still runtime-pending.)
  (Infection ground splats are no longer deferred — heavy phase 9
  `SendInfectionStatesTo` reuses live `EntitySpawn` 86; dual-box still
  runtime-pending.)
- **`Resonator` / `RoadConnector` — parked (no co-op mutation).** Decompile
  `Resonator.onNightStart` is empty (dead `waitToSpawnWorm`); `RoadConnector`
  is worldgen pathfinding only (host gen + WorldSaveShare). Protocol **25**.
- **`RandomEvent.randomizeStartTime` — host-auth (code).** Decompile rolls
  `timeToStart` from `Events.initialize` and each `onNewDay`. Clients already
  Prefix-skip `RandomEvent.fire`; now also skip schedule rolls so early gen /
  day edges cannot diverge from host. Late-join latch remains
  `ScenarioStateBulk` (138). Dual-box still runtime-pending.
- **`RandomNumberGenerator` / `ChapterPreset.initFlags` — host-auth (code).**
  Decompile: `RandomNumberGenerator.Awake` → `init()` rolls digit tables used by
  `Padlock.Start` (`randomCombination` + `numbersDict`); `WorldGenerator.generateWorld`
  picks a `ChapterPreset` and may call `initFlags` (`randomFlags` story outcomes)
  before finish. Connected clients Prefix-skip both so early gen cannot diverge
  from host; WorldSaveShare / FlagSync supply truth. Dual-box still runtime-pending.
- **WorldGenerator early-gen spawns — host-auth (code).** Clients Prefix-skip
  `spawnMiscObjects`, `spawnFreeRoamingCharacters`, `spawnGlobalCharacters`,
  `spawnNightObjects`, `respawnAllEnemies`, and `ObjectPoolSpawnerController.spawn`
  in addition to `spawnRandomObjects`
  (early `generateWorld` before `WorldGenSharePatch` blocks `onFinished`).
  Dual-box still runtime-pending.
- **`RandomWorldObjects` / `WorldChunk.spawnRandomObjects` — host-auth (code).**
  Decompile: `Controller.startDay` (hard night) and `WorldGenerator.generateWorld`
  → `spawnRandomObjects` → per-chunk `getUnspawnedObject` RNG + `AddPrefab` +
  `addToSaveable`. Clients Prefix-skip `WorldChunk` /
  `WorldGenerator.spawnRandomObjects` (early gen can run before
  `WorldGenSharePatch` blocks `onFinished`; startDay world edges already
  suppressed). Host share / host startDay own placements. `MoveOnSpawned` is
  Awake jitter on host-placed prefabs — no separate sync. Dual-box still
  runtime-pending.
- **ObjectStages — parked (no dedicated msg).** Decompile
  `ObjectStages.cs`: each stage is only `duration` + `List<GameEvents>`;
  `setStage` updates private `currentStage` / `timeSpent` / `isActive` then
  calls `gameEvents[i].fire()` — no mesh, collider, or other world state of
  its own. Player-visible effects are therefore the same as any other GE:
  live host fire → `GameEventsFired` (65) via `GameEventsFiredPatch`; late
  join → `GameEventsBulk` (136). Client one-shot fires from a local stage
  timer are blocked by that patch. `Trigger` only `Destroy`s the component
  after use. Do **not** invent `StageState` unless a future find shows
  stage index itself must be visible without going through GameEvents
  (msg **137** is `WorldBurnState`, not StageState).
- **`VineSpawner` host-only Start — parked (clients must spawn locally).**
  Decompile `VineSpawner.Start`: `Core.AddPrefab` inactive vines, then wires
  `GameEvents.events[0].targetGameObjects[i]` to those instances (plus
  optional background). Awake sets `spawned` when `Core.loadingGame`; Start
  early-outs on `spawned`.
  Mod apply path (`GameEventNetHandlers.ApplyGameEventsFired`) resolves the
  `GameEvents` component by name/pos then calls `best.fire()` under
  `NetworkApplyGuard`. Vanilla `GameEvent.fire` acts on the **local**
  `targetGameObjects` list refs — not name/pos lookup of vine GOs. If clients
  Prefix-skipped `VineSpawner.Start`, those list slots stay null and
  `GameEventsFired` / `GameEventsBulk` apply would no-op vine activate on
  clients. Only peer divergence is cosmetic `Core.getRandomHalfRotation()`.
  Do **not** host-auth skip Start without a separate vine-identity sync.
  Protocol **25** unchanged.
- **`ActionWhenTurnedOn` — parked (covered by LightState / Item.turnOn).**
  Decompile: `Item.turnOn` / `turnOff` set `ActionWhenTurnedOn.turnedOn`
  true/false; `powerDown` clears it. Live + late-join lamp sync already
  applies via `LightState` (6) → `ApplyLightState` → `item.turnOn()` /
  `turnOff()` under `NetworkApplyGuard`, which re-arms the same `turnedOn`
  latch and therefore `Action.Update` damage/refresh ticks. No dedicated
  `ActionWhenTurnedOn` message. Protocol **25** unchanged.
- **`EventTrigger.fired` / `firedExit` late-join — parked (no dedicated msg).**
  Decompile `EventTrigger.cs` / `EventTriggers.cs`:
  - `fire(...)` early-outs on `(fired && !multipleFire)`; on success sets
    `fired=true` / `firedExit=false` then calls `gameEvents.fire()` only
    (optional `RemovePooledPrefab` for item triggers). SaveState persists
    `fired` / `firedExit` for SP reload — not a separate world mesh.
  - `fireExit` early-outs on `firedExit`; sets `firedExit=true` (and clears
    `fired` when `multipleFire`) then `gameEventsExit.fire()`.
  - Area path: `OnTriggerEnter` → `fireEventTrigger(area)` (Player.Instance
    only in vanilla); MP adds proxy enter via `EventTriggersProxyPatches`
    without suppressing local-body enter (needed for `multipleFire` ambients).
  Live MP: one-shot GEs are host-auth (`GameEventsFiredPatch` Prefix blocks
  client `!multipleFire` `fire()` unless `NetworkApplyGuard`); host fan-out
  is `GameEventsFired` (65); late join is `GameEventsBulk` (136) which
  latches the same `GameEvents.fired` one-shots. CustomCursorAction
  `onActivate` is client→host via `ActivateCursorAction`, not a local
  one-shot fire. After late join, a joiner with `EventTrigger.fired=false`
  can still *enter* `EventTrigger.fire` on area/sight, but linked GE
  side effects are blocked by bulk latch and/or the client one-shot Prefix.
  Do **not** add `EventTriggerBulk` unless a future find shows trigger-local
  state (not GE) that must be visible without re-entering the volume.
  (Msg **138** is `ScenarioStateBulk`, not EventTrigger.)
- Wrong-save warning UI: **code shipped.** Join slot picker marks
  `[DIFFERENT CAMPAIGN]` when slot meta CampaignId ≠ host package and warns
  on overwrite confirm. Host-push / RestoreSelf refuse paths call
  `WrongSaveWarning` (HUD `displayMessage` + join label `WRONG SAVE`).
  Terminal share failure (`WORLD SHARE FAILED:`) unchanged. Dual-box soak
  still pending.
- Complete interaction-lock coverage, including simultaneous container and
  crafting races. **Workbench exclusive lock (msg 119) remains parked by
  playtest product decision (0.7.40):** both players may open/use the same
  bench; `WorkbenchOpenLock` is a stub; wire handler ignores traffic. Vanilla
  `Workbench.open` has no exclusive latch — re-enable only if a future
  playtest asks for one-crafter-at-a-time again (restore grant/deny + Harmony
  Prefix bodies; see 0.7.x `WorkbenchLockPatches` park comment).
  **Container simultaneous-open:** parked as incomplete exclusive UI — loot
  mutations are already host-validated (`ContainerItem` Take/Place/Remove +
  `ContainerStateRequest`/`Sync` on open). Dual open only means dual UI; host
  denies bad takes. Do not invent a container lock unless playtest shows a
  remaining race after host validation.
- Host migration during an active dream: **parked.** `HostMigration` refuses
  mid-dream authority flip and disconnects without GRANT (dream session is
  not migratable). Dual-box mid-dream host-loss still soak-pending.
- Exact proxy field-of-view parity for general EventTrigger sight checks:
  **done in code** — host `EventTriggers.isCurrentlyInSightOfPlayer` uses
  `HostPlayerIdentity.AnyInSight` (`Player.isInSight` + proxy `_transform`
  swap, including `inSightOfPlayerRadius`), same as Porter /
  `InSightOfPlayer.checkSight`. Dual-box sight-trigger runtime still pending.
  **PorterSpawner / `InSightOfPlayer.checkSight` improved** via
  `HostPlayerIdentity.AnyInSight` + client spawn skip — see stabilization
  section above.
- Some dream, spectator, and dialogue presentation edge cases — **parked as
  presentation-only (not world-authority gaps):**
  - Spectator dialogue UI / welcome and gossip randomness (historical 0.7.x
    deferred notes; no shared world mutation).
  - Portrait / dialogue overlay edge cases after world-only drains (0.7.75
    line; live DialogOutcome + lookKeyhole drain already host-auth).
  - Lost dream-chain packet fallback (0.7.x deferred; DreamSession /
    DreamChainStart exist — soak missing packet recovery).
  Do **not** invent sync for cosmetic HUD/overlay variance unless playtest
  shows a story latch or world object diverging. Protocol **25** unchanged.
- **`PlayerSpawn` / `PlayerSpawnPoint` / `PossibleRespawnLocation` — parked
  (local registry / storage example).** Decompile: `PlayerSpawn` registers into
  `WorldGenerator.playerRespawnPoints` when `isRandomRespawn`; `PossibleRespawnLocation`
  Awake registers into `possibleRespawnLocations`; `PlayerSpawnPoint` is a
  Storage example (`PlayerLocator`) unused by campaign co-op paths. Host owns
  respawn picks; per-peer list registration is local scene bookkeeping. Protocol
  **25** unchanged.
- **`WaitAndDie` — parked (FX / timer; onTime → GE host-auth).** Decompile
  `WaitAndDie.die2`: optional `fireTrigger` → `Core.sendTriggerInfo(..., onTime)`
  else `RemovePooledPrefab`. CharacterMessage / epilogue UI paths are local.
  Story side effects are EventTrigger → `GameEvents.fire` (client one-shots
  blocked by `GameEventsFiredPatch`; host fan-out live 65 / bulk 136). Do **not**
  invent WaitAndDie sync. Protocol **25** unchanged.
- **`Broadcaster` — parked (serializer interest util).** Decompile static
  reflection helper for LevelSerializer interests — not gameplay mutation.
  Protocol **25** unchanged.
- **`UpgradeItemMenu` / `UpgradeItemBtn` — parked (personal item upgrades).**
  Decompile: `tryToCraft` → `Player.startUpgrading(invItem, itemUpgrade)` on the
  local player's workbench item UI; consumes personal inventory materials /
  writes personal `InvItemClass` upgrades. Shared bench world state remains
  `workbenchLevel` (synced). No upgrade-craft msg. Protocol **25** unchanged.
- **`WhereAmI` `player_in*Hideout` flags — already local-only (code).** Decompile
  clears/sets `player_inFirstHideout` / Second / Third each 1.5s tick from local
  `Player` position. `FlagSyncBoolPatch.IsLocalOnlyEphemeralFlag` skips any
  `player_in*` name (playtest thrash if synced). Story flags still FlagSync.
  Protocol **25** unchanged.

Do not mark these items as runtime-verified from static or unit tests alone.
