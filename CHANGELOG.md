# Changelog

## Versioning

The current product line is `0.8.x`. The plugin and display version are
**0.8.7**. The current Horde wire protocol is **25** (unchanged from 0.7.81;
this line is an architecture rewrite, not a wire bump).

This file is a public ship log. Code-only status and runtime status are called
out separately. A runtime item is not considered verified until it has been
tested in the game.

---

## 0.8.7 — Traps and hits that the name list missed

- **Bear, chain, and mutated traps** spring and get picked up for the other player even when the object name does not contain "trap". The old check was a word list copied in several places.
- **A swing** no longer lands on whatever enemy happens to hold that id when the name does not match. A stale id only counts if the same-named enemy is within a few steps. Unsynced hits still use the wider search.
- **Broken glass** still springs for the other player. Ordinary names that merely contain "glass" do not. The client only asks the host to spring a real trap, and being stuck follows the bear-trap flag.
- **Fleeing enemies** hide when they actually run away (`escaping`, flee, or want to despawn). Investigating a noise or a bird that is still flying stays visible.
- Protocol **25** unchanged. Product bump **0.8.6 → 0.8.7**.

---

## 0.8.6 — Client enemies stay on the host's bodies

Clients were deleting save enemies when updates paused, swapping same-name packs, and running the single-player death (triggers, night spawner, trader night-end) on their own machine.

- **Out of range, dream, or a crowded snapshot** no longer deletes the body. It stays put and picks up again when the host sends the next update. Only a real despawn removes it.
- **Same-name enemies** bind only when they are already next to the host position. A one-of-a-kind character is moved onto the host pose instead of being replaced by a blank copy. Extra locals are hidden, not deleted.
- **Death on the client is presentation.** Health percentage is applied. Downed (pre-death) is separate from a finished kill. The corpse uses the host's loot when opened. Attack clips play once.
- **Host despawn removes the body** even if your side already played the death. A downed enemy stays downed when health rounds to 0%. The first fall of a get-up enemy is not a finished kill. Dream and overworld copies of the same name are not swapped. The lying-down collider is applied with the death, and standing back up drops the corpse shell.
- Protocol **25** unchanged. Product bump **0.8.5 → 0.8.6**.

---

## 0.8.5 — MelonLoader peer build (no BepInEx dependency)

MelonLoader is a real peer of BepInEx again: same shared Path B body, no
BepInEx types on the Melon runtime path.

- **Loader-agnostic config/log:** `ModConfigStore` + `ModSetting<T>` (BepInEx-
  compatible INI) and `IModLogger` with BepInEx / Melon wrappers. Melon DLL no
  longer references `BepInEx.dll`.
- **Entries:** BepInEx still uses `BepInEx/config/com.yokware.branch.cfg`; Melon
  writes the same key shape under Melon `UserData/YokWare/`.
- **Repo pipeline:** `scripts/fetch-melonloader-refs.sh` + `scripts/build-loaders.sh`
  build both variants from a fresh clone (Melon refs stay uncommitted).
- **Parked:** in-game Melon smoke on a Melon-installed Darkwood (one Doorstop per
  game dir — cannot co-host with BepInEx on the same install).

Protocol **25** unchanged. Product bump **0.8.4 → 0.8.5**.

---

## 0.8.4 — Dual-box join/dialog log fixes + entity debug logging

- **`VerboseEntitySync`** (Debug, default **true**): enables Entity/Combat/AI/Death
  Trace under Support without full Trace preset; bumps LogMinLevel to Trace for
  those dumps. Set false for quiet play.
- **`EntitySyncLog`** facade + TraceRate on hot paths:
  host snapshot/anim/alive, client snap/match/interp/HP, clip apply + reaction
  clips, death anim, pending/phantom spawn/despawn, PlayerAnim TX/RX,
  EntitySound (incl. GetHit echo skip), Attack/FF/ProxyDmg/DamagePlayer.


Log-grounded stability pass from a dual-box session (protocol **25** unchanged).

