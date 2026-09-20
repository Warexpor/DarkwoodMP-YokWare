# Changelog

## Versioning

The current product line is `0.8.x`. The plugin and display version are
**0.8.0**. The current Horde wire protocol is **25** (unchanged from 0.7.81;
this line is an architecture rewrite, not a wire bump).

This file is a public ship log. Code-only status and runtime status are called
out separately. A runtime item is not considered verified until it has been
tested in the game.

---

## 0.8.0: Architecture rewrite (structure first)

Intentional major structural pass on Path B. Gameplay math and known patch
targets stay; the code shape does not. 0.7.81 remains the last pre-rewrite
ship line (committed/pushed backup).

### Goals

- Break up god-files (`LanNetworkManager.Handlers`, `WorldPhysicsSyncService`,
  flat `Patches/`) into domain modules with clear ownership.
- Prefer composition (handler services) over endless `partial` dumps.
- Keep protocol **25** wire formats stable unless a later 0.8.x needs a bump.
- Strip over-engineering and wrong techniques when found; no sync crutches.

### Parked / deferred (investigation)

- **`QuestRandomizer`:** parked — rare/debug-style Bring-me-X (`Core.displayMessage`
  hardcoded English; random item count/type; shared `inventory` check +
  next-day rifle reward). No other C# callers in Assembly-CSharp; campaign
  “bring me” lines are dialogue (Wolf/Musician), not this component. Personal
  message + shared inventory would need a design if ever used — do not invent
  sync. Evidence in `COOP_COVERAGE.md`. Protocol **25** unchanged.
- **`Underwater`:** parked — host Character AI only; no dedicated msg.
  Driven from `Character` paths already client-suppressed via
  `ClientAIDisablePatches`; peers observe via `EntityStateBroadcast`.
  Evidence in `COOP_COVERAGE.md`. Protocol **25** unchanged.
- **`VineSpawner` host-only Start:** parked — clients must spawn + wire
  `GameEvents.events[0].targetGameObjects` locally; `ApplyGameEventsFired`
  calls `best.fire()` which uses those list refs (not vine name/pos lookup).
  Peer divergence is cosmetic vine Y rotation only. Evidence in
  `COOP_COVERAGE.md`. Protocol **25** unchanged.
- **`ActionWhenTurnedOn`:** parked — `Item.turnOn`/`turnOff` set `turnedOn`;
  `LightState` apply already calls turnOn/Off. No dedicated msg. Evidence in
  `COOP_COVERAGE.md`. Protocol **25** unchanged.
- **`EventTrigger.fired` / `firedExit` late-join bulk:** parked — no
  dedicated trigger bulk (msg **138** is `ScenarioStateBulk`). Decompile
  `EventTrigger.fire` latches
  `fired` then only calls `gameEvents.fire()` (plus optional item remove);
  `fireExit` only calls `gameEventsExit.fire()`. World correctness is the
  linked `GameEvents` latch. Late-join already sends `GameEventsBulk` (136)
  for `fired && !multipleFire`; client one-shots are also blocked by
  `GameEventsFiredPatch` (area enter is intentionally not suppressed —
  `EventTriggersProxyPatches`). Joiner may re-enter `EventTrigger.fire` with
  local `fired=false`, but GE side effects do not re-run. Protocol **25**
  unchanged. Evidence in `COOP_COVERAGE.md`.
- **`timesCraftedLimit` / `Player.craftedItems`:** parked — per-player by
  design (`Player.SaveState`, local `Player.Instance` gate, personal UI msg).
  Shared craft world state remains `workbenchLevel` (already synced). No craft-
  count msg; protocol **25** unchanged. Evidence in `COOP_COVERAGE.md`.
### Changed (this milestone)

- **CharacterSpawnPoint host authority:** Harmony Prefix on
  `CharacterSpawnPoint.actuallySpawn` and `waitToSpawnCharacter` — clients
  skip; Offline/Host keep vanilla. Covers `Location.spawnCharacters` and
  WorldGenerator BigBiome direct `actuallySpawn`. Observation via
  `EntityStateBroadcast` — no new message. Protocol **25** unchanged.
- **InventoryRandom host authority:** Harmony Prefix on `InventoryRandom.randomize`
  and `spawnItems` — clients skip (and set `spawnedItems`); Offline/Host keep
  vanilla. Covers Awake/`init` rolls, Location difficulty `spawnItems` path, and
  NPC trader new-day `randomize(force)`. Independent peer RNG was diverging chest
  / corpse / trader contents (same family as UniqueItemSpawner). When peers are
  already connected after host `spawnItems`, host Broadcasts existing
  `ContainerStateSync` (76) snapshot (incl. empty NPC refresh); otherwise late
  open uses `ContainerStateRequest` / `ContainerStateSync` via
  `ContainerSearchedPatch`. No new message. Protocol **25** unchanged.