- **Missing chunk 0:0:** overlapping host world share (auto-share + WorldRequest
  chain) wiped client `_chunkBuffers` under apply. Client now ignores Begin while
  receiving/applying; host coalesces duplicate pushes to the same peer mid-share.
- **GetProfiles WARN ×1000:** slot picker OnGUI invoked private `GetProfiles`
  (no `profs.dat` Exists check) on empty `Darkwood_Second`. Prefer public
  `loadGameProfiles`, Exists-gate private path, cache for 2s, log InnerException
  once.
- **Oven DialogOutcome NRE:** drain-abort `SilentCloseAfterWorldApply` nulled
  `dw.npc` before `displayDialogue(lookAtPot)`. Re-bind NPC after scrub; preflight
  null guard; richer catch context.
- **Walkie ItemsDatabase:** expected boot race demoted Warn → Event (still retries).
- **Parked:** host `flushPending` / `footType=Item` PerfSeg cliffs after late-join
  settle — needs another Dev soak with segment attribution before changing flush
  budget.

Product bump **0.8.3 → 0.8.4**.

---

## 0.8.3 — Wine dual-box cursor (always free)

- **SecondDarkwood freeze/trap:** Wine/Proton often keeps `Application.isFocused`
  true under XWayland, so 0.8.2’s blur-only release never fired; when it did,
  ClipCursor unlock could hard-freeze the client. Now
  `FreeCursorForDualBox` (default **true**) always rewrites Confined/Locked to
  `None` — no confine, no blur toggle thrash. Protocol **25** unchanged.
- Product bump **0.8.2 → 0.8.3**.

---

## 0.8.2 — Dual-box cursor release

- **Wayland cursor magnet:** vanilla `CursorLockMode.Confined` keeps a Hyprland
  pointer confine alive after Alt+Tab, so the mouse stays trapped in the game
  window even when another window has focus. Mod now releases to `None` on
  blur and restores `Confined` on focus (`CursorConfineFocusGuard` +
  `set_lockState` prefix). Protocol **25** unchanged.
- Product bump **0.8.1 → 0.8.2**.

---

## 0.8.1 — Dual-box diagnosis ship

Ships the playtest diagnosis path that code-only polish still needs: logs +
dual-box gate. Protocol **25** unchanged.

- **`scripts/check-dualbox-perf.sh`:** greps host + client `LogOutput.log` for
  `[Perf]` (must exist) and fails on any `[PerfCliff]`. Defaults to this
  machine’s Steam + SecondDarkwood BepInEx paths.
- **`DarkwoodMP.Mod/docs/PLAYTEST.md`:** dual-box soak + triage checklist
  (Support preset, probe ON, cliff tags, reverse-check sync spot-checks).
- **LOGGING.md:** documents `[PerfCliff]` / `[PerfSeg]` thresholds and points
  at the script; probe ON line now prints cliff thresholds.
- Product bump **0.8.0 → 0.8.1** (`PluginInfo`, assembly, csproj, README,
  COOP_COVERAGE, CONTRIBUTORS).

Runtime FPS proof remains a dual-box soak (script is the gate, not a substitute
for playing).

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

### Changed (beauty pass)

- **Entity pending LateUpdate budget:** CEI pending matches no longer walk
  `O(pending × tracker)` every frame — one `CopyAll` + round-robin tight
  retries (12/frame) and timeout resolves (3/frame); inactive Character scan
  no longer Invalidates the scene TTL every 0.5s. Protocol **25** unchanged.
- **Dream GE overlap + CachedColliders closeout:** `WorldQueryHelper` NonAlloc
  buffers raised (1024/256) so dream `nameR=80` does not silently truncate;
  client PlayerState / death / vault use `RemotePlayerProxy.CachedColliders`;
  trade pending capped at 64; GE soft IndexOf skipped when exact-name bucket
  exists; resolve-by-name cache capped at 256. Protocol **25** unchanged.
- **PlayerState host-forward BroadcastHot:** 3+ peer forward of client
  PlayerState uses recycled hot buffers + `excludePlayerId` (no per-packet
  `BuildPacket`/`CopyData` alloc at ~30 Hz). Protocol **25** unchanged.
- **PhysicsState count-prefix buffers:** send `CopyGrow` + recv recycled
  deser arrays with `ObjectCount`/`DoorCount`/… (wire still count-then-items;
  no per-tick `new T[n]` on oscillating object counts). Protocol **25**
  unchanged.
- **Physics snapshot scratch lists:** sound/kinematic/gate/pos expiry uses
  static key lists (no per-tick `new List` on the host phys path). Protocol
  **25** unchanged.
- **Steam hot-send length + GetAll retire:** SNS send accepts explicit length
  (no tight-buffer copy for recycled packets); remaining
  `CharacterTracker.GetAll` call sites use `CopyAll`. Protocol **25**
  unchanged.
- **Entity broadcast string cache:** 10 Hz snapshot path caches stripped
  name + prefab path by stable id and skips Unity name/GetComponent work when
  numeric+clip fields match last send; trap dead-key purge reuses scratch
  lists. Protocol **25** unchanged.
- **Voice / physics-key / door / anim polish:** voice capture serializes
  capture-buf slices (no per-packet `new byte[]`); free-body motion keys use
  InstanceID ints; Door open/close MethodInfo cached; PlayerAnimation calls
  `PlayTorso`/`PlayLegs` directly; `TraverseHack` split out of Types hub.
  Protocol **25** unchanged.
- **Dream + CEI ownership splits:** `DreamSyncPatches.Lifecycle` (start/end);
  `ClientEntityInterpolationService.Pending` (budgeted LateUpdate match).
  Protocol **25** unchanged.
- **LateUpdate scratch + ship gates:** ItemMovingSound / FlagSync reuse static
  key lists; CEI caches corpse-Item check; physics Apply prefers
  `_objectInterp` CachedRb/Item; deleted `CharacterTracker.GetAll`; PathB
  ProductInvariant locks OverlapSphere / hub&lt;500 / no Domains LNM façades.
  Protocol **25** unchanged.
- **Night spawn + hub headroom:** night redirects drop LINQ/`ToList` for a
  scratch far-proxy buffer + sqrMagnitude; trap/phys debounce and Steam soft
  reconnect reuse key lists; `LocalAudioService.Clips`, `ClientPerfProbe`, and
  panel hitbox helpers peeled under the 500-line hub gate. Protocol **25**
  unchanged.
- **Trap Trigger fast-path + CachedCharBase:** phys trap snapshot reads
  typed `Trigger.triggered` (NonAlloc fallback); remote proxies cache
  CharBase for host AI/combat; melee/AI hit debounce reuse scratch lists;
  CharacterTracker Harmony patches peeled for hub headroom. Protocol **25**
  unchanged.
- **Container + Save hub splits:** `ContainerSyncPatches.Opened` (to-opened /
  controller / activate); `SaveNetHandlers.Apply` (save-sync apply, backup
  restore wait). Protocol **25** unchanged.
- **JournalNetHandlers split:** item/workbench/oxygen vs
  `JournalNetHandlers.Bulk` (bulk sync, flush, vault). Protocol **25**
  unchanged.
- **Tick ownership + Location BroadcastHot:** `LanNetworkManager.Tick.Drag`
  holds drag-end + LateUpdate; LocationEnter/Exit use recycled hot send.
  Protocol **25** unchanged.
- **DragSync BroadcastHot:** drag move/stop serialize uses recycled packet
  buffers (same hot path as PlayerState / PhysicsState). Protocol **25**
  unchanged.
- **PlayerState BroadcastHot + LateUpdate list reuse:** ~30 Hz PlayerState
  uses recycled packet buffers; push-scrape / kinematic release LateUpdate
  uses static key lists (no per-frame `new List`). Protocol **25** unchanged.
- **Physics hot send + PerfCliff:** `BroadcastHot` recycles writer/buffer for
  PhysicsState ticks; `CoopPerfProbe` emits `[PerfCliff]` on ≥100ms frames and
  on 2s windows under ~15 FPS so dual-box soak leaves searchable evidence.
  Protocol **25** unchanged.