- **RandomSpawnArea host authority:** Harmony Prefix on
  `RandomSpawnArea.spawnPrefab` — clients skip; Offline/Host keep vanilla
  interval AddPrefab near `Player.Instance`. Observation via entity /
  WorldSaveShare — no new message. Protocol **25** unchanged.
- **WormsSpawner / Location.spawnWorm host authority:** clients Prefix-skip
  `Location.spawnWorm` and `WormsSpawner.spawn` (night mushroom AddPrefab).
  No C# callers beyond `Location.spawnWorm` in the decompile (likely
  animation/UnityEvent); still host-gated so any invoke cannot diverge.
  No new message. Protocol **25**.
- **Night scenario late-join (`ScenarioStateBulk` 138):** host light-phase
  bulk now snapshots `NightScenario` name + per-index non-firing latches from
  decompile `CustomEvent.started`, `RandomEvent.startedToday` /
  `RandomEvent.disabled`, plus `currentEvent` index and `timeStarted`
  (day/time). Client apply sets those fields only — never calls
  `CustomEvent.fire` / `RandomEvent.fire` / `checkFrequencies` (spawns stay on
  host entity snapshots; one-shot GEs stay on `GameEventsBulk` 136). Live
  `ScenarioSync` (39) / `ScenarioEventFired` (40) unchanged. Protocol **25**.
- **ObjectSpawner host authority:** Harmony Prefix on private
  `ObjectSpawner.spawnObject` — clients skip; Offline/Host keep vanilla
  interval / randomOffset / loop AddPrefab|AddPooledPrefab. Independent peer
  RNG was diverging placements. No new message (entity / WorldSaveShare
  observation, same family as RandomObjectSpawner). **VineSpawner** host-only
  Start parked: clients must run Start so `GameEvents.targetGameObjects` are
  wired for `GameEventsFired` / Bulk apply (`best.fire()` uses local list
  refs). **ActionWhenTurnedOn** parked as covered by `Item.turnOn`/`turnOff`
  via existing `LightState` apply. Protocol **25** unchanged.
- **PorterSpawner / InSightOfPlayer FOV (partial):** clients Prefix-skip
  `PorterSpawner.Start` and `waitToSpawn` so only the host places the Porter
  NPC (entity snapshots; no new message). Host `InSightOfPlayer.checkSight`
  already OR'd session avatars via `HostPlayerIdentity.AnyInSight`; that helper
  now reuses vanilla `Player.isInSight` for the local player and for each
  `RemotePlayerProxy` by briefly pointing `Player._transform` at the proxy
  (same `currentFOV` / `FOVDot` / `Core.canSee` path — no magic 55° fallback).
  General EventTrigger FOV parity stays deferred. Protocol **25** unchanged.
- **World-object Burn sync (Door/Window/Item):** new Forwardable `WorldBurnState`
  (msg **137**) for Flame/molotov ignition on barricades and destructible items.
  Harmony on `Burn.Start` / `Burn.stop` (Prefix before Destroy); skips `CharBase` /
  `Player` / `ProxyItem` so Character/Player paths stay on `EntityBurning` (41) /
  `PlayerBurning` (44). Host broadcasts; client→host→peers. Apply finds Door /
  Window / Item by pos, `AddComponent<Burn>` (optional remaining time) or
  `Burn.stop()` under `NetworkApplyGuard`. Late-join `SendWorldBurnStatesTo` scans
  live world Burns. Protocol **25** unchanged (new ID within same DLL).
- **BirdArea co-op presence:** vanilla `OnTriggerEnter`/`Exit` require
  `GetComponent<Player>()` — remote proxies strip `Player`, so the host never
  saw clients walk into bird volumes. Clients Prefix-skip `BirdArea.Start`
  (and local trigger side effects) so only the host spawns/simulates AreaBirds
  (entity state broadcast; no new message). Host MP replaces enter/exit with
  presence refcount for `Player` + `RemotePlayerProxy`; when the enterer is a
  proxy, `sendBirdToAttackPlayer` uses `attackCharacter(proxy)` instead of
  `attackPlayer()` → `Player.Instance`. Offline unchanged. Protocol **25**
  unchanged.
- **RandomObjectSpawner host authority:** Harmony Prefix on
  `RandomObjectSpawner.spawnObject` — clients skip; Offline/Host keep vanilla
  probability + prefab roll / `tryToSpawn`. Independent peer RNG was diverging
  world loot and NPCs. No new message: Characters ride `EntityStateBroadcast` +
  client pending match / local spawn; Items/saveables from Awake/worldgen ride
  WorldSaveShare / campaign load (`Core.addToSaveable`). Pre-handshake Offline
  may still spawn on both machines before Role is Client — share reconciles.
  Protocol **25** unchanged.