- **Entity broadcast buffer reuse:** static `NetWriter` + `CopyDataInto`
  recycled send buffer; `SendRawToReadyPeers(data, length)` avoids per-tick
  `CopyData` alloc on the 10 Hz entity path. Protocol **25** unchanged.
- **ObjectResolve + held-light:** name→last-hit cache before full RB walk;
  full-scan interval 0.5s→2s; held flare/match strip uses one Component walk.
  Protocol **25** unchanged.
- **Dream prop + GE soft-match:** pad Item collider fan-out caches
  `GetComponentsInChildren` (invalidate on dream enter/exit); client miss
  resolves via Item cache not Transform FoT; GE soft-match uses a
  normalized-name index rebuilt with the scene GE cache. Protocol **25**
  unchanged.
- **Medium hitch pass:** physics snapshot copies into recycled arrays (no
  per-tick `ToArray`); door/gen proxy interest uses a reusable position list;
  journal world-destroy uses scene TTL cache (not `Resources.FindObjectsOfTypeAll`);
  night-dead/revive PlayerState uses `RemotePlayerProxy.CachedColliders`;
  dream-end GE clear also drops `_pendingGameEventQueuedAt` keys. Protocol
  **25** unchanged.
- **Hitch/crash hardening (audit follow-up):** null `WorldObjectState.Name`
  no longer NRE-aborts physics apply; entity `_pendingMatches` capped at 96;
  trade inventory flush throttled to 0.5s (no per-frame NPC walks); trap
  pending capped at 64; CEI inactive Character scan via `WorldQueryHelper`
  (0.5s invalidate); phantom replace reuses a scratch HashSet. Protocol
  **25** unchanged.
- **ProxyAggro hot path:** `CharacterTracker.CopyAll` (no `GetAll` alloc);
  hoist night-dead proxy checks out of the per-character loop; XZ sqr
  distance before nearView/sniff gates. Protocol **25** unchanged.
- **`CoopPolicy` split:** Time/Dialog, Npc/Night, Session, Combat/Dream
  files; PathB.Tests links all four. No non-message hub remains ≥500.
  Protocol **25** unchanged.
- **Dream UniqueObject remap pad-scoped:** `RemapDreamUniqueObjects` uses
  `GetComponentsInChildren` under the dream root instead of a world
  `FindObjectsOfType` (load hitch + overworld twin risk). Protocol **25**
  unchanged.
- **Final hub shrink (excl. message DTOs):** Split
  `SecondPlayerAnimController.Apply`, `DreamSyncManager.SceneLoad.Post`,
  `SteamCoopTransport.Migration`, `LanNetworkManager.HostConnect`,
  `HostMigration.Handoff.Promote`, `RemotePlayerProxy.Apply` (behavior
  unchanged). Protocol **25** unchanged.
- **Hot-path alloc audit:** zero remaining `Physics.OverlapSphere(`
  (allocating) call sites in the mod; NonAlloc + scene-scan TTL stay in
  place. Dual-box FPS/sync soak still required for runtime proof
  (`CoopPerfProbe` / dual LogOutput). Protocol **25** unchanged.
- **Held-light / GameEvents / drag ownership splits:**
  `PlayerHeldLightApplyNetHandlers.Flashlight`, `GameEventNetHandlers.Apply`,
  `PlayerInteractNetHandlers.DragSpawn` (behavior unchanged). Protocol **25**
  unchanged.
- **Dream SceneLoad flush path:** after façade retirement,
  `TryFlushPendingGameEventsAfterDreamLoad` goes through
  `GameEventHandlers` (not a removed LNM method). Protocol **25** unchanged.
- **Session / Night / Location ownership splits:**
  `LanNetworkManager.SessionHandlers.LateJoin`, `NightNetHandlers.Scenario`,
  `LocationEnterExitNetHandlers.Exit` (behavior unchanged). Protocol **25**
  unchanged.
- **Dispatch ownership split:** 126-case `ProcessInboundMessage` switch →
  `TryDispatch{Session,Combat,Players,World,DialogueDream}` partials; main
  Dispatch keeps apply-guard + host forward only. Protocol **25** unchanged.
- **Vault uses cached proxy colliders:** `HandleVaultState` reads
  `RemotePlayerProxy.CachedColliders` instead of per-message
  `GetComponentsInChildren`. Protocol **25** unchanged.
- **OverlapSphere NonAlloc pass:** physics/combat/light/trap/drag apply paths
  use shared NonAlloc buffers (`WorldPhysicsSyncService.OverlapNear` /
  `WorldQueryHelper.SharedOverlapBuf`) instead of allocating
  `Physics.OverlapSphere` arrays. Protocol **25** unchanged.
- **Vault Jumpable query:** `HandleVaultState` no longer uses a 500m
  `OverlapSphere` (alloc/FPS cliff). NonAlloc overlap within 16m of the proxy
  — vault windows are local. Door/window spatial lookups in
  `WorldQueryHelper` also use NonAlloc. Protocol **25** unchanged.
- **Inventory LNM façade retired:** `SyncItemAmount` moved to
  `InventorySyncUtil`; deleted `LanNetworkManager.InventoryHelpers.cs`.
  Protocol **25** unchanged.
- **`DreamDoorSyncPatches` split:** hinged Door open/unlock/unblock vs
  `DialogueDoorAftermath` (`DreamDoorSyncPatches.Aftermath.cs`). Protocol
  **25** unchanged.
- **Hub shrink below 600 (excl. message DTOs):** remaining service/patch files
  that were still 600–800 lines are now split:
  `DoorSyncPatches.Interact`, `PlayerActionSyncPatches.Combat`,
  `LanNetworkManager.SteamPeers`, `VoiceChatService.Speakers`,
  `SpectatorModeController.EnterExit` (behavior unchanged). Protocol **25**
  unchanged.
- **`DreamSyncPatches` split:** ~711-line dream Harmony file →
  `DreamSyncPatches.cs` (preset/prepare/start/end) + `DreamSyncPatches.Authority.cs`
  (chain/switch/end-authority/skills; behavior unchanged). Protocol **25**
  unchanged.
- **`WorldSaveShareService.Profiles` split:** ~690-line profile hub →
  `WorldSaveShareService.{Profiles,ProfilesEnter,ProfilesDisk}.cs` (behavior
  unchanged). Protocol **25** unchanged.
- **GameEvents scene-scan cache:** soft-match / bulk / dream leave-door paths
  reuse `WorldQueryHelper.GetCachedSceneComponents<GameEvents>()` (3s TTL) instead
  of raw `FindObjectsOfType` on every miss — cuts client hitch when pending GE
  flushes. Same helper now used on hot door/barricade/lock/station/trade/pickup/
  death-drop/interact apply paths. Caches invalidate on network stop and dream
  enter/exit (`InvalidateCommonSceneScanCaches`). Protocol **25** unchanged.
- **`WorldPhysicsSyncService.Snapshot` split:** ~708-line snapshot hub →
  `WorldPhysicsSyncService.{Snapshot,SnapshotScan}.cs` (build vs doors/traps/
  generators scan; behavior unchanged). Protocol **25** unchanged.
- **`WorldPhysicsSyncService.Thrown` split:** ~797-line thrown/flare hub →
  `WorldPhysicsSyncService.{Thrown,ThrownSpawn,ThrownLights}.cs` (behavior
  unchanged). Protocol **25** unchanged.
- **`MainMenuMultiplayerInject.Panel` split:** ~776-line title panel →
  `MainMenuMultiplayerInject.{Panel,PanelWidgets}.cs` (orchestration vs button
  widgets; behavior unchanged). Protocol **25** unchanged.
- **`WorldPhysicsSyncService.Apply` further split:** ~996-line apply hub →
  `WorldPhysicsSyncService.{Apply,Pickups,ObjectResolve}.cs` (plus existing
  TrapsDoors; behavior unchanged). Protocol **25** unchanged.