- **UniqueItemSpawner TeddyBear host authority:** Harmony Prefix on
  `UniqueItemSpawner.spawn` — clients skip; only Offline/Host roll the random
  container and `createItem("TeddyBear")`. Independent peer RNG was placing the
  bear in different chests (0 mod hits). After host spawn, if peers are already
  connected, host fans existing Forwardable `ContainerItem` PlaceItem (msg **11**)
  for that slot — no new message. Spawn before handshake still runs on the
  future host; late peers observe via `ContainerStateRequest` /
  `ContainerStateSync` on open (`ContainerSearchedPatch`). Protocol **25**
  unchanged.
- **ChainParent attach + Vine latch:** `ChainState` (134) now also posts from
  `ChainParent.attach` and Vine Update false→true latch (`chainParent.attached = true`
  without calling attach). Apply mirrors detach: remote `attached=true` with local
  `!attached` calls `attach()` under `NetworkApplyGuard`. Host and client both
  Broadcast (Forwardable). Protocol **25** unchanged.
- **Infection late-join bulk:** host heavy phase 9 (with gas) scans
  `Infection` components and pushes living splats via existing `EntitySpawn` (86)
  — same apply path as live host spread sync; no new message id. Cap 256; skips
  `disappearing`. Protocol **25** unchanged.
- **Late-join fired GameEvents bulk:** new host→peer `GameEventsBulk` (msg **136**)
  for already-latched one-shots (`fired && !multipleFire` on host components).
  Heavy late-join phase 11 scans `FindObjectsOfType<GameEvents>` (includeInactive),
  skips `multipleFire`, `isSavedDelayedEvent`, ephemeral dream FX (`def_glow` /
  `def_shadow`), and `dream_*` when no dream is active. Joiner applies each entry
  via the live `GameEventsFired` resolve/fire path under `NetworkApplyGuard`
  (pending queue when the GO is not loaded yet). Host does not re-fire. Night
  **scenario** bulk remains deferred (unique spawn replay risk). Protocol **25**
  unchanged (new ID within same DLL). Cap 2048.
- **ShadowArmor mid-fight health sync:** new Forwardable `ShadowArmorState` (msg **135**)
  for absolute `health` / `maxHealth` / `destroyed` keyed by rounded world pos. Harmony
  postfix on `ShadowArmor.damageMe` and `die` (skip under `NetworkApplyGuard`); host
  broadcasts, client→host→peers. Apply finds armor via destructible `Item` at pos or
  nearest `ShadowArmor`, sets `health`/`destHealth`, or `die(instant)` when destroyed.
  Character-owned armor still syncs by pos (HP bar only; entity combat authority
  untouched). Late-join `SendShadowArmorStatesTo` pushes damaged armor only. Closes the
  gap where `MeleeWorldHit` / light only showed peers the destroy event. Protocol **25**
  unchanged (new ID within same DLL).
- **ChainParent co-op sync:** new Forwardable `ChainState` (msg **134**) for absolute
  `health` + `attached` keyed by rounded world pos (optional `maxHealth` trailer for
  late-join). Harmony postfix on `getHit` / `attach` / Vine latch / `detach` (timer,
  health-zero, onDie); host broadcasts, client→host→peers. Apply finds nearest
  `ChainParent`, sets health, calls `detach` or `attach` under `NetworkApplyGuard`
  when remote attached differs. Late-join `SendChainStatesTo` pushes only damaged or
  detached chains. Protocol **25** unchanged (new ID within same DLL).
- **Late-join map discoveries:** `SendMapStateSyncTo` now scans
  `MapElement` (`FindObjectsOfType`, includeInactive) for `isOnMap` + non-empty
  `elementName` (decompile: `MapElement.isOnMap`, `Map.showElement`), caps at
  4096, and fills `MapStateSync` DiscoveryCount / DiscoveryElementNames.
  `HandleMapStateSync` already applies via `OnRemoteElementDiscovered`. Live
  discoveries remain msg 69; protocol 25 unchanged.
- **FX / proxy third split (remaining ~500+ NetHandlers):** `PlayerLightFxNetHandlers`
  (~614) → `PlayerLightFxApplyNetHandlers` (RX `PlayerLightState` + pending) +
  `PlayerLightFxAmbientNetHandlers` (emitter / remote-lantern static helpers); thin
  façade kept. `CombatFxNetHandlers` (~548) → `CombatFxImpactNetHandlers`
  (throwable / explosion / melee) + `CombatFxGasBurnNetHandlers` (gas / burn /
  late-join gas); thin façade kept (dropped dead duplicate `HandlePlayerAudio`
  already owned by `WorldFxNetHandlers`). `WorldProxyNetHandlers` (~541) →
  `WorldProxyLifecycleNetHandlers` (spawn / teleport / destroy / dream resync /
  aggro) + `WorldProxyEffectNetHandlers` (footstep / effect-sync / sound / scare);
  thin façade kept. **Skipped (no clean cluster):** `PlayerHeldLightApplyNetHandlers`
  (~528 — one continuous flare/match apply path; flashlight alone too thin),
  `PlayerInteractNetHandlers` (~513 — single drag domain; body-push is drag-adjacent),
  `LocationEnterExitNetHandlers` (~508 — enter/exit/settle share proxy placement +
  resolve helpers). Awake wires siblings; no Ensure*; protocol 25 unchanged.