- **`ClientEntityInterpolationService` further split:** ~1002-line main (Tick
  already separate) → `ClientEntityInterpolationService.{Snapshot,Presentation,Spawn}.cs`
  (`partial` static; fields/public note API stay in main; behavior unchanged).
  Protocol **25** unchanged.
- **`SteamCoopTransport` split:** ~1105-line Steam lobby/SNS hub →
  `SteamCoopTransport.{PollSend,ConnectionCallbacks,LobbyCallbacks}.cs`
  (`sealed partial`; host/join/migration stay in main; poll/send, connection
  status, lobby callbacks; behavior unchanged). Protocol **25** unchanged.
- **`HostAIPatches.Perception` split:** ~753-line perception Harmony file →
  `HostAIPatches.Perception.CanSee.cs` (`HostCanSeeEnemyPatch`) + remaining
  awareness/melee patches in `Perception.cs` (behavior unchanged). Protocol
  **25** unchanged.
- **`ClientStateBackup` split:** ~892-line monolith →
  `ClientStateBackup.{Collect,Paths,Campaign,Restore}.cs` (`partial` static class;
  DTOs stay in main; collect/serialize, disk IO, campaign/progress heuristics,
  restore; behavior unchanged). Protocol **25** unchanged.
- **`HostMigration` split (restored):** a bad extract had dropped handoff/promote
  (~700 lines). Restored from HEAD; now `HostMigration.{PeerRoster,Handoff}.cs`
  plus main fields/ticks (mid-dream refuse stays inlined in Handoff). Protocol
  **25** unchanged.
- **`LanNetworkManager` core split:** ~1364-line main networking file →
  `LanNetworkManager.{Tick,Transport,PeerEvents}.cs` (`partial` class; fields /
  Awake / StartHost / ConnectToHost / StopNetwork stay in main; Update+LateUpdate
  tick, Send*/Broadcast/MarkPeer*, and peer connect/receive events moved;
  behavior unchanged). Protocol **25** unchanged.
- **`MainMenuMultiplayerInject` split:** ~1497-line title MULTIPLAYER UI →
  `MainMenuMultiplayerInject.{Panel,HostJoin}.cs` (`partial` class; fields /
  OnUpdate / lifecycle in main; panel construction vs host-join / handshake
  wait; behavior unchanged). Protocol **25** unchanged.
- **BulkSync LNM façade retired:** private Handle*/Send* wrappers removed;
  Dispatch/session/journal call `BulkSyncHandlers` directly. Real session
  callbacks live in `LanNetworkManager.SessionCallbacks.cs`. Protocol **25**
  unchanged.
- **`WorldSaveShareService` split:** ~1600-line monolith →
  `WorldSaveShareService.{HostShare,ClientApply,Profiles,Utils}.cs`
  (`partial` class; ctor/schedule stay in main; behavior unchanged).
  Protocol **25** unchanged.
- **`HostAIPatches` split:** monolithic combat AI Harmony file →
  `HostAIPatches.{Identity,Perception,Grid,Targeting}.cs` (ownership by
  concern; behavior unchanged). Protocol **25** unchanged.
- **`ClientEntityInterpolationService` Tick split:** `TickLateUpdate` moved to
  `ClientEntityInterpolationService.Tick.cs` (~1435→1002 main + 447 tick;
  `partial` class; behavior unchanged). Protocol **25** unchanged.
- **`WorldPhysicsSyncService` TrapsDoors split:** trap locate/apply, door find,
  and generator spawn/find helpers moved from `Apply.cs` (~1337→996 lines) into
  `WorldPhysicsSyncService.TrapsDoors.cs` (behavior unchanged). Protocol **25**
  unchanged.
- **Retire thin `LanNetworkManager` façades:** Dispatch / session / tick call
  `*NetHandlers` directly for Examine, Chapter, Cutscene, Door, CursorAction,
  Map, Chain, ShadowArmor, WorldBurn, and Epilogue SceneLoad. Handler
  properties live in `LanNetworkManager.HandlerRegistry.cs`. Deleted 10
  private Handle* wrapper partials. Protocol **25** unchanged (wire behavior
  identical).