- **Combat / location / dialog second split:** `CombatNetHandlers` (~749) →
  `CombatDeathBagNetHandlers` (bag maps + spawn/loot/late-join) +
  `CombatAttackNetHandlers` (attack/damage/FF + sanitize) +
  `CombatDeathStateNetHandlers` (PlayerDied / night morning / final dreamscene);
  thin façade kept. `LocationNetHandlers` (~635) → `LocationEnterExitNetHandlers`
  (enter/exit/settle/proxy) + `LocationEntityTrapNetHandlers` (entity spawn + trap);
  thin façade kept. `DialogOutcomeNetHandlers` (~637) →
  `DialogOutcomeApplyNetHandlers` (outcome apply/drain/tree) +
  `DialogOutcomeCloseNetHandlers` (onCloseDialogue / leave-door GEs / NPC resolve);
  thin façade kept (`StripCloneSuffix` forwards). Awake wires siblings; no Ensure*;
  protocol 25 unchanged.
- **Held-light + container second split:** `PlayerHeldLightNetHandlers` (~886) →
  `PlayerHeldLightPackNetHandlers` (TX `PackContinuousLights` / local held helpers) +
  `PlayerHeldLightApplyNetHandlers` (RX apply/spawn/destroy); thin façade kept.
  `ContainerNetHandlers` (~690) → `ContainerLootNetHandlers` (take/put/deny/refund) +
  `ContainerDeathDropNetHandlers` (corpse/open state request) +
  `ContainerPendingNetHandlers` (pending remove/pre-count + state-sync apply); thin
  façade kept. Awake wires siblings; no Ensure*; protocol 25 unchanged.
- **Player held-light compose:** `PlayerStateNetHandlers` (~711) → pose/movement
  (~199) + new `PlayerHeldLightNetHandlers` (remote flare/match/flashlight apply +
  local `PackContinuousLights` / held-light helpers). Awake wires both; façade
  forwards destroy/pack/`IsMatchLightItem`. No Ensure*; protocol 25 unchanged.
- **LNM further slim (~1926 → ~1332):** `PackContinuousLights` + send cache →
  `PlayerHeldLightNetHandlers`; `ProxyAggroCheck` → `WorldProxyNetHandlers`;
  `SyncExistingDeathBags` → `CombatNetHandlers`. Transport Start/Stop/Update peer
  poll stays on `LanNetworkManager`.
- **Megabase handler split (relocation only):** `PlayerNetHandlers` (~1300) →
  `PlayerStateNetHandlers` / `PlayerPresenceNetHandlers` / `PlayerInteractNetHandlers`;
  `FxNetHandlers` (~1204) → `WorldFxNetHandlers` / `PlayerLightFxNetHandlers` /
  `CombatFxNetHandlers`; `WorldStateNetHandlers` (~930) → `WorldPhysicsNetHandlers` /
  `WorldWeatherTimeNetHandlers` / `WorldLateJoinNetHandlers` / `WorldProxyNetHandlers`;
  `DialogNetHandlers` (~699) → `DialogOutcomeNetHandlers` / `DialogNpcLockNetHandlers`
  (tree folded into outcome; no dialog bulk); `WorldSendNetHandlers` (~679) →
  `WorldObjectSendNetHandlers` / `WorldSendNetHandlers` (despawns folded into object sends).
  Awake wires each service; façades updated; wire/patches unchanged.
- Product line bumped **0.7.81 → 0.8.0** (`PluginInfo`, assembly, csproj, docs,
  PathB product invariant tests).
- Scaffolded `Bootstrap/`, `Core/`, and `Domains/{Combat,Dream,Dialogue,World,Inventory,Doors,Players,Map,Night}/`.
- Moved entry/policy leaf files into `Bootstrap/` and `Core/`; relocated Combat and
  Dream `LanNetworkManager` partials under `Domains/` (still partials — behavior unchanged).
- **Sync/ + Patches/ → Domains/** (folder moves only; C# namespaces and Harmony
  attributes unchanged; protocol 25 unchanged):
  - Dream sync → `Domains/Dream/` (`DreamSyncManager`, `DreamSession`,
    `DreamAudioPlayer`, `DreamForestSpiritAggro`, `FinalDreamsceneManager`)
  - Doors/stations → `Domains/Doors/` (`DoorSyncPatches`, `SawSyncPatches`,
    `StationSyncPatches`); workbench lock → `Domains/Inventory/`
  - Dialogue sync → `Domains/Dialogue/` (tree sync/codec, host/client guards,
    `NpcDialogueLock`)
  - Players/Map → `Domains/Players/` (`CharacterTracker`, `EntityTrackers`,
    `FreezeTracker`, `PeerItemPresence`) and `Domains/Map/` (`MultiplayerMapManager`)
  - `WorldPhysicsSyncService` → `Domains/World/`
  - Flat `Patches/` grouped into `Domains/*/Patches/`; cross-cutting leftovers
    remain under `Patches/` (save/path/pause/world-share)
  - `ProductInvariantTests` Horde authority paths updated to
    `Domains/Combat/Patches/…`
- **`LanNetworkManager.Handlers.cs` (~10.5k) split** into domain/session
  partials (behavior unchanged; protocol 25 unchanged):
  - `Networking/Session/` — handshake/world-share, save/backup, bulk sync
  - `Domains/Players/` — player state/lights/drag + anim/FX handlers
  - `Domains/Doors/` — locks, DoorOpen, barricades
  - `Domains/Inventory/` — containers, trade, journal/workbench
  - `Domains/World/` — GameEvents, locations, physics/FX/sends
  - `Domains/Night/` — shadows/scenario/sleep + flags
  - `Domains/Dialogue/` — dialog outcome/tree/NPC lock
  - `Domains/Map/` — markers/discoveries
  - Original god-file removed (no empty stub).
- Removed empty `Sync/` folder; leftover helpers live under `Domains/World/`.
- Merged flat `Players/` into `Domains/Players/Runtime/` (namespaces unchanged).
- **`WorldPhysicsSyncService` (~4.5k) split** into partials under `Domains/World/`:
  Snapshot, Apply, Interpolation, Lights, Thrown, CombatFX, Types (+ thin core fields).
- **Networking layout:** services → `Networking/Services/`; Steam partial →
  `Networking/Steam/`; inbound switch → `Networking/Dispatch/LanNetworkManager.Dispatch.cs`.
  `LanNetworkManager.cs` slimmed (~2.4k → ~1.8k LOC).
- **`DreamSyncManager` (~2k) split** into partials: Session, RemoteEntry,
  EndFreeze, Cleanup, SceneLoad, Transition (+ thin core fields).
- Separated `LanNetworkManager` handler partials out of Harmony patch files
  (Examinable, Chapter, Cutscene, CursorAction, Epilogue) so Patches/ no
  longer embeds networking handlers.
- **Stripped dual `Door.open` Harmony:** removed `Sync.DoorOpenPatch`; sole
  fan-out is `DoorOpenSyncPatch` (DoorOpen + DoorState once, real OpenForce).
- **Combat composed:** `CombatNetHandlers` owns death-bags + combat message
  handlers; `LanNetworkManager.Combat.cs` is a thin façade (0.8 composition pattern).
- **Dream composed:** `DreamNetHandlers` + thin DreamHandlers façade.
- **Containers composed:** `ContainerNetHandlers` + thin ContainerHandlers façade.
- **Dialog composed:** `DialogNetHandlers` + thin DialogHandlers façade.
- **Map / Examinable / Trade composed:** `MapNetHandlers`, `ExaminableNetHandlers`,
  `TradeNetHandlers` + thin façades (`SendBulkOrAll` now internal for trade bulk).
- **Doors composed:** `DoorNetHandlers`, `LockNetHandlers`, `BarricadeNetHandlers`,
  `CursorActionNetHandlers` + thin façades; `_remoteOutsideLocation` kept on manager.
- **Night composed:** `NightNetHandlers`, `FlagNetHandlers` + thin façades
  (shadows/scenario/sleep/time only).
- **Players composed:** `PlayerStateNetHandlers`, `PlayerPresenceNetHandlers`,
  `PlayerInteractNetHandlers`, `PlayerFXNetHandlers` + thin façades
  (trap/drag public APIs preserved; former megabase `PlayerNetHandlers` split by theme).
- **Journal composed:** `JournalNetHandlers` + thin façade.
- **World composed:** `ChapterNetHandlers`, `CutsceneNetHandlers`, `GameEventNetHandlers`,
  `LocationNetHandlers`, `WorldFxNetHandlers`, `PlayerLightFxNetHandlers`,
  `CombatFxNetHandlers`, `WorldPhysicsNetHandlers`, `WorldWeatherTimeNetHandlers`,
  `WorldLateJoinNetHandlers`, `WorldProxyNetHandlers`, `WorldSendNetHandlers`
  + thin façades (public Send*/proxy APIs preserved; former megabase `FxNetHandlers`
  / `WorldStateNetHandlers` split by theme); tick/shadow fields →
  `LanNetworkManager.WorldTickFields`; door/trap sends on `WorldSendNetHandlers`.
- **Strip pass (spatial / workbench / epilogue / domain hygiene):**
  - Junk-drawer `JournalWorkbenchHandlers` removed; spatial finds → `WorldQueryHelper`
    (DeathDrop fallback included); callers use helper directly;
    `InventoryHelpers` keeps only `SyncItemAmount`.
  - `FindDestructibleItemXz` moved into `WorldQueryHelper`; FX façade wrapper dropped.
  - Disabled workbench exclusive-lock gutted: deleted no-op Harmony patches;
    `WorkbenchOpenLock` stub keeps `Reset` + `HostReleaseAllForPlayer`; wire msg 119 +
    ignore handler retained.
  - Epilogue SceneLoad composed: `EpilogueNetHandlers` + thin EpilogueHandlers façade.
  - Dual `Door.open` Harmony already stripped earlier this line (`DoorOpenSyncPatch` only).
  - Late-join world light/generator sync moved Lock → `WorldLateJoinNetHandlers`
    (was briefly on `WorldStateNetHandlers` before the WorldState theme split).
    (`SyncExistingWorldLightsTo` / `SyncExistingGeneratorsTo` / `ResyncWorldLightsForPeer`).
  - Façade `Ensure*Handlers` spam removed (~24 methods / ~225 call sites): Awake
    already constructs every `*NetHandlers` service.
  - Saw/feeder/lure station sync moved Night → `Domains/Doors/StationNetHandlers`
    (+ thin `StationHandlers` façade) next to `SawSyncPatches` / `StationSyncPatches`.
  - **Session Save / BulkSync composed:** `SaveNetHandlers` + `BulkSyncNetHandlers`
    (thin Session façades); flag bulk consolidated onto `FlagNetHandlers`.
    Handshake/`SessionHandlers` and `ResetSessionNetworkState` stay on the manager;
    protocol 25 unchanged.
- **Beauty / state ownership:** clear domain-owned pending queues and maps moved
  off the `LanNetworkManager` state bag onto owning `*NetHandlers` services
  (station saw/feeder/lure, flags, barricades, constructibles, trade inventories,
  container remove/take pre-counts, journal bulk, night scenario, game-events,
  drag claims/remote-drag maps, pending player lights, melee-hit debounce).
  LNM keeps thin forwards only where call sites cannot change cheaply (drag maps,
  container record helpers, pending lights). Peer maps / Steam / migration /
  handshake / late-join bulk stay on LNM (session transport).
  `ResetSessionNetworkState` clears via handler `Clear*` methods.
- **Diff optics:** vs HEAD the rewrite is roughly LOC-neutral (~+21k / −18.5k
  with all files staged). Cursor’s earlier ~+52k view was untracked new files
  (NetHandlers + physics/dream partials) counted as pure adds without pairing
  the deleted god-files (`Handlers.cs` ~10.5k, `WorldPhysicsSyncService` ~4.5k,
  `DreamSyncManager` ~2k).
- **`Networking/` folder layout** (folder moves + one dispatch extract; namespaces
  unchanged; protocol 25 unchanged):
  - Service-ish leaves → `Networking/Services/` (`EntityStateBroadcastService`,
    `ClientEntityInterpolationService`, `WorldSaveShareService`, `WorldSyncService`,
    `HostMigration`, `ClientStateBackup`, `NetworkApplyGuard`, `NetworkResetRegistry`)
  - Steam stays under `Networking/Steam/` (`SteamCoopTransport`, `SteamRelay`);
    `LanNetworkManager.Steam.cs` moved beside them
  - `ConnectionBackend` → `Networking/Transport/`
  - Inbound `ProcessInboundMessage` switch →
    `Networking/Dispatch/LanNetworkManager.Dispatch.cs` (partial; behavior identical)
  - `ProductInvariantTests` paths updated for Services + Dispatch PutRaw check

### Parked / deferred

- Full dual-box soak of every domain after the rewrite (required before calling
  0.8 “playtest-green”). Dual-deploy of the Release DLL to Steam + SecondDarkwood
  plugins is done; in-game soak is not.
- **ObjectStages:** no `StageState` msg. Decompile shows stages only fire
  `GameEvents` (`setStage` → `gameEvents[i].fire()`); covered by live
  `GameEventsFired` (65) + late-join `GameEventsBulk` (136). See
  `docs/COOP_COVERAGE.md`.
- **Session** Save / Bulk / Session handlers stay as organized `LanNetworkManager`
  partials under `Networking/Session/` — handshake, peer maps, Steam rebind, and
  late-join orchestration are the transport seam; a full `*NetHandlers` extract
  failed and is not retried. Optional later peel of Save/BulkSync only.
- Further slim of `LanNetworkManager.cs` core (~1.8k) and optional Save/Bulk
  composition for consistency.
- Optional protocol bump only if a later 0.8.x forces wire changes.

---

## 0.7.81 (host notes): Linux dual-box playtest paths

Machine move Windows → Linux. No protocol or gameplay wire change.

### Changed (dev / deploy)

- `DeployToGameDirs` honors optional `SecondPlugins` from `GamePath.local.props` (Windows path remains the default fallback).
- `YokWare.EntitySpawner` now deploys to Steam + SecondDarkwood plugin dirs after build.
- Added `.gitattributes` (`eol=lf`) and launch helpers: `scripts/run-darkwood-host.sh`, `scripts/run-seconddarkwood.sh` (Proton Experimental as Wine runtime when system `wine` is unavailable).

### Verification

- Steam native + BepInEx **5.4.23.5** linux-x64: YokWare 0.7.81 + EntitySpawner + ItemSpawner load.
- SecondDarkwood under Proton: same mod DLL, Doorstop via `winhttp=n,b`, save root → `Darkwood_Second`.

---

## 0.7.81: Ordered snapshots and authority hardening

This release closes several state-ordering, authority, and lookup gaps found
during the stabilization pass.

### Changed

- `PlayerState`, `EntityState`, and `PhysicsState` now carry per-sender
  sequence numbers. Duplicate and older unreliable snapshots are ignored.
  Reliable physics events use a separate sequence stream.
- Host combat derives the attacker from the receiving peer and uses the host
  proxy for attack position and range checks. Invalid positions, damage,
  target types, player IDs, and unknown victims are rejected.
- Client AI suppression applies to `Sniffer` and `AIPath` components without
  requiring a `Character` lookup.
- Active dream objects are resolved under `Dreams.dreamLocation`. Cleanup no
  longer uses global name lookup that could select an overworld twin.
- Primitive and byte-array packet reads now reject truncated or unreasonable
  payloads instead of accepting unsafe lengths.

### Verification and deployment

- The Path B tests cover sequence ordering and wraparound, combat validation,
  component-level AI guards, dream lookup policy, and malformed packets.
- Protocol **25** is required on both peers because the three snapshot payloads
  changed.
- This documentation pass does not rebuild or deploy the game mod. Existing
  build output from the stabilization pass is not treated as a fresh build.
- Dual-box combat, dream, late-join, and packet-reordering runtime checks are
  still pending. Static and unit tests do not verify Unity lifecycle behavior.

### Deferred

- Host migration during a dream.
- Speculative gameplay rewrites.
- Broad linker and output-size optimization.

## 0.7.80: Split-map host simulation

Host and clients can occupy different locations without the host dropping the
other player's world grid, entities, or location.

- Remote player bubbles remain entered after vanilla location transport.
- Proxy culling checks every relevant grid, not only the host's current grid.
- Object registration uses the grid containing the position.
- A location remains active while a remote player is inside it.
- Return-to-world no longer snaps a proxy that remains in a location.
- Night-death counting includes handshaked peers whose proxies have not spawned.
- LAN and Steam use the same dream-join rejection rule.

Protocol 24 remained unchanged. Runtime coverage for dense three-player
split-map sessions and late-join story state remains pending.

## 0.7.79: Dialogue world authority

Dialogue choices now have one world-state writer.

- Clients defer flags, world events, transport, shared reputation, and map
  markers while applying a dialogue board. The host applies those outcomes.
- Linear and source boards are replayed on the host. Destination boards remain
  available for keyhole and portrait chains.
- Journal entries and journal item identity are shared. Physical item rewards
  remain personal to the speaking player.
- Shared NPC reputation is applied once on the host. NightTrader and TheThree
  reputation remains per-player.
- Dialogue tree state is flushed by the host after the authoritative apply.
- Remote location requirements can use a proxy, and bag item requirements use
  `PeerHasItem` message 133.

Deferred: spectator dialogue UI, welcome and gossip randomness, late-join
GameEvent bulk, proxy field-of-view parity, and GameEvent code paths that
bypass `NPC.set_reputation`. Runtime dialogue verification is pending.

## 0.7.78: Entity host-player identity

Host AI, sight, culling, and special attacks now account for remote players.
Forest spirit, banshee, shooter, hit-and-run, and nearest-player paths no
longer assume that `Player.Instance` is the only player. Client AI remains
disabled; clients present host snapshots.

Protocol 23 remained unchanged. Runtime coverage is pending.

## 0.7.77: Repository cleanup

Removed the unused Path A archive, research-only trees, scratch files, and
superseded audit documents from the ship repository. The Path B mod, tests,
entity spawner, scripts, tools, and current docs remain.

No mod behavior or protocol changed.

## 0.7.76: Dialogue overlay and dream-door audio

- World-only dialogue now hides the full dialogue background and clears stale
  presentation state.
- Leave-door GameEvents own the dream door sound. A second `DoorOpen` path no
  longer plays the sound twice.

## 0.7.75: Dream dialogue presentation and forest spirit

- Delayed portrait changes no longer restore another player's dialogue
  presentation after a world-only dialogue drain.
- Dream leave-door handling records the GameEvent as the owner of the open
  operation and suppresses duplicate force-open audio.
- Forest spirit attacks retain their assigned player instead of retargeting
  to a different peer.

## 0.7.74: Beartrap removal hitch

Trap removal is debounced before scene searches, captures names before
destruction, and avoids unnecessary item scans. Generator turn-on performance
was not changed.

## 0.7.73: Join work and share-pack scheduling

- Heavy late-join searches run one type per frame.
- World-share file reads and compression run away from the main thread.
- Player animation libraries are sent only when the library changes.

The first join can still cost time for world loading and vanilla save work.
No claim is made that all join hitches are fixed.

## 0.7.72: Host push audio and trap destruction

- Physics snapshots no longer suppress host-observed scrape when a client is
  pushing an object.
- Beartrap disarm and destruction now reach the other peers through the
  destruction path.

## 0.7.71: Scrape and trap path diagnostics

Scrape ownership now follows drag and contact state rather than proximity.
Network stop only stops the mod-owned moving-object sound. Trap disarm sends
its removal state from the actual disarm and progress completion paths.

## 0.7.70: Menu hint, scrape, and beartrap disarm

Removed the title-screen hosting hint. Client-owned scrape no longer competes
with the host echo. Silent beartrap disarm applies the triggered and inactive
state without relying on a name filter.

## 0.7.69: Dream party-once rules

Completed named dreams are rejected for the current party, skill-dream flags
are enforced before the vanilla check, and completed random presets are kept
out of the refill pool.

## 0.7.68: Dream re-entry and abort cleanup

Dream re-entry loads completed named dreams through the normal remote path.
Failed pad loads, disconnects, rejected requests, and join attempts during
entry now clear the session and freeze state. Dream door fan-out is held until
the active pad exists.

Deferred: a fallback for a lost dream-chain packet, portrait edge cases on
remote load, and fresh dual-box re-testing.

## 0.7.67: Steam host grant and migration

Steam peer rosters now carry Steam IDs. Graceful and crashed host departure
use the same lowest-player-ID election model as LAN, with SNS reconnect to the
elected host.

## 0.7.66: Restore-self safety

Restore Self is available only in a chapter, only for a client, and only when a
usable campaign-scoped backup exists. It now reports the backup result in the
settings UI.

## 0.7.51 through 0.7.65: Multiplayer menu presentation

The title multiplayer entry and its panel were aligned with the native menu:
labels, hover states, hitboxes, button sizing, fallback text, and embedded
wordmark assets were corrected. The panel now keeps connection progress on the
active join row and places recovery actions under settings.

These releases changed presentation and menu flow only. Protocol 23 remained
unchanged.

## 0.7.50: Wardrobe loot and client scrape

Empty furniture inventories are no longer mistaken for dropped pickups.
Drag-owned objects are excluded from the physics snapshot path so native
scrape audio does not compete with DragSync.

## 0.7.49: Fleeing entities and roaming presentation

Host `Character.removeMe` now sends entity despawn state for fleeing wildlife.
Client presentation restarts stopped locomotion clips and does not revive
corpses while waking entities.

## 0.7.48: Refactor and trust hardening

- Research-only projects were moved out of the ship solution.
- Remote forwards are checked against the actual sender.
- Container placement, trade, damage, friendly-fire, and save-share paths
  reject invalid or unauthorized requests.
- Repeated patch and tracker code was consolidated, and dead code was removed.
- LiteNetLib is supplied through NuGet version 1.3.5.
- CI paths and public build documentation were corrected.

The product line was normalized to 0.7.x. Runtime soak remained required.

## 0.7.47: Steam SNS join reliability

Steam joins received a longer timeout, early invite callback registration,
more tolerant lobby-member timing, and a relay warm-up retry.

## 0.7.46: Death recovery and object lookup

Day-death recovery now leaves the stale location grid and refreshes the World
grid. Object destruction and light application gained inactive-object and
position-aware lookup paths. Dead combat targets are resolved for cleanup and
then rejected for damage.

## 0.7.45: Location transport and trap state

Client location entry uses a host transport request instead of moving the host.
Audio interest matches the entity interest range. Barricade and furniture
matching uses XZ distance. Trap disarm clears the local trap state safely.

## 0.7.40 through 0.7.44: Entity and audio stabilization

This group aligned entity interest, proxy culling, flee behavior, corpse
handling, client claims, and multi-listener audio. It also added pending
matching and grace periods for save-point entities and reduced duplicate
scrape paths. Remaining runtime checks were documented rather than treated as
closed by code inspection.

## 0.7.28 through 0.7.39: Steam, voice, dreams, and diagnostics

The project added SteamNetworkingSockets transport, optional Steam voice and a
craftable walkie item, then hardened dream entry and exit, campaign-scoped
client backups, dialogue authority, host clock authority, and world-share
failure handling. Performance probes were added for network polling, entity
application, pending queues, and scene searches.

## Earlier 0.7.x history

Earlier 0.7.x releases established the Path B host-authoritative foundation:
player and entity snapshots, world physics, locations, weather, map markers,
containers, dropped items, death bags, trade, reputation, construction,
combat, night death, dreams, chapter transitions, audio, spectator mode, and
world save sharing. The protocol increased as wire contracts were added; the
current value is 25.

### Ongoing verification

- Full dual-box and three-player campaign coverage is still a playtest task.
- Late-join one-shot and scenario bulk remain deferred.
- Wrong-save warning UI, full interaction-lock coverage, and some dream and
  spectator presentation edge cases remain deferred.
- Runtime behavior must be checked in both directions: host action observed by
  client, client action observed by host, late join, and players in different
  locations.