- **Batch 2 façades retired:** Dialog (outcome/NPC-lock), Flag, GameEvent,
  Barricade, Trade, Container*, Station, Lock, Dream, Night. Dispatch/tick/
  session/external call sites now use `*Handlers` directly; another 10 thin
  LNM partials deleted. Protocol **25** unchanged.
- **Batch 3 façades retired:** Journal (workbench/vault/oxygen/compressor),
  Location (enter/exit/entity/trap), PlayerFX (anim/FX/dropped items +
  `DispatchRemotePlayerForward`). `WorkbenchLevelSync` Dispatch now calls
  `BulkSyncHandlers` directly. Handler props in `HandlerRegistry.cs`; 3 thin
  LNM partials deleted. Protocol **25** unchanged.
- **Batch 4 façades retired:** Combat, FX, Players, WorldSend, WorldState.
  Dispatch / tick / session / Steam / HostMigration / PlayerFX forward path
  call `*Handlers` directly. Remaining public/internal Send*/GetProxy/
  Notify*/RegisterDeathBag forwards live in
  `LanNetworkManager.PublicApi.cs`. Handler props in `HandlerRegistry.cs`;
  5 Domains LNM façades deleted (Domains LNM left: InventoryHelpers +
  WorldTickFields). Protocol **25** unchanged.

### Parked / deferred (investigation)

- **`SpriteRandomizer`:** parked (DEFERRED-ok cosmetic) — visual RNG only
  (color / rotation / mirror / sprite / anim / height). Decompile tooltip warns
  collider issues when `randomizeOnLoad` + non-circle collider with mirror /
  rotation. No sync unless a future playtest proves physics diverge. Evidence
  in `COOP_COVERAGE.md`. Protocol **25** unchanged.
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

- **Dual-box soak (beauty gate):** parked — structure/hot-path code is green
  (build + 66 PathB tests; DLL deployed Steam + SecondDarkwood). Runtime
  proof: run host+client, then `scripts/check-dualbox-perf.sh` (requires
  `[Perf]` on both logs, fails on `[PerfCliff]`). Also spot-check entity
  sync + GE fan-out in play. Protocol **25** unchanged.
- **`WaitAndDie` / `Broadcaster` / `UpgradeItemMenu`:** parked with decompile
  citations (FX/onTime→GE host-auth; serializer util; personal item upgrades).
  `WhereAmI` `player_in*` already local-only via FlagSync. Protocol **25**
  unchanged.
- **Examinable onExamine host authority:** clients still show personal examine
  HUD / local `DescriptionPool` draw, but `Core.sendTriggerInfo(onExamine)` is
  blocked on clients; host re-runs `examine()` with HUD suppressed so story GE
  fires once and examined / `displayedDescriptionPool` flags fan out (msg
  **110**). Shared pool string identity not wire-synced. Protocol **25**
  unchanged.
- **`AnimationPlay` / `MagicContainer` / respawn registry stubs:** parked with
  decompile citations (cosmetic anim RNG / empty stub / local WorldGenerator
  bookkeeping). Protocol **25** unchanged.
- **Presentation edge cases:** deferred bullet expanded with citations
  (spectator dialogue UI, gossip randomness, portrait overlays, lost
  dream-chain fallback) — parked as presentation-only, not world-authority.
  Protocol **25** unchanged.
- **Wrong-save policy tests:** `WorldSharePolicy.FormatWrongSave` /
  `IsWrongSaveMessage` live in pure `CoopPolicy` (unit-tested); UI wrapper
  unchanged. Protocol **25** unchanged.
- **Container simultaneous-open / dream host-migration:** parked with
  citations in `COOP_COVERAGE.md` (loot already host-validated; mid-dream
  migration refuses and disconnects by design).
- **ObjectPoolSpawnerController.spawn host authority:** clients Prefix-skip the
  early-gen controller pass (belt on top of existing ObjectPoolSpawner
  spawnObject/tryToSpawn skips). Protocol **25** unchanged.
- **Workbench exclusive lock:** confirmed parked (not re-enabled) — disabled
  since 0.7.40 playtest ask (both players may share a bench). Msg **119**
  reserved; stub + ignore handler remain. Evidence in `COOP_COVERAGE.md`.
- **Wrong-save warning UI:** join slot picker flags `[DIFFERENT CAMPAIGN]` when
  a profile's CampaignId differs from the host package and strengthens the
  overwrite confirm; refuse paths for host-push / RestoreSelf now call
  `WrongSaveWarning` (in-world `displayMessage` + join progress `WRONG SAVE`).
  Share-failure terminal path unchanged. Protocol **25** unchanged.
- **WorldGenerator early-gen spawn host authority:** clients Prefix-skip
  `spawnMiscObjects`, `spawnFreeRoamingCharacters`, `spawnGlobalCharacters`,
  `spawnNightObjects`, and `respawnAllEnemies` (same early-gen hole as
  `spawnRandomObjects` before `onFinished` block). No new message; protocol
  **25** unchanged.
- **WorldChunk.spawnRandomObjects host authority:** clients Prefix-skip chunk /
  `WorldGenerator.spawnRandomObjects` (early worldgen + hard-night startDay).
  Upgrades the prior “parked — host clock” note: connected clients can still
  run gen before `WorldGenSharePatch` blocks `onFinished`. No new message;
  protocol **25** unchanged.
- **RandomEvent.randomizeStartTime host authority:** clients Prefix-skip schedule
  rolls (`Events.initialize` + `onNewDay`). Fire was already blocked; independent
  `timeToStart` RNG still diverged from host / `ScenarioStateBulk` late-join.
  No new message; protocol **25** unchanged.
- **Worldgen RNG host authority:** Harmony Prefix on `RandomNumberGenerator.init`
  and `ChapterPreset.initFlags` — clients skip; Offline/Host keep vanilla.
  Decompile: RNG Awake/`init` rolls padlock digit tables; `generateWorld` may
  call `ChapterPreset.initFlags` (`randomFlags` story outcomes) before
  `WorldGenSharePatch` blocks `onFinished`. Independent client rolls would
  diverge combinations / flags ahead of WorldSaveShare. No new message;
  protocol **25** unchanged.
- **EventTriggers sight FOV parity:** host `isCurrentlyInSightOfPlayer` now uses
  `HostPlayerIdentity.AnyInSight` (same `Player.isInSight` + proxy `_transform`
  swap as Porter / `InSightOfPlayer`), including `inSightOfPlayerRadius`.
  Removes the prior simplified angle + `Core.canSee` proxy path that could
  disagree with vanilla FOV/dot. No new message; protocol **25** unchanged.
  Dual-box sight-trigger runtime still pending.
- **GameEventsBulk destroyOnFire latch:** host records one-shot `GameEvents`
  with `destroyOnFire` at live fire time (decompile schedules
  `Destroy(gameObject)` after event delays). Late-join `GameEventsBulk` (136)
  merges those identities with the live `fired && !multipleFire` scan so
  joiners still apply shells that are already gone on the host. No new
  message; protocol **25** unchanged. Dual-box late-join still runtime-pending.
- **SpawnPrefab host authority:** Harmony Prefix on `SpawnPrefab.Start` —
  clients skip; Offline/Host keep vanilla. Vanilla Start AddPrefab(GameObject)
  then Destroy(self) — both peers would duplicate. Observation via entity /
  WorldSaveShare (GameObject AddPrefab is not string-path PhysicsSpawnSync).
  No new message. Protocol **25** unchanged.
- **ObjectPoolSpawner host authority:** Harmony Prefix on
  `ObjectPoolSpawner.spawnObject` and `tryToSpawn` — clients skip; Offline/Host
  keep vanilla. Awake controller registration stays on all peers (host worldgen
  path). Biome prop place via `ObjectPoolSpawnerController`. Observation via
  entity / WorldSaveShare — no new message. Protocol **25** unchanged.
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
