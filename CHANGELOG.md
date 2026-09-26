# Changelog

## Versioning

The current product line is `0.8.x`. The plugin and display version are
**0.8.75**. The current Horde wire protocol is **25** (unchanged from 0.7.81;
this line is an architecture rewrite, not a wire bump).

This file is a public ship log. Code-only status and runtime status are called
out separately. A runtime item is not considered verified until it has been
tested in the game.

---

## 0.8.75 — SilentDisarmDepth + host forbidInputs Finalizers

Batch 45 dig: Batch 44 closed DialogClientWorldDefer / pickup-guard depth Ends, but
missed `ItemDisarmSilentTrapPatch` Prefix `SilentDisarmDepth++` + Postfix-only `--`,
and `DialogHostStaleBoardGuardPatch` Postfix-only `Core.forbidInputs` clear.
Harmony skips Postfix when the original throws → sticky silent-disarm routing /
host input lock. Protocol **25** unchanged. Product bump **0.8.74 → 0.8.75**.

- **Dig ranked:**
  1. **`ItemDisarmSilentTrapPatch` sticky SilentDisarmDepth — SHIPPED.**
     `TrapDisarmHarvestSync.cs` / `TrapDisarmHarvestTracker`: Prefix `++`; End only
     in Postfix. Throw mid `Item.disarm` → `IsSilentDisarm` forever → boom TrapState
     skipped (`DoorSyncPatches` / `ClientTrapTriggerPatch`) and every
     `switchToTriggered` mis-sent as silent harvest.
  2. **`DialogHostStaleBoardGuardPatch` sticky forbidInputs — SHIPPED.**
     `DialogHostPresentationSuppressPatches.cs`: world-only host apply Postfix
     clears `Core.forbidInputs` / `cantChangeForbidInputs` / `dw.forbidInputs` when
     `ShouldSuppress`. Throw mid `displayNextBoard` → host unable to walk/look/inv
     (changePortrait Invoke cancelled by silent-close).
  3. Remaining Begin/End / Postfix-only restore scan — **0** further residuals
     (`NightSpawnFlagPatch`, `GameEventFireFlavorSourcePatch`, pause UI, explosions,
     sounds, getHit, pickup, DialogClientWorldDefer already Finalizer).
  4. Trap `id=0` / ContainerTakeDenied — **skip** (no fresh 0.8.74 LogOutput; dual-box
     still banners **0.8.34** @ 16:02 MSK).
  5. Parked list — unchanged; no touch.
- **Shipped:**
  - `ItemDisarmSilentTrapPatch`: depth `--` moved to **Finalizer**; Postfix keeps
    TrySendSilentTrapState.
  - `DialogHostStaleBoardGuardPatch`: forbidInputs clear moved to **Finalizer**;
    Postfix keeps HideSpeakerVisuals.
- **Player situations:**
  - Disarm/harvest a beartrap and vanilla `Item.disarm` throws: later stomps still
    boom-sync; silent harvest wire no longer stuck on.
  - Host applies a peer NPC dialogue board that throws mid-`displayNextBoard`: host
    can walk/look/open inv again (forbidInputs not latched).
- **Preserved:** protocol 25, HostWorldReady 139, beartrap, indoor reverb,
  CoopWorldPresencePolicy.
- **Parked (unchanged):** WorkbenchOpenLock; oxygentank_full; mid-dream migrate;
  InvItem trailers; gasoline `__result` without NRE; trap id=0 until fresh
  0.8.75 dual-box evidence.
- **Rev / deploy:** dual-rev md5 match `2ef178af83dd9054ad16e5896e16d77a` → Steam +
  SecondDarkwood plugins. ProductInvariant assert Finalizer symbols.
- **Batch 45 residuals:** fresh 0.8.75 dual-box LogOutput (replace stale 0.8.34);
  trap id=0 / ContainerTakeDenied confirm; parked list unchanged.

## 0.8.74 — DialogClientWorldDefer + pickup-guard Finalizers

Batch 44 dig: Batch 42 `IsInside*`/`Suppress*` Finalizer scan reported 0 residuals,
but missed Begin/End **depth** guards. `DialogClientWorldDeferBoardPatch` Prefix
`Begin` + Postfix-only `End`, and `PlayerPickupDroppedItemPatch` Prefix
`TrapPickupGuard`/`WorldPickupWireGuard` Begin + Postfix-only End — Harmony skips
Postfix when the original throws, leaving sticky defer/guards. Protocol **25**
unchanged. Product bump **0.8.73 → 0.8.74**.

- **Dig ranked:**
  1. **`DialogClientWorldDeferBoardPatch` sticky Active — SHIPPED.**
     `DialogClientWorldDeferPatches.cs` / `DialogClientWorldDefer`: client
     `displayNextBoard` Prefix Begin; End only in Postfix finally. Throw →
     `Active` forever → `Flags.setFlag` / `Events.fireWorldEvent` /
     `OutsideLocations.prepareLocation` / `returnToWorld` / `Map.showElement`
     suppressed on the speaking client (NPC dialogue softlock / missed map pins).
  2. **`PlayerPickupDroppedItemPatch` sticky guards — SHIPPED.**
     `DroppedItemSyncPatches.cs`: Prefix `TrapPickupGuard.Begin` /
     `WorldPickupWireGuard.Begin`; End only in Postfix. Throw mid
     `getDroppedItem` → RemoveItem suppress for that trap inv and
     WorldObjectRemoved mute forever (pickup/container race residue).
  3. Trap `id=0` / junk ContainerTakeDenied — **skip** (Batch 42 closed; no fresh
     0.8.73 LogOutput; dual-box still banners **0.8.34** @ 16:02 MSK).
  4. HostMigration promote / soft-reconnect / generator fuel / prologue /
     map discoveries / dream party / audio forward / siege beyond getHit —
     audited; no new file:symbol CAN-fix beyond (1)(2). Sibling forks Yokyy /
     DarkwoodMod older than already-ported; RO skip.
  5. Parked list — unchanged; no touch.
- **Shipped:**
  - `DialogClientWorldDeferBoardPatch`: End moved to **Finalizer**; Postfix keeps
    dreamToStart scrub + board commit.
  - `PlayerPickupDroppedItemPatch`: TrapPickupGuard / WorldPickupWireGuard End
    moved to **Finalizer**; Postfix keeps deferred claim finish.
- **Player situations:**
  - Client talks to an NPC and a board/outcome throws mid-`displayNextBoard`:
    next dialogue choices still apply flags/world events/map pins (no permanent
    client world-defer lock).
  - Pick up a world drop / sprung beartrap loot and vanilla transfer throws:
    later container takes and world-object removes sync again (guards not stuck).
- **Preserved:** protocol 25, HostWorldReady 139, beartrap, indoor reverb,
  CoopWorldPresencePolicy.
- **Parked (unchanged):** WorkbenchOpenLock; oxygentank_full; mid-dream migrate;
  InvItem trailers; gasoline `__result` without NRE; trap id=0 until fresh
  0.8.74 dual-box evidence.
- **Rev / deploy:** dual-rev md5 match `64cc6942cd76ca2555bea68a2358310b` → Steam +
  SecondDarkwood plugins. ProductInvariant assert Finalizer symbols.
- **Batch 44 residuals:** fresh 0.8.74 dual-box LogOutput (replace stale 0.8.34);
  trap id=0 / ContainerTakeDenied confirm; parked list unchanged.

## Batch 42 — NO-SHIP (trap id=0 + junk ContainerTakeDenied dig; stay on 0.8.73)

Warexpor priority residual from Batch 41: client LogOutput spam
`[Trap] client: player 1 trapped id=0` plus `ContainerTakeDenied` / junk refund
after beartrap rescue. Protocol **25** unchanged. Product stays **0.8.73**.
Dual-deploy untouched (md5 `a7abd68ab8a68db8b03ccb36c93087d5`).

- **Dig ranked:**
  1. **Trap `id=0` trapped spam (P0 residual) — NO safe CAN-fix.**
     Dual-box LogOutput (mtime **26 Sep 16:02 MSK**, load banner **0.8.34**)
     shows host TrapSync `beartrap id=1` at `(-11617.70,-10.40,12268.80)` while
     host PlayerState reports `InBearTrap` + `TrapNetId=0` at
     `(-11608.13,16.00,12233.10)` (~37m XZ). `TrapNetworkId.ResolveOccupyingTrapId`
     (`LanNetworkManager.Tick` → `PlayerStateMessage.TrapNetId`) correctly returns
     0 outside the 2.5m XZ occupancy window; stashing NetId on any Trigger fire
     while `inBearTrap` would mis-attribute occupancy and block co-op rescue.
     `GetComponentInParent<Trigger>` hardening vs `WorldQueryHelper` is speculative
     for this 37m case — not shipped (beartrap preserve). Needs fresh **0.8.73**
     dual-box repro with VerboseLogging + trap timeline.
  2. **Junk `ContainerTakeDenied` after beartrap rescue — already fixed; skip.**
     Same stale log: `[SendPickup] called for Scrap metal` → WOR `junk` → host
     H6 deny take junk → client refund. Root cause was pre-**0.8.36** SendPickup
     using slot type `junk`. Current symbols already correct:
     `DroppedItemSyncHelpers.ResolveWorldPickupClaim` (trap GO name / scrap rewrite),
     `TrapPickupGuard` + `ContainerSyncHelpers.IsContainer` (no RemoveItem on trap
     inv), `WorldPhysicsSyncService.ShouldDestroyWorldPickup` (junk needle must not
     eat trap GO). Live DLL is **0.8.73**; no re-ship.
  3. Harmony Prefix sticky without Finalizer — scan **0** residual pairs
     (theme exhausted 0.8.70–0.8.72).
  4. Fresh other P0/P1 outside parked — none with file:symbol beyond above.
- **Skipped / parked (unchanged):** WorkbenchOpenLock; oxygentank_full; mid-dream
  migrate; InvItem trailers; gasoline `__result` without NRE; trap id=0 until
  fresh 0.8.73 dual-box evidence.
- **Shipped:** none (ZERO safe CAN-fixes).
- **Rev / deploy:** none. No product bump, no redeploy. Live remains **0.8.73**.
- **Batch 43 residuals:** fresh 0.8.73 dual-box LogOutput (replace stale 0.8.34
  banner at 16:02); repro host beartrap spring + peer rescue — confirm whether
  `trapped id=0` / ContainerTakeDenied still appear; parked list unchanged.

---

## 0.8.73 — displayMessage Postfix-hide (tutorial MoveNext NRE)

Batch 41 dig: fresh dual-box LogOutput (16:02 MSK) showed
`GameEvent+<fire>d__77.MoveNext` NullReferenceException on both host and client
right after `Hideout1_tutorial_02` proxy enter / GameEventsSync apply.
`UI.displayHelpMessage` already Postfix-hides (0.8.32), but
`Player.displayMessage` / `Core.displayMessage` still used Bool-Prefix false when
`PersonalFlavorHud.ShouldShow` was false — vanilla then does
`displayMessage(...).texts = texts` / `AssignDeathObjects` with no null check.
Protocol **25** unchanged. Product bump **0.8.72 → 0.8.73**.

- **Shipped:**
  - `PersonalFlavorHud.HideCharacterMessage` — alpha-0 + short longevity + deferred
    Destroy so MoveNext still holds a non-null ref.
  - `ExaminableHostHudSuppressPatch` + both `CoreDisplayMessage*SuppressPatch`:
    Prefix-null → **Postfix-hide** (same class as HelpMessage).
- **Player situations:**
  - Peer walks into hideout tutorial volume while you are elsewhere (or host
    proxy-fires GE for a remote body): no Unity NRE spam in `GameEvent.fire`;
    far peer still gets no personal hint flash; near peer still sees the tip.
  - Host examine re-run (SuppressCount): still no host flavor text for the
    client's examine (create-then-hide, not skip-create).
- **Preserved:** protocol 25, HostWorldReady 139, beartrap, indoor reverb,
  CoopWorldPresencePolicy.
- **Parked (unchanged):** WorkbenchOpenLock; oxygentank_full; mid-dream migrate;
  InvItem trailers; gasoline `__result` without NRE.
- **Rev / deploy:** dual-rev md5 match `a7abd68ab8a68db8b03ccb36c93087d5` → Steam +
  SecondDarkwood plugins. ProductInvariant 77/77 pass.

---

## 0.8.72 — Door/Window getHit Finalizer (BeginGetHit sticky)

Batch 40/41 dig: `DoorGetHitPatch` / `WindowGetHitPatch` Prefix `BeginGetHit` +
dict stash with End only in Postfix — Harmony skips Postfix when the original
throws, leaving sticky `IsInsideGetHit` (suppresses `destroyBarricade` sync
forever) and leaked Prefix stash. Same class as 0.8.70–0.8.71 Finalizer restores.
Protocol **25** unchanged. Product bump **0.8.71 → 0.8.72**.

- **Shipped:**
  - `BarricadeSyncHelpers` nesting-aware `_getHitDepth` (door A getHit can nest
    into door/window B; single id cleared A's suppress early).
  - `DoorGetHitPatch` / `WindowGetHitPatch`: EndGetHit + stash Remove moved to
    **Finalizer**; Postfix only sends BarricadeEvent.
- **Player situations:**
  - Night defense: smash a barricaded door or window — peer sees damage/destroy;
    if vanilla `getHit` throws mid-hit, next smash still syncs (no silent stuck
    suppress on that board).
  - Nested hit (rare): hitting one board that cascades into another no longer
    clears the outer suppress early (no double destroyBarricade fan).
- **Preserved:** protocol 25, HostWorldReady 139, beartrap, indoor reverb,
  CoopWorldPresencePolicy.
- **Parked (unchanged):** WorkbenchOpenLock; oxygentank_full; mid-dream migrate;
  InvItem trailers; gasoline `__result` without NRE.
- **Rev / deploy:** dual-rev md5 match `8c785589e177bd7074b8858d3aef1509` → Steam +
  SecondDarkwood plugins. ProductInvariant 16/16 pass.

---

## Batch 39 — NO-SHIP (day-death spectator audit + dig; stay on 0.8.71)

Warexpor concern: ship notes about a “day death” / death-of-a-player fix may have
meant we forcibly switch the dead peer to spectator on normal daytime death
(vanilla = bag drop ~half items + house respawn). Protocol **25** unchanged.
Product stays **0.8.71**. Dual-deploy untouched (md5 `919c300db8287d18e74ea38c7a63a3cd`).

- **Day-death verdict: CORRECT (no spectator on day death).**
  - **Vanilla** (`Player.onDeath`): `transportToHome` + revive at hideout, then
    `dropBody` when inventory > 1; `skipDay` only when
    `isHardNight && (!Core.isDay() || CurrentTime > nightTime - 50f)`.
  - **YokWare day path:** `ClientDeathPatch` / `HostDeathSendPatch` Prefix set
    `isNight` via `isHardNight && (!Core.isDay() || CurrentTime <= dayTime + 50f)`,
    then `DeathStateTracker.OnLocalDayDeath()` (clears `LocalNightDeath`, log
    “normal respawn”) and **`return true`** so vanilla bag + house continue.
    Remotes: `CombatDeathStateNetHandlers.HandlePlayerDied` →
    `OnRemoteDayDeath` (proxy Death1 pose; **no** `ForceEnter`).
  - **Bag sync:** `DeathBagDropSyncPatch` Postfix on `Player.dropBody` fans
    `DeathBagSpawn` (dream skipped only).
  - **House grid hygiene (not spectator):** `DayDeathTransportHomeGridPatch` +
    `OutsideLocationDeathGridHygienePatch` +
    `LocationEnterExitNetHandlers.OnLocalReturnedToWorldAfterDeath` leave stale
    outside-location grid / fan LocationExit so hideout respawn is visible.
  - **Spectator only when:**
    1. **Partial night death** — `NightDeathSkipDayPatch` →
       `EnterNightDeathSpectator` → `SpectatorModeController.ForceEnter`
       (gated `DeathStateTracker.LocalNightDeath`;
       `NightDeathPolicy.ShouldSuppressWorldDeathMutations` also blocks
       `transportToHome` / enemy respawn until morning).
    2. **Final dreamscene death** — `FinalDreamsceneManager` `ForceEnter`.
    3. F4 target cycle while already spectating (`SpectatorModeController.EnterExit`).
  - **Ship-note clarification:** 0.8.42 “Day-death proxy premature revive” /
    `LanNetworkManager.Tick` Death1 force when `LocalNightDeath || !local.alive`
    only keeps the remote proxy corpse-dead during vanilla’s brief `!alive`
    window / get-up clips — it does **not** enter spectator on day death.
- **Dig ranked (Batch 39, outside parked):**
  1. Day-death → spectator misunderstanding — **audited correct; no code change.**
  2. Remaining Prefix sticky flag / Postfix-only clear without Finalizer —
     scan found **0** residual `IsInside*`/`Suppress*`/`Inside*` set+clear pairs
     missing Finalizer (theme exhausted 0.8.70–0.8.71).
  3. Fresh other P0/P1 — none with file:symbol beyond parked list.
- **Skipped / parked (unchanged):** WorkbenchOpenLock; oxygentank_full fan;
  mid-dream migrate; InvItem trailers; gasoline/ObjectPool/addItemTypeToPlayer
  `__result=null` only if playtest NRE; fresh 0.8.71 dual-box LogOutput (both
  installs still show load banner **0.8.34**, mtime **26 Sep 16:02 MSK** — stale
  vs live **0.8.71** md5 `919c300db8287d18e74ea38c7a63a3cd`).
- **Shipped:** none (ZERO safe CAN-fixes).
- **Rev / deploy:** none. No product bump, no redeploy. Live remains **0.8.71**.

---

## 0.8.71 — Harmony Finalizer restore (more stash/flag)

Batch 38 dig: remaining Prefix sticky flag/counter + Postfix-only clear (Batch 37
class). Protocol **25** unchanged. Product bump **0.8.70 → 0.8.71**.

- **Dig ranked:**
  1. **Postfix-only stash/flag restore (CAN-FIX P0/P1)** — Harmony skips Postfix
     when the original throws; Prefix flags/counters stay wrong forever:
     - `ExplosionOnActivatePrefix` bumps `ActivationDepth` / `IsInsideSpawnObjects` /
       `IsHostSynced` / `CurrentExplodes`; Postfix-only clear → throw leaves host
       treating later `AddPrefab` as explosion secondaries (or depth never unwinds).
       Move clear to **Finalizer** (nesting-aware).
     - `ExplosionDamageSkipPatch` Prefix sets `IsInsideLocalExplosion`; Postfix-only
       clear → stuck true mis-routes client hitscan as explosion AOE. Add **Finalizer**.
     - `FastProjectileSweepPatch` Prefix sets `IsInsideFastProjectileRaycast`;
       Postfix-only clear → stuck true makes `HitscanImpactSyncPatch` skip forever.
       Add **Finalizer**.
     - `HostBansheeAgitatedPatch` Prefix sets `SuppressHostScreamForward`; Postfix-only
       clear → stuck true suppresses banshee scream forward. Add **Finalizer** clear.
     - `TrapPlacementPatch` Prefix sets `InsideTrapPlacement`; Postfix-only clear →
       stuck true suppresses WorldObject harvest/destroy. Add **Finalizer**.
     - `UiOpenNoPausePatches` / `UiCloseNoUnpausePatches` / `LevelingMenuHide` Prefix
       Begin + Postfix End on shared `SuppressPause`/`SuppressUnpause` → throw leaves
       pause/unpause blocked. Move End to **Finalizer** only (avoid double-End stealing
       LevelingMenu.show cross-method hold).
  2. **Fresh other P0/P1 outside parked** — none with file:symbol evidence beyond this
     Finalizer class. WorkbenchOpenLock / oxygentank_full / mid-dream migrate /
     InvItem trailers / gasoline `__result` untouched (no NRE proof).
  3. **LogOutput** — Steam + SecondDarkwood `BepInEx/LogOutput.log` mtime still
     **26 Sep 16:02 MSK**, load banner **0.8.34** (pre-0.8.68). Stale vs live
     **0.8.70** md5 `64fc462d0b3087b576423c544156c712`. No fresh 0.8.70 dual-box
     playtest log yet.
- **Shipped:** Explosion onActivate/explode Finalizer clears; FastProjectile +
  Banshee + TrapPlacement Finalizer flag clears; UI pause End→Finalizer;
  ProductInvariant Batch 38 gates.
- **Skipped / parked:** WorkbenchOpenLock; oxygentank_full fan; mid-dream migrate;
  InvItem trailers; gasoline/ObjectPool/addItemTypeToPlayer `__result=null`
  (no caller NRE proof).
- **Rev1:** Finalizer restores + ProductInvariant Batch 38 gates; Release BepInEx
  build OK (0 warn); PathB **77/77**.
- **Rev2:** hubs OK (ExplosionSpawn 175, FastProjectile 181, HostAIPatches.Targeting
  373, DoorSyncPatches 419, NoWorldPause 150, all &lt;500); HostWorldReady/_Highest
  **139**; protocol **25**; dual-deploy Steam+SecondDarkwood md5
  `919c300db8287d18e74ea38c7a63a3cd`.
- **Player situations:** If a grenade/explosion activate hiccups, the host no longer
  keeps tagging every later spawn as an explosion secondary. If a local explode or
  bullet FixedUpdate throws, peer hitscan/explosion damage routing and impact sync
  keep working. If banshee agitates and throws mid-call, scream forward is not stuck
  off. If trap placement throws mid-progress bar, world harvest/destroy is not stuck
  suppressed. If map/journal/dialogue open throws, co-op pause is not stuck blocked.
- **Batch 39 residuals:** parked WorkbenchOpenLock / oxygentank_full fan /
  mid-dream migrate / InvItem trailers; gasoline/ObjectPool/addItemTypeToPlayer only
  if playtest NRE; fresh 0.8.71 dual-box LogOutput (replace stale 0.8.34).

---

## 0.8.70 — Harmony Finalizer restore (stash/flag bad-state)

Batch 37 dig: remaining Prefix `return false` without `__result` + Postfix/Finalizer
bad-state. Protocol **25** unchanged. Product bump **0.8.69 → 0.8.70**.

- **Dig ranked:**
  1. **Postfix-only stash/flag restore (CAN-FIX P0/P1)** — Harmony skips Postfix when
     the original throws; Prefix mutations / sticky flags stay wrong forever:
     - `HostCheckStuffPatch` Prefix clears `temporarySpawned` / `wantToDespawn` /
       `forestSpirit` for remote-near NPCs, restored only in Postfix → throw leaves
       never-despawn / spirit-idle corruption. Move restore to **Finalizer**.
     - `EntitySoundSyncPatches` Prefix sets `TraverseHack.InsideCharacterSounds` (+
       `InsideEscapingLoop` on escaping); Postfix-only clear → stuck true suppresses
       PlayerAudio forward / AudioSuppression for the rest of the session. Add
       **HarmonyFinalizer** clears (Idle/Growl/Escaping/SingleInstance/play/GetHit).
     - `ClientProjectileDamagePatch` Prefix sets `IsInsidePlayerBulletCollision`;
       Postfix-only clear → stuck true mis-routes peer projectile/hitscan damage.
       Add **Finalizer** clear.
  2. **Remaining non-void Prefix without `__result` (skipped — no NRE proof)** —
     Cecil scan vs Assembly-CSharp: only gasoline `Core.AddPrefab` (GameObject) and
     `ObjectPoolSpawner.spawnObject`/`tryToSpawn` (GameObject). Caller IL:
     - GasolineTrail string path → `Player.waitToSpillLiquid` → `addToSaveable` which
       `op_Inequality` null-checks (returns null). Object overload callers
       (`Explodes.spawnObjects`) **pop** the result. No NRE path.
     - ObjectPool only external caller `ObjectPoolSpawnerController.tryToSpawn`
       null-checks with `op_Inequality`. No NRE path.
     - `Inventory.addItemTypeToPlayer` (InvItemClass): all 15 call sites **pop** or
       `InvItemClass.isNull` — RISK=0. Dialog suppress stays as-is.
     No remaining IEnumerator Prefix without `__result` (Batch 36 EmptyRoutine covered).
  3. **Fresh other P0/P1 outside parked** — none with file:symbol evidence beyond
     this Finalizer class. WorkbenchOpenLock / oxygentank_full / mid-dream migrate /
     InvItem trailers untouched.
  4. **LogOutput** — Steam + SecondDarkwood `BepInEx/LogOutput.log` mtime still
     **26 Sep 16:02 MSK**, load banner **0.8.34** (pre-0.8.68). Stale vs live
     **0.8.69** md5 `70c1efdc6ba3d472a1e9af346c82cb1e`. No fresh 0.8.6x dual-box
     playtest log yet.
- **Shipped:** HostCheckStuff Finalizer restore; EntitySound + ClientProjectile
  Finalizer flag clears; ProductInvariant `HarmonyFlagStash_UsesFinalizerRestore`.
- **Skipped / parked:** WorkbenchOpenLock; oxygentank_full fan; mid-dream migrate;
  InvItem trailers; gasoline/ObjectPool/addItemTypeToPlayer `__result=null`
  (no caller NRE proof).
- **Rev1:** Finalizer restores + ProductInvariant gate; Release BepInEx build OK;
  PathB **77/77**.
- **Rev2:** hubs OK (HostAIPatches.Perception 402, EntitySoundSync 312,
  ClientProjectile 30, all &lt;500); HostWorldReady/_Highest **139**; protocol **25**;
  dual-deploy Steam+SecondDarkwood md5 `64fc462d0b3087b576423c544156c712`.
- **Player situations:** If an NPC `checkStuff` hiccups while a co-op partner is
  nearby, that NPC no longer stays permanently "don't despawn" / spirit-idle from a
  half-applied keep-alive. If a creature sound or your bullet collide throws mid-call,
  the mod no longer leaves "inside character sounds" or "inside bullet collide" stuck
  on — peer damage and audio forwarding keep working.
- **Batch 38 residuals:** parked WorkbenchOpenLock / oxygentank_full fan /
  mid-dream migrate / InvItem trailers; gasoline/ObjectPool/addItemTypeToPlayer only
  if playtest NRE; fresh 0.8.70 dual-box LogOutput (replace stale 0.8.34).

---

## 0.8.69 — Harmony IEnumerator suppress EmptyRoutine (HelpMessage-class)

Batch 36 creative adversarial dig: Prefix `return false` on non-void methods
without `__result`. Protocol **25** unchanged. Product bump **0.8.68 → 0.8.69**.

- **Dig ranked:**
  1. **Harmony suppress NRE (IEnumerator / StartCoroutine null)** — same class as
     0.8.37 HelpMessage / `GameEvent.fire`. Cecil scan of Prefix `return false`
     without `__result` against `Assembly-CSharp` found coroutine skips that leave
     `__result` null while vanilla `StartCoroutine(method())` callers:
     - `Dreams.prepareDream` (client abort + host TryBegin reject)
     - `Player.onDeath` (dream-death skip, host+client)
     - `CharacterSpawnPoint.waitToSpawnCharacter` (client skip)
     - `CharacterSpawner.waitToSpawnWorm` / `waitToSpawnShadow` / `spawnForestSpirit`
       (client disable + host forest-spirit redirect)
     Fix: shared `HarmonyCoroutineUtil.Empty()` assigned to `__result` before
     `return false` (mirrors `GameEventDreamAuthorityPatch`).
  2. **Remaining non-void suppressors (skipped)** — `Core.AddPrefab` gasoline trail
     (intentional null; network apply uses Explicit flag), `ObjectPoolSpawner`
     (controller `op_Inequality` null-check), `Inventory.addItemTypeToPlayer`
     (dialog path `pop`s result). Not StartCoroutine-null class; no playtest NRE.
  3. **Sibling fork gaps** — Yokyy/DarkwoodMod have same ClientWorld / onDeath
     Prefix skips without EmptyRoutine; nothing safer to port. No wholesale merge.
  4. **Hot net Apply null/throw** — DialogOutcome / Flag / Night Apply paths already
     null-guard `Player.Instance` / Singletons. No new CAN-fix.
  5. **LogOutput** — Steam `BepInEx/LogOutput.log` mtime 26 Sep 16:02 MSK loads
     **0.8.34** (pre-0.8.68). Shows historical `GameEvent.fire` MoveNext NRE on
     Hideout1_tutorial_02 — stale vs live **0.8.68** md5. No fresh 0.8.6x playtest.
- **Shipped:** EmptyRoutine on prepareDream / onDeath / waitToSpawn* /
  spawnForestSpirit Prefix suppresses; `HarmonyCoroutineUtil`; ProductInvariant gate.
- **Skipped / parked:** WorkbenchOpenLock; oxygentank_full fan; mid-dream migrate;
  InvItem trailers; gasoline/ObjectPool/addItemTypeToPlayer non-IEnumerator
  suppressors (no StartCoroutine-null proof).
- **Rev1:** EmptyRoutine util + Prefix `__result` on prepareDream / onDeath /
  waitToSpawn* / spawnForestSpirit; ProductInvariant gate; Release build OK.
- **Rev2:** GameEventDreamAuthorityPatch brace cleanup after util migrate;
  PathB **76/76**; hubs OK (DreamSyncPatches 239, ClientWorld 195,
  NightSpawnRedirect 153, all &lt;500); HostWorldReady/_Highest **139**;
  protocol **25**; dual-deploy Steam+SecondDarkwood md5
  `70c1efdc6ba3d472a1e9af346c82cb1e`.
- **Player situations:** If a peer aborts `prepareDream("")` while waiting host
  DreamStarted, or dies in shared dream, or client skips worm/spawn wait
  coroutines — Unity no longer throws "routine is null" from those Prefix skips.
  Gameplay outcome unchanged (still skip); only the NRE is gone.
- **Batch 37 residuals:** parked WorkbenchOpenLock / oxygentank_full fan /
  mid-dream migrate / InvItem trailers; optional defensive `__result=null` on
  gasoline AddPrefab / ObjectPool / dialog addItemTypeToPlayer only if playtest
  shows NRE; fresh 0.8.69 dual-box LogOutput.

---

## Batch 35 — NO-SHIP (wiki unique softlock + TODO dig; stay on 0.8.68)

Warexpor unique / limited progression dig + AGENTS/CHANGELOG/FIXME near
networking. Protocol **25** unchanged. Product stays **0.8.68**. Dual-deploy
untouched (md5 `1d60791c92cd0ce18f020b2d58bde057`).

- **Wiki unique / limited (cross-check vs mod):**
  - **Keys** (Prologue Key, Big Metal Key, Cellar/Chest/Rusty/Wolf hideout,
    Burned House/Cottage, Room Key, Mushroom Granny, Sawmill, Shed, Twisted,
    Keyring, etc.) → `KeyReference` live JournalItem + already-claimed Prefix
    destroy + late-join `DestroyWorldJournalObject` (KeyReference scan). Covered.
  - **Doctor / chapter quest items** (Doctor key reclaim, instructions, Wolf
    wantsToTalk) → shared journal + ReputationBulk / DialogTree wantsToTalk
    (0.8.55). No new softlock file:symbol.
  - **Elephant → Empty Oxygen Tank; Old Shed second empty; Compressor Parts →
    compressor fill** → `OxygenTankAcquirePatch` fans `oxygentank_empty`;
    `CompressorConvertDetectPatch` fans empty→full convert; `PeerItemPresence`
    OR on host `EventTriggerRequirement.haveItem` includes Inventory+Hotbar
    (0.8.52). Flooded passage is personal haveItem / dialogue
    (`noOxygenTankToGoUnderwater`); host world gates use PeerItemPresence.
  - **Compressor Parts / Drawings / other quest InvItems** → journal
    QuestItemReference path + host-auth world pickup claim
    (`FinishWorldPickupClaim` / GUID claim). No new missing destroy path.
- **Dig ranked:**
  1. **oxygentank_full world-pick fan** — still no softlock proof beyond hotbar
     PeerItemPresence + empty fan + compressor convert. Vanilla
     `getItemInPlayer` already includes Hotbar for local dive. Second empty
     (Old Shed) remains alternate acquire. Inventing a full-tank fan without
     playtest brick = not a CAN-fix. **Parked / skip.**
  2. **WorkbenchOpenLock** — intentional product park since 0.7.40
     (`COOP_COVERAGE`: both may open/use; vanilla `Workbench.open` has no
     exclusive latch; msg 119 stub + ignore handler; disconnect release is
     no-op). Not a softlock. Surgical DragClaim-style lock only if playtest
     asks one-crafter. **Skip.**
  3. **AGENTS.md / CHANGELOG / FIXME|TODO near networking** — no unshipped
     P0/P1 softlock with file:symbol outside the parked list (Hub comments are
     TraverseHack / historical bug notes, not open holes).
  4. **Host-auth unique pickup / journal destroy / PeerItemPresence** —
     re-validated against wiki set; no regression hole found. Do not re-fix.
  5. Fresh other P0/P1 with file:symbol — **none**.
- **Skipped / parked (unchanged):** WorkbenchOpenLock; hotbar 3D; trap mid-lerp;
  promote auto-Save; night music; oxygentank_full world-pick fan; mid-dream
  migration; Examinable examined; EventTrigger.fired bulk; timeSeen/modifiers;
  locationDirections; timeDeactivated; InvItem trailers on
  trade/drop/deathbag/container (theme exhausted through 0.8.68).
- **Shipped:** none (ZERO safe CAN-fixes).
- **Rev / deploy:** none. No product bump, no redeploy. Live remains **0.8.68**
  md5 `1d60791c92cd0ce18f020b2d58bde057`. Protocol **25**; HostWorldReady/_Highest
  **139**; beartrap / indoor reverb / CoopWorldPresencePolicy preserved.
- **Player situations:** N/A (no-ship).
- **Batch 36 residuals:** same parked list; only ship if playtest proves a
  concrete unique softlock (esp. oxygentank_full world-pick / late-join after
  convert) or asks Workbench one-crafter exclusive lock.

## Batch 34 — NO-SHIP (fresh dig, non-item-wire; stay on 0.8.68)

Fresh dig outside InvItem trailers / parked list. Protocol **25** unchanged.
Product stays **0.8.68**. Dual-deploy untouched (md5 `1d60791c92cd0ce18f020b2d58bde057`).

- **Dig ranked:**
  1. **Proxy attack swing / hit react** — PlayerAnimationTriggerPatch + PlayerState
     torso clips + SecondPlayerAnimController transient Once-clip guard already cover
     Attack/HitN. ProxyDamagePatch→DamagePlayer/FF + HitscanBloodPatch forward blood.
     HostMeleeSensorPatch blood+DamagePlayer. No remaining file:symbol hole. Skip.
  2. **Stealth / crouch / smell** — vanilla has **no crouch**. Smell = Sniffer + FOV;
     HostSnifferUpdatePatch / HostCanSeeEnemyPatch / WorldProxyLifecycle notice already
     multi-proxy. Skip.
  3. **Fire / burning world objects** — WorldBurnState Door/Window/Item + EntityBurning
     / PlayerBurning + LiquidStopBurning. Infection AddComponent<Burn> only clears
     splats (InfectionDisappear synced); no world-burn target hole. Skip.
  4. **Gas / mushrooms / spores** — GasTrail/GasIgnite + late-join SendGasStateTo;
     WormsSpawner host-auth; Infection spawn/disappear live+bulk. Skip.
  5. **Wolf / dog companion** — wiki+decompile: no persistent companion. Dog lure/eat
     host AI + EntityState Behaviour.following presentational. PetDog via PlayerAnim.
     Skip.
  6. **Piotrek / special NPC follow** — dialogue vendor + tractor parts (personal);
     Aggressiveness.follower is story NPC, EntityState packs following. Skip.
  7. **Chapter transition softlocks** — ChapterProgression + share fallback (3×12s) +
     ChapterSessionResume loadingGame unstick 45s already ship. No new softlock
     file:symbol. Skip.
  8. **Host migration mid-combat** — ReclaimSimulationAuthorityAfterPromote +
     ReleaseAuthorityForPromote + EntityStateBroadcast Resume + ClientAI Role-dynamic;
     promote auto-Save still parked (F3 reminder). Mid-dream still parked. No new
     mid-combat CAN-fix. Skip.
  9. Fresh other P0/P1 with file:symbol — **none**.
- **Skipped / parked (unchanged):** WorkbenchOpenLock; hotbar 3D; trap mid-lerp;
  promote auto-Save; night music; oxygentank_full world-pick fan; mid-dream migration;
  Examinable examined; EventTrigger.fired bulk; timeSeen/modifiers; locationDirections;
  timeDeactivated; InvItem trailers on trade/drop/deathbag/container (theme exhausted
  through 0.8.68 — do not re-fix).
- **Rev / deploy:** none. ZERO safe CAN-fixes → no bump, no redeploy.
- **Batch 35 residuals:** same parked list; only ship if playtest proves a concrete
  non-item-wire P0/P1 with file:symbol.

## 0.8.68 — TradeInventory upgrades + shouldBeActive stock parity

Batch 33 (Warexpor residual — player-sold upgraded / flashlight-on items on
trader absolute stock; fresh dig outside parked + outside just-shipped empty-mag).
Protocol **25** unchanged.

- **Sold upgraded item shows base on peer trader (P1):** Drop/death-bag/container
  already carried workbench `ItemUpgrade` names (0.8.65), but
  `TradeInventorySync` only had type/amount/IsRecipe/durability. Sell an upgraded
  axe to NightTrader → peer `ApplyToNpc` `createItem` left upgrades empty —
  buy-back / peer UI showed base damage. Wire: AvailableBytes upgrade trailer
  (count+names per entry) after the 0.8.62 recipe/dur block, same pattern as
  `ContainerStateSync`. Build collects via `InvItemUpgradeWire.CollectNames`;
  Apply calls `InvItemUpgradeWire.Apply`. NPC InventoryRandom stock rarely has
  upgrades; trailer still required for player-sold items.
- **Sold flashlight-on shows off on peer trader (P1):** `shouldBeActive` already
  on drop/death-bag/container (0.8.66) and ClientStateBackup (0.8.59). Trade
  absolute stock omitted it. Wire: bool trailer per entry after upgrades;
  Apply via `InvItemTransferApply.ApplyMeta` (also keeps empty-mag / 0-dur from
  0.8.67).
- **Dig ranked:**
  1. **TradeInventorySync upgrades + shouldBeActive — SHIPPED**
     (SyncMessages TradeInventorySyncMessage; TradeSyncPatches Build/Apply).
  2. timeDeactivated on transfer — still ~1s early regen only; no playtest
     recharge evidence. Skip.
  3. oxygentank_full world-pick fan — parked (PeerItemPresence / compressor
     path covered). Skip.
  4. WorkbenchOpenLock — parked stub by design. Skip.
  5. hotbar 3D / trap mid-lerp / promote auto-Save / night music / Examinable
     examined / mid-dream migration / EventTrigger.fired bulk /
     timeSeen/modifiers / locationDirections — parked unchanged. Skip.
  6. Trade empty-mag / 0-dur — just shipped 0.8.67; do not re-fix. Skip.
  7. Drop/death-bag/container upgrade/active trailers — just shipped
     0.8.65–0.8.66; do not re-fix. Skip.
  8. Fresh other P0/P1 outside parked — none beyond this Trade wire hole.
- **Skipped / parked (unchanged):** WorkbenchOpenLock; hotbar 3D; trap mid-
  lerp; promote auto-Save; night music; oxygentank_full world-pick fan;
  mid-dream migration; Examinable examined presentation; EventTrigger.fired
  bulk; timeSeen/modifiers; journal locationDirections; empty-mag / 0-dur on
  TradeInventory (just shipped); upgrade/shouldBeActive/IsRecipe/DoorOpen on
  drop-deathbag-container (just shipped).
- **Rev1:** TradeInventorySync Upgrades[][] + ShouldBeActive[] AvailableBytes
  trailers; Build CollectNames/shouldBeActive; Apply UpgradeWire + ApplyMeta;
  ProductInvariant gate; version 0.8.68.
- **Rev2:** ApplyMeta only when both abs-dur + shouldBeActive trailers present
  (legacy 0.8.62–0.8.67 dual-deploy leaves createItem defaults); PathB 75/75;
  hubs OK (TradeSync 426, CombatDeathBag 395, ContainerSyncPatches 364);
  HostWorldReady/_Highest=139; protocol 25; dual-deploy Steam+SecondDarkwood.
- Protocol **25** unchanged. Product bump **0.8.67 → 0.8.68**.
- **Deployed md5** `1d60791c92cd0ce18f020b2d58bde057` (build = Steam host =
  SecondDarkwood client).
- **Runtime:** code-only until dual-box: upgrade melee at workbench → sell to
  NightTrader → peer opens trade → same upgrades on stock; sell ON flashlight
  → peer stock keeps shouldBeActive; buy-back restores upgrades/active.
- **Batch 34 residuals:** oxygentank_full world-pick fan; WorkbenchOpenLock;
  hotbar 3D; trap mid-lerp; promote auto-Save (F3); night music; Examinable
  examined presentation; mid-dream migration; EventTrigger.fired only if
  playtest proves; timeSeen/modifiers / locationDirections / timeDeactivated
  only if playtest proves; pre-0.8.68 TradeInventory packets omit upgrades /
  shouldBeActive (legacy apply = base / off).

---

## 0.8.67 — TradeInventory empty-mag + 0-dur stock parity

Batch 32 (Warexpor fresh dig — InvItem peer-transfer fields exhausted after
0.8.66; dig elsewhere: timeDeactivated / reload-aim / heal-eat double-consume /
sleep-bed / multi-session; Trade stock hole found). Protocol **25** unchanged.

- **Empty magazine sold/restocked gun vanishes from peer trader UI (P1):**
  `TradeInventorySync.BuildMessage` used `Amounts = ammo` for hasAmmo items and
  skipped `amt <= 0`. Sell an empty pistol to NightTrader → absolute fan omitted
  the gun; peer `ApplyToNpc` cleared stock and never recreated it. Client→host
  trade reply could also drop the empty gun from host truth. Build now keeps
  hasAmmo entries with Amounts=0; Apply creates with Amount→ammo (0 stays empty)
  after an ItemsDatabase hasAmmo gate.
- **Broken (durability 0) sold item restored full on peer trader (P1):** Apply
  used `absDur > 0f` after createItem(…, 1f, …) — same hole class as container
  0.8.66. Always assign absolute durability (including 0).
- **Dig ranked:**
  1. **TradeInventorySync empty-mag omission + 0-dur apply — SHIPPED**
     (file:symbol BuildMessage `amt <= 0` skip; ApplyToNpc `absDur > 0f`).
  2. timeDeactivated on transfer — regeneratesWhenInactive uses
     `timeDeactivated < Time.time - 1f`; peer createItem defaults 0 → ~1s early
     regen only. No playtest recharge evidence. Skip.
  3. Reload animation / chamber — torso reload clips already via
     PlayerAnimationTriggerPatch; chamber ammo is personal until drop (ammo on
     wire 0.8.58/66). No remaining file:symbol gap. Skip.
  4. Aim / ADS / zoom — local FOV / Far Look; proxy aim pose via synced torso
     clips; Crosshair gated off on proxy by design. Skip.
  5. Healing / eat/drink double-consume — InvItemClass.use is local personal
     inventory; no network use() fan. No evidence. Skip.
  6. Sleep / bed remaining — SleepEndRequest clock only; no vanilla per-bed
     SaveState (0.8.62). Skip.
  7. Multi-session brick beyond 0.8.49–0.8.61 — Player.SaveState vs
     ClientStateBackup covered; remaining fields parked (timeSeen/modifiers /
     locationDirections / gotHit readers). No new softlock file:symbol. Skip.
- **Skipped / parked (unchanged):** WorkbenchOpenLock; hotbar 3D; trap mid-
  lerp; promote auto-Save; night music; oxygentank_full world-pick fan;
  mid-dream migration; Examinable examined presentation; EventTrigger.fired
  bulk; timeSeen/modifiers; journal locationDirections; shouldBeActive /
  empty-mag / 0-dur / upgrades / IsRecipe / DoorOpen on drop-deathbag-container
  (just shipped — do not re-fix).
- **Rev1:** TradeInventorySync Build empty-mag keep; Apply hasAmmo+ammo assign +
  always absDur; ProductInvariant gate; version 0.8.67.
- **Rev2:** Apply durability only when Durabilities trailer present (legacy
  pre-0.8.62 safe); PathB 74/74; hubs OK (TradeSync 400, CombatDeathBag 395,
  ContainerSyncPatches 364); HostWorldReady/_Highest=139; protocol 25;
  dual-deploy Steam+SecondDarkwood.
- Protocol **25** unchanged. Product bump **0.8.66 → 0.8.67**.
- **Deployed md5** `a1cc9ce6680e448f2bb443d70814bd51` (build = Steam host =
  SecondDarkwood client).
- **Runtime:** code-only until dual-box: sell empty pistol / broken melee to
  NightTrader → peer opens trade → same empty/broken item still in stock;
  buy-back keeps 0 ammo / 0 durability.
- **Batch 33 residuals:** oxygentank_full world-pick fan; WorkbenchOpenLock;
  hotbar 3D; trap mid-lerp; promote auto-Save (F3); night music; Examinable
  examined presentation; mid-dream migration; EventTrigger.fired only if
  playtest proves; timeSeen/modifiers / locationDirections / timeDeactivated
  only if playtest proves; Trade upgrades/shouldBeActive only if playtest
  proves; pre-0.8.67 TradeInventory packets omit empty-mag / may full-bar
  broken (legacy apply).

---

## 0.8.66 — shouldBeActive + empty-mag / 0-dur on peer createItem

Batch 31 (Warexpor fresh dig — InvItem fields lost on drop/death-bag/container
peer createItem; ammo/durability verify; broader multi-session). Protocol **25**
unchanged.

- **Flashlight on/off stripped on peer copies (P1):** ClientStateBackup already
  persisted `shouldBeActive` (0.8.59) for the owning player, but
  `DroppedItemSpawn`, `DeathBagSpawn`, `ContainerItem`, and
  `ContainerStateSync` never carried it. Peer `createItem` left the flag
  false — ON flashlight dropped / bagged / crated came back OFF when another
  player took it (Player.currentItem light gate needs the flag). Wire:
  AvailableBytes bool trailer after the upgrade trailer on those messages;
  place-deny refund too. Send paths collect; `InvItemTransferApply.ApplyMeta`
  after createItem.
- **Empty magazine → 1 round on peer (P1):** Wire already had Ammo, but death-
  bag / container apply used `Ammo > 0` and skipped zero. Vanilla createItem
  maps Amount→ammo for hasAmmo, so Amount=1 left the peer with 1 in the mag.
  Apply now always assigns ammo when hasAmmo (drop path already did).
- **Broken item (durability 0) restored full on peer container (P1):** Container
  Place / StateSync / place-deny used `Durability > 0f` (deny also fed absolute
  dur into the createItem 0..1 multiplier). Always assign absolute durability
  via ApplyMeta after createItem(…, 1f, …).
- **Dig ranked:**
  1. **shouldBeActive on death-bag / drop / container — SHIPPED.**
  2. **Empty-mag ammo apply hole — SHIPPED** (apply-side; ammo already on wire).
  3. **Durability already on wire — verified;** 0-dur container apply hole —
     SHIPPED with ApplyMeta.
  4. Other InvItem fields (timeSeen / modifiers / timeDeactivated / modifierQuality)
     — no new playtest evidence beyond parked list. Skip.
  5. Broader non-item multi-session brick — no new file:symbol evidence outside
     parked. Skip.
- **Skipped / parked (unchanged):** WorkbenchOpenLock; hotbar 3D; trap mid-
  lerp; promote auto-Save; night music; oxygentank_full world-pick fan;
  mid-dream migration; Examinable examined presentation; EventTrigger.fired
  bulk; timeSeen/modifiers; journal locationDirections; upgrade trailers /
  DoorOpen / recipe IsRecipe (just shipped — do not re-fix).
- **Rev1:** InvItemTransferApply; ShouldBeActive trailers; send+apply on
  drop/death-bag/container (+late-join sync, deny refund); empty-mag/0-dur
  apply; ProductInvariant gate.
- **Rev2:** PathB 73/73; hubs OK (CombatDeathBag 395, ContainerSyncPatches
  364, DroppedItems 189); HostWorldReady/_Highest=139; protocol 25;
  dual-deploy Steam+SecondDarkwood.
- Protocol **25** unchanged. Product bump **0.8.65 → 0.8.66**.
- **Deployed md5** `007329e2ecb8cafbe7d17e2e57e12d4c` (build = Steam host =
  SecondDarkwood client).
- **Runtime:** code-only until dual-box: turn flashlight ON → drop / die / put
  in crate → peer picks up / loots / takes → select item → light still ON;
  empty pistol in crate → peer sees 0 rounds; broken weapon in crate → peer
  sees 0 durability.
- **Batch 32 residuals:** oxygentank_full world-pick fan; WorkbenchOpenLock;
  hotbar 3D; trap mid-lerp; promote auto-Save (F3); night music; Examinable
  examined presentation; mid-dream migration; EventTrigger.fired only if
  playtest proves; timeSeen/modifiers / locationDirections / timeDeactivated
  only if playtest proves; pre-0.8.66 shouldBeActive trailers absent (legacy
  apply = off / prior ammo>0 / dur>0 holes).

---

## 0.8.65 — Shared InvItem upgrades on death-bag / drop / container

Batch 30 (Warexpor residual — shared workbench ItemUpgrade names on
death-bag / ground drop / container Place+StateSync; deny place refund;
fresh dig in those files). Protocol **25** unchanged.

- **Shared upgraded items stripped on peer copies (P1):** ClientStateBackup
  already persisted `Upgrades[]` (0.8.59) for the owning player, but
  `DroppedItemSpawn`, `DeathBagSpawn`, `ContainerItem`, and
  `ContainerStateSync` only carried type/amount/dur/ammo/`IsRecipe`. Peer
  `createItem` rebuilt a base weapon — workbench damage/dur modifiers gone
  when the item moved between players via drop, death bag, or shared crate.
  Wire: AvailableBytes upgrade trailer (byte count + names) after the
  IsRecipe trailer on those messages; `InvItemUpgradeWire` Collect/Apply/
  Write/TryRead(+Many). Send paths collect; apply paths after createItem.
  Place-deny refund carries the same trailer on `ContainerTakeDenied`.
- **Dig ranked:**
  1. **Shared InvItem upgrades on death-bag / drop / container — SHIPPED**
     (DroppedItemSpawn + late-join SyncExistingDroppedItems;
     DeathBagSpawn + late-join SyncExistingDeathBags;
     ContainerItem Place + ContainerStateSync snapshots + InventoryRandom
     fan-out; place-deny refund).
  2. TradeInventory upgrades — NPC shop stock is InventoryRandom (no
     workbench upgrades). Skip.
  3. UpgradeItemMenu / UpgradeItemBtn — remains personal (COOP_COVERAGE
     parked). Skip.
  4. DoorOpen OpenForce — just shipped 0.8.64; do not re-fix. Skip.
  5. Fresh other P0/P1 in touched files — none beyond this wire hole.
- **Skipped / parked (unchanged):** WorkbenchOpenLock; hotbar 3D; trap mid-
  lerp; promote auto-Save; night music; oxygentank_full world-pick fan;
  mid-dream migration; Examinable examined presentation; EventTrigger.fired
  bulk; timeSeen/modifiers; journal locationDirections.
- **Rev1:** InvItemUpgradeWire; message trailers; drop/death-bag/container
  send+apply; deny refund; ProductInvariant source gate.
- **Rev2:** PathB 72/72; hubs OK (CombatDeathBag 389, ContainerSyncPatches
  356, DroppedItems 191); HostWorldReady/_Highest=139; protocol 25;
  dual-deploy Steam+SecondDarkwood.
- Protocol **25** unchanged. Product bump **0.8.64 → 0.8.65**.
- **Deployed md5** `155d095cf59b6fcc30cf1dbe010ac96f` (build = Steam host =
  SecondDarkwood client).
- **Runtime:** code-only until dual-box: upgrade a melee at workbench →
  drop / die / put in crate → peer sees same upgrades (getModdedDamage /
  SaveState.upgrades parity).
- **Batch 31 residuals:** oxygentank_full world-pick fan; WorkbenchOpenLock;
  hotbar 3D; trap mid-lerp; promote auto-Save (F3); night music; Examinable
  examined presentation; mid-dream migration; EventTrigger.fired only if
  playtest proves; timeSeen/modifiers / locationDirections only if playtest
  proves; pre-0.8.65 upgrade trailers absent (legacy apply = no upgrades);
  TradeInventory upgrades only if playtest shows upgraded trader stock.

---

## 0.8.64 — Door kick OpenForce / opener on DoorOpen

Batch 29 (Warexpor fresh dig — InventoryRandom field collapses, ThrownItem /
projectile ownership, door kick/open after scrape, voice-less HelpMessage,
weather particles, chapter/biome, fresh 0.8.6x logs, inventive multi-session).
Protocol **25** unchanged.

- **Door kick OpenForce lost on peers (P1):** `DoorOpen` applied with hardcoded
  metal 30000 / wood 0 and opener = door pos. `DoorState` (real OpenForce +
  opener, incl. AI `openThump` 45000 → `door_hit_run`) then **skipped** because
  `opened` already matched. Peers heard soft `openSound`, wrong hinge kick.
  Wire: `DoorOpenMessage` AvailableBytes trailer OpenForce + OpenerPos; apply
  uses trailer; broadcast prefers `openerTransform` (AI thump) over local
  Player; Physics door apply still snaps body rot/angVel when already open.
- **Dig ranked:**
  1. **Door kick OpenForce / opener on DoorOpen — SHIPPED.**
  2. InventoryRandom upgrades/ammo — ammo+dur+IsRecipe already on
     ContainerStateSync (0.8.62); loot `createItem` never rolls upgrades.
     Skip.
  3. ThrownItem / projectile ownership / damage remaining — host-auth
     SpawnThrownItem + MuteThrownCombat visualOnly; LongevitySec flare remain
     already wired. No new smoking gun. Skip.
  4. Voice-less HelpMessage / examinable — PersonalFlavorHud NearRange +
     Postfix-hide (not Prefix-null) already fixes Hideout1_tutorial_02 NRE.
     Skip.
  5. Weather particles / rain softlock — WeatherSync startRain/stopRain +
     schedule suppress covered; no softlock evidence. Skip.
  6. Chapter load / biome transition — ChapterProgression + LocationEnter
     guards covered; no new file:symbol hole. Skip.
  7. Fresh 0.8.6x logs — host/client logs are **0.8.34** @ 16:02 MSK; no
     0.8.6x smoking gun. Skip.
  8. Inventive multi-session — death-bag/drop upgrades not on wire (personal
     UpgradeItemMenu parked; backup already has Upgrades). Large recipe-
     adjacent theme; not surgical for this batch. Skip.
- **Skipped / parked (unchanged):** WorkbenchOpenLock; hotbar 3D; trap mid-
  lerp; promote auto-Save; night music; oxygentank_full world-pick fan;
  mid-dream migration; Examinable examined presentation; EventTrigger.fired
  bulk; timeSeen/modifiers; journal locationDirections; shared-item upgrades
  on death-bag/drop/container (Batch 30 if playtest proves).
- **Rev1:** DoorOpen OpenForce+Opener trailer; Broadcast openerTransform;
  HandleDoorOpen apply; Physics already-open body snap.
- **Rev2:** PathB 71/71; hubs OK (DoorNetHandlers 154, DreamDoorSyncPatches 193,
  Apply 497); HostWorldReady/_Highest=139; protocol 25; dual-deploy Steam+SecondDarkwood.
- Protocol **25** unchanged. Product bump **0.8.63 → 0.8.64**.
- **Deployed md5** `1c9e4223934300e263127460baeec80b` (build = Steam host = SecondDarkwood client).
- **Runtime:** code-only until dual-box: AI/player thump a closed door → peer
  hears `door_hit_run` and sees matching hinge kick.
- **Batch 30 residuals:** shared InvItem upgrades on death-bag/drop/container;
  oxygentank_full world-pick fan; WorkbenchOpenLock; hotbar 3D; trap mid-lerp;
  promote auto-Save (F3); night music; Examinable examined presentation;
  mid-dream migration; EventTrigger.fired only if playtest proves;
  timeSeen/modifiers / locationDirections only if playtest proves; pre-0.8.64
  DoorOpen packets lack OpenForce trailer (legacy apply = metal/wood fallback).

---

## 0.8.63 — Dropped-item / death-bag recipe wire parity

Batch 28 (Warexpor fresh dig — dropped world IsRecipe, death-bag recipes,
give/throw recipe flag, workbench craft output peer scrap, other InvItem wire
holes, multi-session softlock outside parked). Protocol **25** unchanged.

- **Dropped ground recipes collapsed (P1):** `DroppedItemSpawn` / late-join
  `SyncExistingDroppedItems` sent `InvItemClass.type` (`"recipe"`) with no
  `isRecipe`/`recipeFor`. Peer `new InvItemClass(type)` spawned a junk recipe
  scrap (same collapse as trade/container pre-0.8.62). Wire now carries
  craftable type + `IsRecipe` AvailableBytes trailer; apply uses
  `createItem(..., isRecipe)`.
- **Death-bag recipes collapsed (P1):** `DeathBagDropSyncPatch` + late-join
  `SyncExistingDeathBags` stored `type` only; `HandleDeathBagSpawn`
  `createItem(type, amount)` dropped recipe identity. Per-entry `IsRecipe`
  trailer after `BagId`; ItemTypes = recipeFor when set; apply
  `createItem(..., isRecipe)`.
- **Dig ranked:**
  1. **DroppedItemSpawn IsRecipe collapse — SHIPPED** (SendDrop + late-join sync
     + HandleDroppedItemSpawn createItem).
  2. **DeathBagSpawn IsRecipe collapse — SHIPPED** (dropBody fan + late-join +
     HandleDeathBagSpawn).
  3. Give/throw recipe flag — dialog `giveItem` is personal
     (`DialogApplyPolicy` / suppress on remote); `throwItem` is ThrownItem FX
     (molotov/flare), not inventory recipe stacks. No InvItem recipe wire.
     Skip.
  4. Workbench craft output scrap for peers — `doCraft` only fans
     WorkbenchLevel; craft result stays personal inventory (COOP_COVERAGE
     craftedItems personal). No craft-output InvItem wire. Skip.
  5. Other InvItem wire — Trade/Container/InventoryRandom/ClientStateBackup
     already 0.8.62. No further createItem apply holes found. Skip.
  6. Broader multi-session softlock — no new evidence outside parked list.
     Skip.
- **Skipped / parked (unchanged):** WorkbenchOpenLock; hotbar 3D; trap mid-lerp;
  promote auto-Save; night music; oxygentank_full world-pick fan; mid-dream
  migration; Examinable examined presentation; EventTrigger.fired bulk;
  timeSeen/modifiers; journal locationDirections.
- **Rev1:** DroppedItemSpawn IsRecipe trailer; DeathBagSpawn IsRecipe[] trailer;
  SendDrop / SyncExisting / dropBody / late-join encode recipeFor; apply
  createItem(..., isRecipe).
- **Rev2:** PathB 71/71; hubs OK (CombatDeathBag 383, DroppedItems 190); HostWorldReady/_Highest=139; protocol 25; dual-deploy Steam+SecondDarkwood.
- Protocol **25** unchanged. Product bump **0.8.62 → 0.8.63**.
- **Deployed md5** `151ecdd84eea47718fb8b01a6cfacc1a` (build = Steam host = SecondDarkwood client).
- **Runtime:** code-only until dual-box: drop a recipe on ground → peer sees the
  same craftable recipe; die with a recipe in bag → peer loots the same recipe.
- **Batch 29 residuals:** oxygentank_full world-pick fan; WorkbenchOpenLock;
  hotbar 3D; trap mid-lerp; promote auto-Save (F3); night music; Examinable
  examined presentation; mid-dream migration; EventTrigger.fired only if
  playtest proves; timeSeen/modifiers / locationDirections only if playtest
  proves; pre-0.8.63 DroppedItem/DeathBag packets lack IsRecipe trailer
  (legacy apply = non-recipe).

---

## 0.8.62 — TradeInventory / container recipe + durability parity

Batch 27 (Warexpor fresh dig — locationDirections, expMachine personal, rot/smell/
infection, sleep bed ownership, hideout crates, remaining SaveState/Journal,
night-trader inventory holes, inventive multi-session softlock). Protocol **25**
unchanged.

- **Trader recipe stock collapsed / poisoned (P1):** `TradeInventorySync` keyed
  stacks by `InvItemClass.type`. Vanilla recipes all share type `"recipe"` with
  distinct `recipeFor` — host restock + absolute fan merged every recipe into one
  `"recipe"` stack and `addItemType` dropped `isRecipe`. Client `acceptTrade` →
  host reply then overwrote host stock with the stripped list (poison). Per-stack
  entries now carry craftable type + `IsRecipe` + absolute durability trailer;
  Apply uses `createItem(..., isRecipe)`.
- **Hideout / chest recipe slots lost on ContainerStateSync (P1):** Same
  type=`"recipe"` hole on `SlotStateEntry` / live `ContainerItem`. Build stores
  `recipeFor` + `IsRecipe` trailer; apply / PlaceItem / take validate via
  recipe-aware match. Absolute durability assigned after create (multiplier arg
  stays 1f).
- **ClientStateBackup recipe restore (P2 ride-along):** Restore used
  `createItem("recipe")` then flipped flags; now `createItem(recipeFor, …,
  isRecipe:true)` matching vanilla ctor.
- **Dig ranked:**
  1. **TradeInventory recipe collapse / host poison — SHIPPED** (decompile
     InvItemClass ctor type→`"recipe"` + recipeFor; TradeSync Build/Apply).
  2. **ContainerStateSync / live ContainerItem recipe — SHIPPED** (hideout
     crates / chests / InventoryRandom fan; SlotStateEntry + PlaceItem).
  3. ClientStateBackup recipe createItem — SHIPPED (ride-along).
  4. locationDirections journal — lazy `convertToLocationText` rebuilds from
     WorldGenerator when missing; locationsDict covered 0.8.61. No softlock
     evidence. Skip (parked).
  5. expMachineId / examinedExpMachine — world `ExperienceMachine.enable` via
     HideoutStateSync sets `Player.experienceMachine`. Skip.
  6. rot / smell / infection personal — timeSeen rot-age body still dead
     (`_ = totalTime`); CharacterEffects backed 0.8.60; infection splat host-auth.
     Skip.
  7. Sleep bed ownership / who slept — SleepEndRequest clock sync only; no
     vanilla per-bed SaveState. Skip.
  8. Hideout crates personal vs shared — crates are shared world containers by
     design (COOP_COVERAGE); confusion was recipe wipe (shipped), not personal
     stash.
  9. Remaining SaveState — gotHit/diedAtLeastOnce still no readers; modifiers
     broken SP. Skip.
  10. Night trader inventory remaining — reputation already personal backup;
      stock hole was shared recipe wire (shipped).
- **Skipped / parked (unchanged):** WorkbenchOpenLock; hotbar 3D; trap mid-lerp;
  promote auto-Save; night music; oxygentank_full world-pick fan; mid-dream
  migration; Examinable examined presentation; EventTrigger.fired bulk;
  timeSeen/modifiers; journal locationDirections.
- **Rev1:** TradeInventory IsRecipe/Durability trailer; per-stack Build/Apply;
  SlotStateEntry IsRecipe trailer; ContainerItem IsRecipe; recipe-aware take
  match; ClientStateBackup recipe createItem.
- **Rev2:** TakeSnapshot braces; recipe-aware take/validate match; hubs &lt;500;
  HostWorldReady/_Highest=139; PathB 71/71; dual-deploy Steam+SecondDarkwood.
- Protocol **25** unchanged. Product bump **0.8.61 → 0.8.62**.
- **Deployed md5** `148fdd63d8ce5cd479d61d1d44fb1246` (build = Steam host = SecondDarkwood client).
- **Runtime:** code-only until dual-box NightTrader/TheThree restock shows
  distinct recipes on client + place/take recipe in hideout chest survives sync.
- **Batch 28 residuals:** oxygentank_full world-pick fan; WorkbenchOpenLock;
  hotbar 3D; trap mid-lerp; promote auto-Save (F3); night music; Examinable
  examined presentation; mid-dream migration; EventTrigger.fired only if
  playtest proves trigger-local gate; timeSeen/modifiers / locationDirections
  only if playtest proves; pre-0.8.62 TradeInventory/Container packets lack
  IsRecipe trailer (legacy apply = non-recipe).

---

## 0.8.61 — ClientStateBackup craftedItems + journal known locations

Batch 26 (Warexpor fresh dig — journal notes/quest personal, skills beyond
LessHealth, trader rep, known locations, FOV/settings, death/face, inventory
weight, remaining Player.SaveState vs ClientStateBackup, multi-session brick).
Protocol **25** unchanged.

- **Personal craft counts wiped on cold rejoin (P1):** Vanilla
  `Player.SaveState.craftedItems` / `CraftingRecipes.timesCraftedLimit` is
  personal (COOP_COVERAGE parked wire-sync by design). WorldSaveShare still
  loads the **host** list onto the client body; ClientStateBackup never
  collected/restored it — limited crafts reset to host counts (dupe past limit
  or false lockout). Collect `CraftedEntry[]`; restore clears + reapplies
  (null = pre-0.8.61 skip).
- **Journal known locations missing from live/bulk (P1):** `Location.discoverMe`
  writes `journal.locationsDict` (Locations tab) but Map discovery only fans
  `Map.showElement`. JournalBulkSync omitted locations; cold rejoin / late-join
  dropped peer-discovered place names even when map pins survived. Live
  `JournalItemKind.Location` + bulk `LocationTypes` AvailableBytes trailer.
- **canActivateSkill SaveState parity (P2 ride-along):** Vanilla
  `PlayerSkills.SaveState` persists the active-skill cooldown gate after
  `initialize`; RestoreSkills did not. Collect + restore (legacy JSON defaults
  true — never locks from old backups).
- **Dig ranked:**
  1. **craftedItems ClientStateBackup — SHIPPED** (Player.SaveState +
     CraftingRecipes.reachedMaxNumberOfTimesCrafted; file:symbol Collect /
     Restore.Extras).
  2. **journal locationsDict live+bulk — SHIPPED** (Location.discoverMe;
     JournalBulk LocationTypes trailer; Kind=5). Shared world journal, not
     personal backup.
  3. canActivateSkill — SHIPPED (PlayerSkills.SaveState parity).
  4. Journal notes text / quest progress personal backup — notes/keys/entries
     already JournalItem + JournalBulk (shared by design; text from
     JournalDatabase). No personal ClientStateBackup hole. Skip.
  5. Skills XP/levels beyond LessHealth — skills use `timesUsed` (already
     Collect/Restore); no separate skill XP field. LessHealth unset 0.8.57.
     Skip.
  6. Reputation traders personal — NightTrader/TheThree already backup;
     others ReputationBulk shared. Skip.
  7. Camera FOV / settings shared — FOV is runtime (skills/items), not
     Player.SaveState; GameSettings is local prefs. Skip.
  8. Death count / face custom — `lifes` already backed; `diedAtLeastOnce` /
     `gotHitAtLeastOnce` have **no readers** in decompile (Flags
     `player_diedAtLeastOneTime` is world). No face custom in vanilla. Skip.
  9. Inventory weight / overload — no weight/overload fields in vanilla
     Inventory/Player. Skip.
  10. Remaining Player.SaveState gaps — `rot` cosmetic; `expMachineId` /
      `examinedExpMachine` world ExperienceMachine (host). Skip.
- **Skipped / parked (unchanged):** WorkbenchOpenLock; hotbar 3D; trap mid-lerp;
  promote auto-Save; night music; oxygentank_full world-pick fan; mid-dream
  migration; Examinable examined presentation; EventTrigger.fired bulk;
  timeSeen/modifiers; journal locationDirections (compass hints — no smoking
  gun beyond locationsDict).
- **Rev1:** CraftedEntry Collect/Restore; CanActivateSkill; JournalItemKind.Location;
  discoverMe Postfix; JournalBulk LocationTypes trailer.
- **Rev2:** discoverMe Prefix only fans on first add (no re-broadcast spam);
  hubs &lt;500; HostWorldReady/_Highest=139; PathB 71/71; dual-deploy
  Steam+SecondDarkwood.
- Protocol **25** unchanged. Product bump **0.8.60 → 0.8.61**.
- **Deployed md5** `eda70b4e4306329f8c8d8990c1c5727b` (build = Steam host = SecondDarkwood client).
- **Runtime:** code-only until dual-box cold-rejoin after limited craft + peer
  location discover (journal Locations tab + map pin).
- **Batch 27 residuals:** oxygentank_full world-pick fan; WorkbenchOpenLock;
  hotbar 3D; trap mid-lerp; promote auto-Save (F3); night music; Examinable
  examined presentation; mid-dream migration; EventTrigger.fired only if
  playtest proves trigger-local gate; timeSeen/modifiers / locationDirections
  only if playtest proves; pre-0.8.61 backup JSON lacks CraftedItems until
  next Collect.

---

## 0.8.60 — ClientStateBackup recipes / hotbar select / effects / personal map pins

Batch 25 (Warexpor fresh dig — timeSeen/rot, hotbar selected, clothing visuals,
status effects, reputation/money, map markers personal, Collect/Restore vs
vanilla SaveState, multi-session softlock). Protocol **25** unchanged.

- **Recipes collected but never restored (P1):** `CollectBackupData` mirrored
  vanilla `Player.SaveState.recipes`, but `RestoreFromBackup` never applied them.
  WorldSaveShare loads the **host** character first, so client-learned recipes
  were wiped on cold rejoin / soft-reconnect / migration. Restore now clears and
  re-adds via `ItemsDatabase.getRecipes` + `refreshRecipes` (vanilla loadValues).
- **Hotbar selected index lost on restore (P1):** Inventory.SaveState does not
  persist `InvSlot.selected`; co-op restores onto a host-loaded body whose
  selected flag can disagree with the client's last slot. Collect stores
  `HotbarSelectedSlot`; restore flips selected flags only (does **not** call
  `InvSlot.select()`, which forces `shouldBeActive=true` and would undo
  flashlight-off from 0.8.59). Pre-0.8.60 JSON uses sentinel −1 (skip).
- **Status effects on backup restore (P1):** Vanilla `Player.SaveState.chEffS`
  / `CharacterEffects.SaveState` was never in ClientStateBackup. Bleed, poison,
  hunger, wards, etc. were lost (or host effects left behind) on resume. Collect
  + restore via `effects.activate(...)`; clears host effects first. Skips
  `damage` (instant getHit) and `timeFreeze` (global `DoUpdateTime`; host
  TimeSync owns the clock).
- **Personal map markers (P1 residual):** `LocalMarkers` cleared by NetworkReset;
  MapStateSync only fans host→client **remotes**. Client blue pins vanished on
  cold rejoin. Backup Collect/Restore + re-broadcast so peers see them again.
- **Dig ranked:**
  1. **Recipes Collect→Restore gap — SHIPPED** (decompile Player.SaveState.loadValues).
  2. **HotbarSelectedSlot — SHIPPED** (InvSlot.selected / CoopPlayerBootstrap selectSlot;
     avoid InvSlot.select shouldBeActive force).
  3. **CharacterEffects backup — SHIPPED** (chEffS parity; skip damage/timeFreeze).
  4. **Personal LocalMarkers backup — SHIPPED** (mod-only; NetworkReset + MapStateSync gap).
  5. timeSeen / item rot — vanilla SaveState copies timeSeen, but createInvItemIcon
     rot-age body is dead (`_ = totalTime` / `_ = timeSeen+100`). No concrete MP
     rot-loss path beyond SP. Skip (parked unless playtest proves).
  6. modifiers — vanilla SaveState ctor self-copies empty list (broken SP). Skip.
  7. Equipped clothing visuals on proxy — hotbar 3D / changedClothes parked; no new
     resume-only smoking gun beyond PeerItemPresence.
  8. Reputation / money — NightTrader per-player already Collect/Restore; no player
     money field in vanilla. ReputationBulk covers shared NPC standing. Skip.
  9. Map fog / discoveries — discoveries via MapStateSync + 0.8.53 OutsideLocation;
     personal pins were the remaining gap (shipped).
- **Skipped / parked (unchanged):** WorkbenchOpenLock; hotbar 3D; trap mid-lerp;
  promote auto-Save; night music; oxygentank_full world-pick fan; mid-dream
  migration; Examinable examined presentation; EventTrigger.fired bulk;
  timeSeen/modifiers backup.
- **Rev1:** EffectEntry/MarkerEntry/HotbarSelectedSlot; Collect effects+markers+
  selected; RestoreRecipes/Effects/Markers/ApplyHotbarSelectedSlot.
- **Rev2:** HotbarSelectedSlot −1 legacy sentinel; marker re-broadcast via
  MultiplayerMapManager.RestoreLocalMarkersFromBackup; skip damage/timeFreeze;
  split Restore.Extras.cs (hub &lt;500); Release + PathB 71/71.
- Protocol **25** unchanged. Product bump **0.8.59 → 0.8.60**.
- **Deployed md5** `0cd9643e8574c9e92fde53f0c590afdd` (build = Steam host = SecondDarkwood client).
- **Runtime:** code-only until dual-box cold-rejoin with learned recipe, non-zero
  hotbar select, active bleed/poison, and personal map pins.
- **Batch 26 residuals:** oxygentank_full world-pick fan; WorkbenchOpenLock;
  hotbar 3D; trap mid-lerp; promote auto-Save (F3); night music; Examinable
  examined presentation; mid-dream migration; EventTrigger.fired only if
  playtest proves trigger-local gate; timeSeen/modifiers only if playtest proves
  rot/mod loss; pre-0.8.60 backup JSON lacks HotbarSelectedSlot/ActiveEffects/
  LocalMapMarkers until next Collect.

---

## 0.8.59 — ClientStateBackup item SaveState parity (lights / upgrades / slots)

Batch 24 (Warexpor fresh dig — melee durability / explosives / lights / armor /
keys / day clock / MakeItemEntry vs vanilla SaveState / multi-session brick).
Protocol **25** unchanged.

- **ClientStateBackup MakeItemEntry incomplete vs vanilla SaveState (P1 set):**
  After 0.8.58 ammo parity, Collect still omitted `shouldBeActive`,
  `timeDeactivated`, and workbench `upgrades[]`. Restore packed items via
  `addSlot()` + `getNextFreeSlot()` (ignored `Slot`, grew inv/hotbar by item
  count every restore) and only wrote durability. Flashlight on/off + fuel
  presentation, workbench ItemUpgrade damage/durability mods on melee/armor,
  and hotbar key layout scrambled / slots ballooned across cold rejoin /
  soft-reconnect / migration. Collect now mirrors SaveState fields;
  Restore places at `entry.Slot`, reconciles Hotbar/Inventory upgrade slot
  deltas (no per-item `addSlot`), applies upgrades + `shouldBeActive`, rebinds
  `Player.currentItem` from the selected hotbar slot.
- **Dig ranked:**
  1. **MakeItemEntry / Restore SaveState gap (shouldBeActive, upgrades, Slot,
     upgrade-slot reconcile) — SHIPPED** (decompile `InvItemClass.SaveState` +
     `Inventory.SaveState.loadValues` + `Player.onDoneSwitchingItem` flashlight
     branch; file:symbol Collect.MakeItemEntry / Restore.RestoreItems).
  2. Melee / armor **durability** — already Collect+Restore (`item.durability`);
     no separate mapping bug. Upgrades were the missing combat/armor residual.
  3. Thrown / placed explosives / bear traps inventory — unplaced stacks are
     normal items (type/amount/durability). Placed world traps stay world sync
     (beartrap preserve). No new inventory-state hole.
  4. Light fuel — flashlight fuel **is** durability (already backed). On/off is
     `shouldBeActive` (shipped).
  5. Keyring / door key consumed beyond journal — journal `keysDict` add/remove
     already JournalNetHandlers + JournalSyncPatches. No ClientStateBackup key
     list; door lock is world. Skip.
  6. Time of day / day index after migrate morning — `Day`/`GameTimeMinutes`
     collected for diagnostics only; host **TimeSync** owns the clock. Restoring
     client day would fight TimeSync. Skip.
  7. Systematic MakeItemEntry vs SaveState — remaining vanilla fields:
     `modifiers` (vanilla SaveState ctor self-copies empty list — broken in SP
     too), `timeSeen` (food rot). Not shipped (low / no SP parity).
  8. Inventive multi-session — per-item `addSlot` growth + ignored Slot was the
     brick with code evidence (shipped).
- **Skipped / parked (unchanged):** WorkbenchOpenLock; hotbar 3D; trap mid-lerp;
  promote auto-Save; night music; oxygentank_full world-pick fan; mid-dream
  migration; Examinable examined presentation; EventTrigger.fired bulk;
  `timeSeen` / `modifiers` backup.
- **Rev1:** ItemEntry ShouldBeActive/Upgrades/TimeDeactivated; MakeItemEntry;
  RestoreItems slot-accurate; ReconcileInventoryUpgradeSlots; apply upgrades.
- **Rev2:** RebindCurrentItemFromSelectedHotbar after hotbar restore; List tidy;
  Release build clean.
- Protocol **25** unchanged. Product bump **0.8.58 → 0.8.59**.
- **Deployed md5** `d8680038357066d7fe5766f059b2310b` (build = Steam host = SecondDarkwood client).
- **Runtime:** code-only until dual-box cold-rejoin with flashlight left on,
  workbench-upgraded melee, and gapped hotbar slots.
- **Batch 25 residuals:** oxygentank_full world-pick fan; WorkbenchOpenLock;
  hotbar 3D; trap mid-lerp; promote auto-Save (F3); night music; Examinable
  examined presentation; mid-dream migration; EventTrigger.fired only if
  playtest proves trigger-local gate; timeSeen/modifiers backup only if
  playtest proves rot/mod loss; pre-0.8.59 backup JSON lacks ShouldBeActive/
  Upgrades until next Collect.

---

## 0.8.58 — ClientStateBackup firearm magazine ammo

Batch 23 (Warexpor fresh dig — trader/ammo/rep/map/death/unique/farm/logs/migration). Protocol **25** unchanged.

- **ClientStateBackup hotbar/inv firearm magazine on resume (P1):** `MakeItemEntry`
  stored live `item.amount` (usually 1 for a gun). Vanilla `InvItemClass.SaveState`
  stores magazine rounds in `amount` when `hasAmmo`; `createItem(type, Amount)` maps
  that back to `ammo`. Soft reconnect / cold rejoin / migration restore via
  `RestoreFromBackup` therefore rebuilt guns with ~1 round in the mag — combat
  softlock until reload or spare ammo. Collect now mirrors SaveState
  (`Amount = hasAmmo ? item.ammo : item.amount`). Restore path unchanged
  (`createItem` + durability). Pre-0.8.58 backup JSON still has stack Amount for
  guns until the next successful Collect.
- **Dig ranked:**
  1. **ClientStateBackup firearm ammo-in-Amount — SHIPPED** (decompile SaveState +
     InvItemClass ctor hasAmmo branch; file:symbol Collect.MakeItemEntry).
  2. Trader concurrent buy / stock — COVERED (exclusive NpcDialogueLock + absolute
     TradeInventorySync host fan; restock host-only). refreshReputation NRE on
     title/join already caught. No new race smoking gun.
  3. Reputation thresholds beyond wantsToTalk — COVERED (NPCState is rep/dead/
     wantsToTalk only; ReputationBulk 0.8.55 trailer; FlagBulk for musician_/story
     flags). No separate threshold channel.
  4. Map fog / explored cells — NO system (PosType.fog is height layer; discovery
     is MapElement pins — 0.8.53 OutsideLocation + marker snapshot). Skip.
  5. Corpse / death bag after DeathBagLooted — COVERED (0.8.43 empty fan + defer
     Destroy under open UI; SyncExistingDeathBags skips looted). No new residual.
  6. Plague doctor / mushroom granny / musician — gates are Flag + DialogTree +
     wants/rep (already bulk). No unique MP hole with file:symbol evidence.
  7. Chicken / livestock / farm — CharacterType.Chicken entity path only;
     chicken_egg_red balance list. No MP hole.
  8. Logs — **stale** (host+client banners still **0.8.34** @ ~16:02 MSK; not 0.8.5x).
  9. 3p mid-night host crash → migrate → cold morning — promote auto-Save stays
     parked; ammo backup fix helps survivor restore. No additional CAN-fix brick.
- **Skipped / parked (unchanged):** WorkbenchOpenLock; hotbar 3D; trap mid-lerp;
  promote auto-Save; night music; oxygentank_full world-pick fan; mid-dream
  migration; Examinable examined presentation; EventTrigger.fired bulk.
- **Rev1:** MakeItemEntry hasAmmo → Amount=ammo (vanilla SaveState parity).
- **Rev2:** comment precision; PathB tests; HostWorldReady/_Highest=139; hubs &lt;500;
  dual-deploy Steam+SecondDarkwood.
- Protocol **25** unchanged. Product bump **0.8.57 → 0.8.58**.
- **Deployed md5** `ce60e41cefebe2bc0da39dd9fd03b430` (build = Steam host = SecondDarkwood client).
- **Runtime:** code-only until dual-box cold-rejoin / soft-reconnect with loaded
  firearm magazine parity (backup Collect after fire, then restore).
- **Batch 24 residuals:** oxygentank_full world-pick fan; WorkbenchOpenLock; hotbar 3D;
  trap mid-lerp; promote auto-Save (F3); night music; Examinable examined presentation;
  mid-dream migration; EventTrigger.fired only if playtest proves trigger-local gate;
  pre-0.8.58 backup JSON mag until next Collect.

---

## 0.8.57 — RestoreSkills host LessHealth/MoreHealth unset

Batch 22 (Warexpor multi-session / vitals residual from 0.8.56). Protocol **25** unchanged.

- **Host LessHealth/MoreHealth bleed into client vitals on cold rejoin (P1):** After WorldSaveShare loads the **host** character, `RestoreSkills` mirrored vanilla `loadValues` by clearing `chosen` only, then `initialize` on the client skill list. Host `LessHealth1`/`MoreHealth1` setters had already mutated `maxHealth` (−50 / +25). Clearing `chosen` without unsetting left the host trait on the peer, or double-applied when the client also had the skill (`chosen=false` then `initialize(true)` sets the property again). Unset `LessHealth1`/`MoreHealth1` before client init (after `ReconcileVitalUpgradePools`; upgrade deltas are independent additives). Clamp vitals after skills unchanged.
- **Dig ranked:**
  1. **RestoreSkills host LessHealth/MoreHealth unset — SHIPPED** (Batch 21 residual; decompile `PlayerSkills.LessHealth1`/`MoreHealth1` + `PlayerSkill.initialize`).
  2. Fresh P0/P1 multi-session / unique / night-defense leftovers beyond parked — **none** with file:symbol evidence (boards/vitals covered by 0.8.56 + this unset).
  3. **Regression skim BarricadeSyncHelpers removed-board latch — OK** (Send path + host Handle latch, bulk `SendRemovedBoardsTo` doors/windows, Reset/ClearPending).
  4. **Regression skim ReconcileVitalUpgradePools — OK** (delta ±25, min floor 1, runs before RestoreSkills unset/init).
- **Skipped / parked (unchanged):** WorkbenchOpenLock; hotbar 3D; trap mid-lerp; promote auto-Save; night music; oxygentank_full world-pick fan; mid-dream migration; Examinable examined presentation; EventTrigger.fired bulk.
- **Rev1:** RestoreSkills unset LessHealth1/MoreHealth1 before chosen-clear + client initialize.
- **Rev2:** comment precision (post-Reconcile additive); PathB 71 pass; HostWorldReady/_Highest=139; hubs &lt;500; dual-deploy Steam+SecondDarkwood.
- Protocol **25** unchanged. Product bump **0.8.56 → 0.8.57**.
- **Deployed md5** `be089c9d1ecdc6c30a07d4eba676354d` (build = Steam host = SecondDarkwood client).
- **Runtime:** code-only until dual-box cold-rejoin with host LessHealth or MoreHealth vs peer without (and both-with) maxHealth parity + night board soft-reconnect regression.
- **Batch 23 residuals:** oxygentank_full world-pick fan; WorkbenchOpenLock; hotbar 3D; trap mid-lerp; promote auto-Save (F3); night music; Examinable examined presentation; mid-dream migration; EventTrigger.fired only if playtest proves trigger-local gate.

---

## 0.8.56 — Night board late-join + permanent HP pool restore

Batch 21 (Warexpor multi-session / hideout defense / permanent health focus). Protocol **25** unchanged.

- **Hideout window/door boards late-join & soft-reconnect (P0):** Live `BarricadeEvent` Destroyed fan-out worked while connected, but late-join bulk only scanned *currently* `barricaded` doors/windows. After `destroyBarricade`, vanilla clears `barricaded`/`playerBarricade` with no leftover flag — soft-reconnect / AlreadyInWorld (skips WorldSaveShare) kept stale boards → night defense asymmetry. Host now latches removed board sites (`BarricadeSyncHelpers._removedBoards`, cap 128) on Destroyed/Built and pushes them in `SendBarricadeDoorsTo` / `SendBarricadeWindowsTo` phases 6–7.
- **Permanent health/stamina pool on cold rejoin (P1):** `ClientStateBackup.RestoreFromBackup` assigned `healthUpgrades`/`staminaUpgrades` counts after WorldSaveShare loaded the **host** character, but never delta-adjusted `maxHealth`/`maxStamina` (vanilla `SaveState.loadValues` loops `upgradeHealth`/`upgradeStamina`, +25 each). Peers kept the host max pool. `ReconcileVitalUpgradePools` delta-adjusts; clamp after `RestoreSkills` (LessHealth/MoreHealth setters also touch maxHealth).
- **Dig ranked (no ship / covered / out of scope):**
  1. Save/profile/chapter beyond StableClientKey/SteamId — no new smoking gun (savch share, AlreadyInWorld menu guard, orphan profs already covered 0.8.50–51).
  2. Crafted furniture / Constructible — live+bulk+pending intact.
  3. Infection splat — EntitySpawn bulk phase 9 intact; player `gassed` is local stamina (not plague).
  4. Motorcycle — wiki = Piotrek tractor parts (personal inv), not a vehicle; flamethrower is firearm muzzle path.
  5. Swamp/ch2 — oxygen softlock still parked (no new proof beyond hotbar PeerItemPresence); compressor covered.
  6. Cord/rope/climb/ladder — no climb/ladder systems in decompile (top-down); rope/cable are craft/trade items.
  7. Smoke/gas/flamethrower world — gas trail/ignite/burn bulk intact; oven smoke is ExperienceMachine particle.
- **Skipped / parked (unchanged):** WorkbenchOpenLock; hotbar 3D; trap mid-lerp; promote auto-Save; night music; oxygentank_full world-pick fan; mid-dream migration; Examinable examined presentation; EventTrigger.fired bulk; skill LessHealth unset-on-restore (pre-existing RestoreSkills host-flag residual → Batch 22).
- **Rev1:** removed-board registry + restore reconcile.
- **Rev2:** drop dangerous maxHealth=100 floor (LessHealth-safe min 1); clamp vitals after RestoreSkills; hubs &lt;500; HostWorldReady/_Highest=139; PathB 71 pass; dual-deploy Steam+SecondDarkwood.
- Protocol **25** unchanged. Product bump **0.8.55 → 0.8.56**.
- **Deployed md5** `ee62f0b484f5241b4914879e03d52a32` (build = Steam host = SecondDarkwood client).
- **Runtime:** code-only until dual-box night board soft-reconnect + cold-rejoin HP upgrade playtest.
- **Batch 22 residuals:** RestoreSkills host LessHealth/MoreHealth unset → shipped in **0.8.57**; remaining parked: oxygentank_full world-pick fan; WorkbenchOpenLock; hotbar 3D; trap mid-lerp; promote auto-Save (F3); night music; Examinable examined presentation; mid-dream migration; EventTrigger.fired only if playtest proves trigger-local gate.

---

## Batch 20 dig — no-ship (stay 0.8.55)


Adversarial N-peer dig; protocol **25** / HostWorldReady **139** unchanged. **No product bump, no redeploy.**

### Dig ranked
1. **`EventTrigger.fired` / `firedExit` dedicated bulk — COVERED, no CAN-fix.**
   Decompile `EventTrigger.fire` latches `fired` then only calls `gameEvents.fire()`
   (+ optional `RemovePooledPrefab`). `fireExit` only calls `gameEventsExit.fire()`.
   `EventTriggerRequirement.Type.gameEventsFired` reads **`GameEvents.fired`**, not
   `EventTrigger.fired`. Late-join already sends `FlagBulk` + `GameEventsBulk` (**136**,
   heavy phase 11, `fired && !multipleFire` + destroyOnFire identities). Client one-shot
   `GameEvents.fire` blocked by `GameEventsFiredPatch` Prefix (`NetworkApplyGuard` apply
   path exempt). Joiner may re-enter volume with local `EventTrigger.fired=false`, but
   GE side effects do not re-run. Adding `EventTriggerBulk` would not fix resolve misses
   and could worsen retry-after-miss. Remains parked per `COOP_COVERAGE.md`.
2. **Other story one-shot / flag / trigger softlocks — no new smoking gun.**
   FlagBulk + live FlagSync, ScenarioStateBulk **138**, DialogTree bulk + close fan-out,
   ReputationBulk wants trailer (0.8.55), GE live **65** + bulk **136** cover story gates.
   Skip-list items unchanged (WorkbenchOpenLock / hotbar 3D / trap mid-lerp / promote
   auto-Save / night music / oxygentank without softlock proof / mid-dream migration /
   Examinable examined presentation-only).
3. **Regression skim 0.8.55 wantsToTalk:** ReputationBulk end-trailer `WantsToTalk[]`
   (AvailableBytes-safe) + apply; `DialogTreeSync.FindNpcForDialogue` + non-default
   wants refresh on bulk — present and preserved. Deployed md5
   `3db37cc9f3db387c292498143c65cce7` Steam=SecondDarkwood.
4. **Other P0/P1 resume/unique/story:** none with file:symbol evidence beyond parked list.

### Shipped / skipped
- **Shipped:** none (zero safe CAN-fixes).
- **Skipped / parked:** EventTrigger.fired bulk; Examinable examined late-join bulk;
  oxygentank_full world-pick fan; WorkbenchOpenLock; hotbar 3D; trap mid-lerp; promote
  auto-Save; night music; mid-dream host migration.

### Rev / deploy
- No rev1/rev2 (no code). Stay **0.8.55**. Dual-deploy skipped.

### Retest (unchanged from 0.8.55)
- Doctor/Wolf soft-reconnect talkTo; DialogTree late-join; volume one-shot after host
  fired (peer must not softlock — GE bulk latch).

### Batch 21 residuals
- oxygentank_full world-pick fan if concrete softlock beyond hotbar;
  WorkbenchOpenLock; hotbar 3D; trap mid-lerp; promote auto-Save (F3 reminder);
  night music cosmetic on cold rejoin; Examinable examined late-join bulk (presentation);
  mid-dream host migration; EventTrigger.fired bulk only if playtest proves trigger-local
  state (not GE) gates progression without volume re-entry.

---

## 0.8.55 — Doctor/Wolf wantsToTalk late-join softlock

Batch 19 (Warexpor multi-session / unique / story focus). Protocol **25** unchanged.

- **Doctor / Wolf / story NPC `wantsToTalk` late-join & soft reconnect (P0):**
  `NPC.talkTo()` early-outs when `!wantsToTalk`. Soft-reconnect / AlreadyInWorld skips
  WorldSaveShare and only runs late-join bulk. `ReputationBulkSync` synced rep+dead but
  not wants; `DialogTreeSync.SendBulkTo` encoded progressed trees with `npc: null` and
  skipped host-default wants=true — so a peer SP save with wants=false could not talk
  to Doctor/Wolf after host re-enabled story talk. ReputationBulk now trails
  `WantsToTalk[]` (AvailableBytes-safe end trailer); apply writes host wants for all NPCs.
  DialogTree bulk attaches live NPC on progressed trees and re-sends non-default wants
  even when the tree already shipped.
- **Dig skipped / presentation / parked:** Examinable `examined` late-join bulk —
  presentation/cursor only (`examined` never gates GE; onExamine host-auth + GameEventsBulk
  136 covers story; CustomCursorAction still reachable after local examine). Mid-dream
  host migration — no surgical safe path (refuse + disconnect without GRANT; dream session
  not migratable). oxygentank_full / WorkbenchOpenLock / hotbar 3D / trap mid-lerp /
  promote auto-Save / night music parked per Batch 19 skip list.
- **Regression skim 0.8.54:** `_unionLvlFlags` + `ReassertLocalLvlFlags` +
  `ReadUnionLvlFlags` outbound; deadline `ClearRemoteInDream` in
  `UnfreezeProxiesAfterDelay` — present and preserved.
- **Rev1:** ReputationBulk wants trailer + apply; DialogTree FindNpcForDialogue +
  non-default wants refresh.
- **Rev2:** end-of-message wants trailer (not per-entry AvailableBytes); hubs &lt;500;
  HostWorldReady/_Highest=139; PathB 71 pass; dual-deploy Steam+SecondDarkwood.
- Protocol **25** unchanged. Product bump **0.8.54 → 0.8.55**.
- **Deployed md5** 3db37cc9f3db387c292498143c65cce7 (build = Steam host = SecondDarkwood client).
- **Runtime:** code-only until dual-box Doctor/Wolf late-join talkTo + soft-reconnect playtest.
- **Batch 20 dig (no-ship):** EventTrigger.fired bulk confirmed covered by GE bulk 136 +
  FlagBulk + client one-shot Prefix — see section above. Residuals roll to Batch 21
  (oxygentank / WorkbenchOpenLock / hotbar 3D / trap mid-lerp / promote auto-Save /
  night music / Examinable examined presentation / mid-dream migration; EventTrigger.fired
  only if playtest proves trigger-local gate).

---

## 0.8.54 — Skill-dream party-once lvl flags + dream stamp cleanup

Batch 18 (Warexpor multi-session / unique / story focus). Protocol **25** unchanged.

- **Skills / traits party-once harden (P1):** `SkillsMenuDreamPartyOncePatch` claimed
  bunker/random party-once but only forced `hadDreamAtLvl2` from bunker completion.
  Random lvl 3/5/6/7 could re-fire when `Dreams.Instance` flags lagged the session
  snapshot (late join / cold resume). `DreamSession` now keeps a `_unionLvlFlags`
  OR across Apply/End/Read; confirmSkills calls `ReassertLocalLvlFlags()`; outbound
  DreamStarted/Ended/Bulk/WriteSnapshot send the union.
- **Dream entry stamp cleanup (P1 N-peer):** Host `NoteRemoteInDream` stamps all
  peers at start (party-once). After the 10s entry deadline, `UnfreezeProxiesAfterDelay`
  now `ClearRemoteInDream` for peers who never `DreamEntered` (matches
  `IsRemoteInDream` post-deadline). Late `DreamEntered` still Confirms.
- **Dig skipped / already covered:** dream enter/exit/spirit sticky/death tracking
  (0.8.19–0.8.39) intact; chapter share + fallback (0.8.15/0.8.26) + map pins (0.8.53);
  well repair-only InteractiveItem; generator/saw FuelDelta (0.8.46) — no non-fuel
  abs last-writer; photo InvItems via PeerItemPresence + unique claim (no new softlock
  proof); map Discoveries pending + hideout oven 1.5f/pending regression skim OK;
  oxygentank_full / WorkbenchOpenLock / hotbar 3D / trap mid-lerp / promote auto-Save /
  night music parked per Batch 18 skip list.
- **Rev1:** union lvl flags + ReassertLocalLvlFlags; deadline ClearRemoteInDream.
- **Rev2:** outbound snapshots use ReadUnionLvlFlags; WriteSnapshot aligned; hubs &lt;500;
  HostWorldReady/_Highest=139; PathB 71 pass; dual-deploy Steam+SecondDarkwood.
- Protocol **25** unchanged. Product bump **0.8.53 → 0.8.54**.
- **Deployed md5** `341c0ea3933e42d70445d295ce3c04f8` (build = Steam host = SecondDarkwood client).
- **Runtime:** code-only until dual-box skill-confirm party-once + dream stamp playtest.
- **Batch 19 residuals:** oxygentank_full world-pick fan if concrete softlock beyond hotbar;
  WorkbenchOpenLock; hotbar 3D; trap mid-lerp; promote auto-Save still disabled (F3 reminder);
  night music cosmetic on cold rejoin; doctor/wolf story-gate playtest if dialog tree gap found;
  Examinable examined late-join bulk (presentation; onExamine host-auth + GE bulk covers story);
  mid-dream host migration still parked.

---

## 0.8.53 — Map discovery OutsideLocation + hideout oven pending

Batch 17 (Warexpor multi-session / unique / map-unlock focus). Protocol **25** unchanged.

- **Map / biome pin unlock late-join & cold resume (P0):** `Map.showElement(string)` only
  searches `getCurrentType()` (WorldGrid / OutsideLocation). World discoveries (Silent Forest /
  Old Woods hideouts, map-item reveals, dialogue mark-on-map) were dropped while a peer was in
  a bunker / village / doctor house, and MapStateSync had no pending when MapElements were not
  spawned yet. Apply now resolves `MapElement` by scene scan (all types) → `showElement(MapElement)`;
  pending queue + tick flush; MapStateSync queues when `!ClientCanApplyWorldBulk`.
- **Hideout oven permanent state holes (P1):** live `HideoutUpgrade` FindNearest **0.5f** missed
  ovens (StateSync used 1f); no pending when oven missing. Radius **1.5f** + pending flush;
  `HideoutStateSync` queues when not in-world / no machines / partial match.
- **Dig skipped / already covered:** reputation + DialogTree + FlagBulk for doctor/wolf/story
  gates (no new softlock patch evidence); workbench level live+bulk intact; generator late-join
  `SyncExistingGeneratorsTo` intact; oxygentank_full world-pick fan (empty+convert + hotbar
  PeerItemPresence still covers; no new softlock beyond 0.8.52); WorkbenchOpenLock / hotbar 3D /
  trap mid-lerp / promote auto-Save parked; night music cosmetic skip.
- **Regression skim 0.8.52:** prologue `HostIsPastPrologue` + ApplyEnd guard + bulk catch-up;
  PeerItemPresence hotbar combine + `addItemType(string,int)` — present and preserved.
- **Rev1:** MapElement scene-resolve + pending discoveries; HideoutUpgrade/State pending + radius.
- **Rev2:** HideoutStateSync keep-pending on partial oven match; MapElement SceneScanCache
  invalidate on miss during discovery apply; hubs &lt;500; HostWorldReady/_Highest=139;
  PathB 71 pass; dual-deploy Steam+SecondDarkwood.
- Protocol **25** unchanged. Product bump **0.8.52 → 0.8.53**.
- **Deployed md5** 808178d90968a41a917661f194331e60 (build = Steam host = SecondDarkwood client).
- **Runtime:** code-only until dual-box OutsideLocation map-pin + hideout oven late-join playtest.
- **Batch 18 residuals:** oxygentank_full world-pick fan if concrete softlock beyond hotbar;
  WorkbenchOpenLock; hotbar 3D; trap mid-lerp; promote auto-Save still disabled (F3 reminder);
  night music cosmetic on cold rejoin; doctor/wolf story-gate playtest if dialog tree gap found.

---

## 0.8.52 — Prologue cold catch-up + hotbar PeerItemPresence

Batch 16 (Warexpor multi-session / unique focus). Protocol **25** unchanged.

- **Prologue catch-up harden (P0 multi-session):** Soft-reconnect only called
  `PrologueSync.SendCatchUpTo`. Cold host restart cleared `_sessionHadPrologue` →
  stuck peer (`forbidInputs` / `playingIntro`) never got `ActionPrologueEnd`.
  `HostIsPastPrologue()` (loaded, `!firstPlay`, `!playingIntro`) now sends End;
  `ApplyEnd` no-ops when the peer is already past intro (no day-N blackScreen flash).
  Catch-up also on phase-1 share handshake + late-join bulk belt.
- **PeerItemPresence hotbar (P1 unique softlock):** `Inventory.getItemInPlayer` /
  bag-only presence missed Hotbar (wiki oxygen-tank softlock; keys/quest on hotbar).
  Combined inv+hotbar counts; `SendFullLocalInventory` scans Hotbar; Harmony on
  `Inventory.addItemType(string,int)` when target is local Hotbar (compressor path).
- **Dig skipped / already covered:** night TimeSync + HideoutStateSync + ScenarioStateBulk
  (siren = nightComing msg via day-chain host-only; no new hole); vendor TradeInventory
  heavy late-join; StableClientKey / graceful-leave (0.8.51) no remaining brick;
  oxygentank_full world-pick fan (empty+convert still covers; no new softlock proof
  beyond hotbar presence); WorkbenchOpenLock / hotbar 3D / trap mid-lerp / promote
  auto-Save parked.
- **Rev1:** prologue HostIsPastPrologue + ApplyEnd guard + bulk catch-up; PeerItemPresence
  hotbar combine + patches.
- **Rev2:** Harmony `addItemType(string,int)` exact; phase-1 handshake catch-up;
  hubs &lt;500 (logic); HostWorldReady/_Highest=139; PathB 71 pass; dual-deploy.
- Protocol **25** unchanged. Product bump **0.8.51 → 0.8.52**.
- **Deployed md5** `7af954d9919df5930cc9e44e0a50bcce` (build = Steam host = SecondDarkwood client).
- **Runtime:** code-only until dual-box prologue cold-stuck + hotbar haveItem playtest.
- **Batch 17 residuals:** oxygentank_full world-pick fan if concrete softlock beyond
  hotbar presence; WorkbenchOpenLock; hotbar 3D; trap mid-lerp; promote auto-Save
  still disabled (F3 reminder only); night music cosmetic on cold rejoin.

---

## 0.8.51 — LAN StableClientKey backup + graceful-leave host checkpoint

Batch 15 (LAN-without-Steam backup id + migration survivor ownership). Protocol **25** unchanged.

- **LAN StableClientKey host backup (P0):** Pure LAN / Steam+SecondDarkwood cold rejoin
  reshuffled `PlayerId` → host pushed the wrong `client_backup_p{N}` (or none). SteamId
  (0.8.50) covers SNS only. New install-scoped `dwmp_lan_client_key.txt` GUID stamped into
  `ClientStateBackupData.StableClientKey` + Handshake trailing string (AvailableBytes-safe;
  **no new msg id**). Host disk `client_backup_k{key}_{campaign}.json`. Cold push without
  SteamId/StableClientKey skips PlayerId fallback (anti false-merge); soft-reconnect still
  allows pN. Local-self remains primary restore.
- **Graceful host-leave world checkpoint (P1 migration ownership):**
  `GracefulHostLeaveReleasePortThenStop` set `_role = Offline` *before* `StopNetwork`, so
  `TryHostWorldSaveCheckpointOnExit` (Role==Host gate) no-op'd — next cold start of the
  old host slot missed mid-session ownership. Now checkpoints **while still Host**, then
  releases port. Promote auto-Save stays **disabled** (survivor sav corruption); survivor
  gets F3 reminder HUD instead.
- **Dig skipped:** oxygentank_full world-pick fan (empty+convert still covers softlock);
  WorkbenchOpenLock / hotbar 3D / trap mid-lerp parked; promote auto-Save not re-enabled.
- **Regression skim 0.8.50:** SteamId Collect/Paths/SaveNetHandlers + intentional
  StopNetwork host checkpoint — present and preserved.
- **Rev1:** StableClientKey mint+handshake+Paths+push gate; graceful-leave checkpoint;
  promote F3 reminder.
- **Rev2:** clean — NotifyPromotedHostSaveReminder method present; hubs &lt;500;
  HostWorldReady/_Highest=139; PathB 71 pass; dual-deploy Steam+SecondDarkwood.
- Protocol **25** unchanged. Product bump **0.8.50 → 0.8.51**.
- **Deployed md5** `831221c775fd3b8573c788379e69b753` (build = Steam host = SecondDarkwood client).
- **Runtime:** code-only until dual-box LAN cold-rejoin backup + graceful-leave→promote
  cold-start playtest.
- **Batch 16 residuals:** oxygentank_full world-pick fan if concrete softlock; WorkbenchOpenLock;
  hotbar 3D; trap mid-lerp; migration promote Save still disabled (F3 reminder only).

---

## 0.8.50 — ClientBackup SteamId key + host-leave world checkpoint

Batch 14 (Warexpor multi-session + unique priorities). Protocol **25** unchanged.

- **ClientBackup SteamID64 disk key (P0 residual):** Host stored backups were
  `client_backup_p{PlayerId}_{campaign}.json`. PlayerId reshuffles on cold sessions →
  wrong inventory restore / stuck items. Steam sessions (and any payload that stamps
  SteamId) now save/load `client_backup_s{SteamId64}_{campaign}.json`. Collect stamps
  `ClientStateBackupData.SteamId`; host save uses `CurrentReceiveSteamId64` then JSON
  field; late-join push resolves via `TryGetSteamIdForPlayer`. Legacy PlayerId files
  still load and one-shot migrate to the Steam key. LAN-without-Steam keeps PlayerId
  paths; client local-self remains primary fallback (0.8.49).
- **Host intentional quit world checkpoint (P1):** `StopNetwork` while host is in-world
  flushes `sav.dat` (local Save, no SaveSync fan-out) so the next session loads current
  world ownership. Migration promote auto-Save stays **disabled** (survivor client Save
  corrupts the slot — HostMigration.Handoff.Promote).
- **Dig skipped / already covered:** oxygentank_full world-pick fan (empty+convert covers
  softlock); journal/GUID/non-GUID unique claim (0.8.47–49); tutorial/story without new
  patch evidence; WorkbenchOpenLock / hotbar 3D / trap mid-lerp parked.
- **Regression skim 0.8.49:** GUID claim + `ClientReportsAlreadyInWorld` mainMenu gate +
  `PeerItemPresence.ClearPlayer` on disconnect — intact.
- **Rev1:** SteamId Collect stamp + Paths save/load + SaveNetHandlers wire + host-leave
  checkpoint; build clean; HostWorldReady/_Highest=139; PathB 71 pass; hubs &lt;500.
- **Rev2:** no further churn (rev1 dual-deploy clean).
- Protocol **25** unchanged. Product bump **0.8.49 → 0.8.50**.
- **Deployed md5** `52a3734ab5bbd11828048373c9150b57` (build = Steam host = SecondDarkwood client).
- **Runtime:** code-only until dual-box Steam cold-rejoin backup + host-quit save playtest.
- **Batch 15 → 0.8.51:** LAN StableClientKey + graceful-leave host checkpoint + promote F3
  reminder shipped. Residuals: oxygentank_full world-pick fan; WorkbenchOpenLock; hotbar 3D;
  trap mid-lerp; promote auto-Save still disabled.

---

## 0.8.49 — GUID drop host-auth + cold-rejoin AlreadyInWorld gate

Batch 13 (Warexpor priorities: multi-session resume + unique/GUID + tutorial dig). Protocol **25** unchanged.

- **GUID DroppedItemPickup same-frame host-auth (P0 residual from 0.8.48):** Player-dropped
  GUID items still used optimistic Broadcast pickup — same-frame cross-machine dual-grant.
  Now mirrors 0.8.48 WOR claim: capture slot meta → vanilla transfer → on success host
  `TryConsumeDropGuid` + fan Remove(`ClaimedBy`); client optimistic + `ModeClaimRequest`;
  loser `ModeClaimDeny` / Remove-with-other ClaimedBy → pre-count surplus refund.
  `DroppedItemPickupMessage` Always-on trailer (Mode/ClaimedBy/ItemType/Amount/Dur/Ammo);
  AvailableBytes-safe; **no new msg id** (`HostWorldReady`/`_Highest` stay **139**).
  ClaimRequest sets `_suppressForwardThisMessage` (Forwardable must not fan requests).
- **Cold-rejoin AlreadyInWorld false-positive (P0 resume):** `ClientReportsAlreadyInWorld`
  treated lingering `Core.loadedGame` (and profile) as in-world even on **main menu** after
  quit — host skipped world share → brick on 3-friend ALL-quit → return same world.
  Hard gate: `Core.mainMenu` → false; phase-2/3 paths unchanged.
- **PeerItemPresence ghost after disconnect (P1):** host `haveItem` OR kept departed peer
  bag presence until full network Reset. `ClearPlayer` on host peer disconnect (LAN+Steam).
- **Dig skipped / already covered:** oxygen empty fan + compressor convert (CompressorSync);
  journal key/quest share + InvItem destroy (0.8.47–48); non-GUID world unique claim (0.8.48);
  HostWorldReady wait vs soft-reconnect (AlreadyInWorld skip share); client local-self backup
  primary on cold rejoin (host push still PlayerId-keyed — SteamId residual);
  tutorial HelpMessage NearRange (0.8.32+); WorkbenchOpenLock / hotbar 3D / trap mid-lerp parked.
- **Rev1:** GUID claim + AlreadyInWorld gate + PeerItemPresence clear; hub line test failed
  (PlayerFX 501 / DroppedItemSyncPatches 539).
- **Rev2:** split `PlayerFXNetHandlers.DroppedItems.cs` + `DroppedItemSyncHelpers.cs`;
  Apply hubs &lt;500; HostWorldReady/_Highest=139; NetMessage contract tests pass (71);
  dual-deploy Steam+SecondDarkwood.
- Protocol **25** unchanged. Product bump **0.8.48 → 0.8.49**.
- **Deployed md5** `bb0038120c18cec2e8dc9ca10737e92f` (build = Steam host = SecondDarkwood client).
- **Runtime:** code-only until dual-box same-frame GUID drop + cold-rejoin playtest.
- **Batch 14 residuals → 0.8.50:** SteamId ClientBackup key + host-leave world checkpoint
  shipped; migration promote Save still disabled; tutorial/oxygen-full/Workbench/hotbar/trap
  parked.

---

## 0.8.48 — Host-auth world pickup claim (same-frame dual-grant)

Batch 12 (host-auth world pickup + residuals dig). Protocol **25** unchanged.

- **Host-auth world unique pickup (P0 residual from 0.8.47):** Non-GUID
  `getDroppedItem` was optimistic broadcast `WorldObjectRemoved` — same-frame
  cross-machine dual-grant. Now: capture slot meta → vanilla transfer → on
  success host `TryConsume` + fan Remove(`ClaimedBy`); client optimistic +
  `ModeClaimRequest` to host; loser gets `ModeClaimDeny` / Remove-with-other
  ClaimedBy → pre-count surplus refund (container deny parity). WOR trailer
  always-on (Mode/ClaimedBy/ItemType/Amount/Dur/Ammo); AvailableBytes-safe;
  **no new msg id** (`HostWorldReady`/`_Highest` stay **139**).
- **Wire guard:** `WorldPickupWireGuard` during `getDroppedItem` suppresses
  `ObjectDestroyTrapPatch` WOR so harvestable Destroy cannot beat host-auth
  with Mode0 Remove. Traps/GUID drops unchanged (SendPickup path).
- **Prefix return-false guard End:** trap guard cleared when Prefix denies
  (Harmony skips Postfix on `return false`).
- **Dig skipped (no smoking gun / parked):** craft result double-grant
  (`craftedItems` personal; workbench lock parked); tree/chop beyond harvest
  WOR; liquid pour beyond gen/saw FuelDelta; bed/sleep (SleepEndRequest);
  photo/examinable host onExamine; doctor/wolf trade absolute stock; GUID
  DroppedItemPickup same-frame (Batch 13); WorkbenchOpenLock; hotbar 3D;
  trap/door mid-lerp.
- **Regression skim 0.8.47:** InvItem-only journal destroy + `dontDestroy`
  honor + world consume Prefix — present.
- **LogOutput:** still **stale 0.8.34** (DLL was 0.8.47; no fresh playtest log).
- Preserves: protocol 25, HostWorldReady 139, beartrap, indoor reverb,
  CoopWorldPresencePolicy, 0.8.37–0.8.47 work.
- **Rev1:** Destroy-path WOR suppressed during pickup; Capture uses slot
  `InvItemClass` (not `Item.invItem` template).
- **Rev2:** clean — Prefix `return false` Ends trap guard; Apply hubs &lt;500;
  HostWorldReady/_Highest=139; NetMessage contract tests pass (71); dual-deploy
  Steam+SecondDarkwood.
- Protocol **25** unchanged. Product bump **0.8.47 → 0.8.48**.
- **Deployed md5** `c138c4bcbe20ef1a18752f7720ff97fb` (build = Steam host = SecondDarkwood client).
- **Runtime:** code-only until dual-box same-frame world unique pickup playtest.
- **Batch 13 residuals (Warexpor priorities):** multi-session resume (3-friend
  new world → prologue → early days → night → all quit host-left → return same
  world: save/share, HostWorldReady, client backup, stuck peer inv, cold rejoin
  vs soft-reconnect, world ownership); unique/limited item softlocks (wiki+code
  1–2 instance keys/tanks/uniques — share/duplicate/host-grant); tutorial/tank/
  story patches; GUID DroppedItemPickup same-frame host-auth (mirror this batch).

---

## 0.8.47 — Journal InvItem destroy + world unique pickup claim

Batch 11 (quest/key dual-pickup + absolute last-writer scan). Protocol **25** unchanged.

- **InvItem-only key/note destroy (P0):** `DestroyWorldJournalObject` only destroyed
  Key/Note when `GetComponent<Item>()` was present. InvItem-only scene keys/notes
  survived peer `JournalItem` and stayed dual-pickable. Now destroys scene-valid
  journal refs (prefer Item root; else reference GO) — QuestItem parity.
- **Already-claimed journal pickup Prefix (P1):** `KeyReference` / `QuestItemReference`
  / `JournalNoteReference.pickup` Prefix: if type already in local journal (peer
  JournalItem arrived first), destroy world GO and skip vanilla + wire (no second
  popup / no redundant fan-out).
- **Non-GUID world unique pickup claim (P1):** `getDroppedItem` without
  `DroppedItemIdentifier` had no consume set (GUID drops already did). Session
  `TryConsumeWorldPickup` on send + inbound `WorldObjectRemoved`; Prefix denies
  grant when already consumed. Closes sequential dual-grant; true same-frame
  cross-machine race remains residual (needs host-auth request).
- **Absolute last-writer scan:** only `Generator`/`Saw` have `addFuel` /
  `waitToSpillLiquid`. Wells (repair-only InteractiveItem), barrels, stoves, radio
  — no identical concurrent Fuel/uses race. No FuelDelta clone.
- **Regression skim 0.8.46:** Generator FuelDelta host-accum + Saw
  `BroadcastAbsoluteFromHost` / `_suppressForwardThisMessage` — present.
- **Skipped / watchlist:** WorkbenchOpenLock (parked); hotbar 3D mesh; trap/door
  mid-lerp (no smoking gun); true same-frame world InvItem dupe (host-auth pickup
  request); LogOutput still **stale 0.8.34** (DLL was 0.8.46; no fresh playtest log).
- Preserves: protocol 25, HostWorldReady 139, beartrap, indoor reverb,
  CoopWorldPresencePolicy, 0.8.37–0.8.46 work.
- **Rev1:** DestroyJournalWorldGo scene-valid + Item-root prefer; journal Prefix
  `__state` gates Postfix; world pickup claim skips traps.
- **Rev2:** clean — Note `dontDestroy` honored (Prefix + DestroyWorld); Apply.cs
  474&lt;500; HostWorldReady/_Highest=139 unchanged; NetMessage contract tests pass
  (71); dual-deploy Steam+SecondDarkwood.
- Protocol **25** unchanged. Product bump **0.8.46 → 0.8.47**.
- **Deployed md5** `0c522616fe2298cad7e94d3f37dbd91a` (build = Steam host = SecondDarkwood client).
- **Runtime:** code-only until dual-box journal key + world unique pickup playtest.

---

## 0.8.46 — Generator/saw host-auth fuel delta (concurrent pour underfuel)

Batch 10 (generators/fuel host-auth + residuals dig). Protocol **25** unchanged.

- **Generator concurrent addFuel underfuel (P0):** `waitToSpillLiquid` pours `addFuel(1)` every
  0.07s; `GeneratorAddFuelPatch` broadcast absolute `GeneratorState.Fuel` and host
  `ApplyGeneratorState` last-writer absolute → dual pour leaves tank short of combined can
  spend. Client pours now send **FuelDelta** (always-on wire field, same-DLL dual deploy);
  host accumulates via `gen.addFuel(delta)`, mutates fan-out to absolute `FuelDelta=0`, and
  **Broadcast** (includes pourer) so clamp/concurrent sum converges on originator. Host pours
  / turnOn / late-join stay absolute (`FuelDelta=0`).
- **Saw concurrent addFuel underfuel (P0, same race):** `SawState` Forwardable absolute last-writer.
  Client `FuelDelta`; host applies delta, `_suppressForwardThisMessage`, rebroadcasts absolute
  (bypasses `IsApplyingRemoteState` send guard). Convert path unchanged (absolute).
- **Skipped / watchlist:** WorkbenchOpenLock (parked); hotbar 3D mesh; trap/door mid-lerp (no
  smoking gun); unique quest/key beyond journal + `DestroyWorldJournalObject` (no new dual-pickup
  race); monologue non-`initiateDialogue` (vanilla `openDialogue` GE → `initiateDialogue` only);
  night siren/scenario/scent (no new concrete hole); ammo/reload (no double-apply evidence);
  LogOutput still **stale 0.8.34** (DLL was 0.8.45; no fresh playtest log).
- **Regression skim 0.8.45:** place-deny refund (`ModePlaceRefund`), dialog-lock
  `DialogHostApplyGuard.Active` release skip, ET exit `HasAny` — present.
- Preserves: protocol 25, HostWorldReady 139, beartrap, indoor reverb, CoopWorldPresencePolicy,
  0.8.37–0.8.45 work.
- **Rev1:** FuelDelta always serialized (not AvailableBytes trailer — GeneratorState lives in
  PhysicsState arrays); pure-gen fan-out ReliableOrdered Broadcast; saw suppress+rebroadcast.
- **Rev2:** clean — Apply.cs 474&lt;500; Saw suppress+BroadcastAbsoluteFromHost; pure-gen
  fan-out includes pourer (ReliableOrdered); HostWorldReady/_Highest=139 unchanged; NetMessage
  contract tests pass; dual-deploy Steam+SecondDarkwood.
- Protocol **25** unchanged. Product bump **0.8.45 → 0.8.46**.
- **Deployed md5** `378b235fd72c2a3f8109c51b016f6596` (build = Steam host = SecondDarkwood client).
- **Runtime:** code-only until dual-box concurrent pour playtest (gen + saw).

---

## 0.8.45 — Container place-deny refund + dialog-lock world-only belt + ET exit occupancy

Batch 9 (inventory/ammo/generators/dialogue/night dig). Protocol **25** unchanged.

- **Container PlaceItem race vanishes item (P0):** Host denied type-clash / bad-amount /
  stack-overflow places with `_suppressForward` only — no refund. Placer already removed
  the item from their bag and kept it only in the local container → item vanish. Reuses
  msg **115** `ContainerTakeDenied` with Mode trailer (`0` take remove / `1` place restore)
  + Durability/Ammo; host snaps container. Legacy 0.8.44 packets still deserialize (AvailableBytes).
  `MarkContainerSlotPlayerPlaced` only after a successful place.
- **NpcDialogueLock released by world-only SilentClose (P1):** `NpcDialogueLockReleasePatch`
  on `DialogueWindow.close` had no `DialogHostApplyGuard.Active` check. Harmony Prefix order
  vs `DialogHostSilentClosePatch` is undefined → host world-only close could `HostRelease(localId)`
  and drop the host's real talk lease mid-conversation (dual open / stuck lock). Guard skips
  release while world-only apply is active.
- **EventTriggers exit belt (P1):** Proxy exit fired when `exited >= entered` even if the
  per-proxy occupancy set still had peers (counter drift vs 0.8.44 local enter++). Defer exit
  fire while `HasAny` so delayed one-shots do not latch with a body still inside.
- **Skipped / watchlist:** Workbench exclusive lock (parked); hotbar 3D mesh; trap/door mid-lerp
  (no smoking gun); inventory stack/ammo/reload personal (no new double-apply); generators/fuel
  absolute last-writer underfuel on concurrent addFuel (no host-auth request path this batch);
  monologue proximity (PersonalFlavorHud NearRange); night siren/chase/scent (redirect + sniff
  commit covered); unique key/quest (journal shared + DestroyWorldJournalObject); LogOutput still
  **stale 0.8.34** (DLL 0.8.45 deployed, no fresh 0.8.44/45 playtest logs).
- Preserves: protocol 25, HostWorldReady 139, beartrap, indoor reverb, CoopWorldPresencePolicy,
  0.8.37–0.8.44 work.
- **Rev1:** Place refund uses `Inventory.addItem(source, addSlotIfNoPlace)` (not bool dropIfNoRoom);
  mark-player-placed only after success; Mode trailer backward-compatible.
- **Rev2:** clean — exit HasAny checked after TryRemove; dialog guard host+client Prefix;
  `ContainerLootNetHandlers.Deny.cs` partial so hub stays &lt;500 lines; no protocol / _Highest bump.
- Protocol **25** unchanged. Product bump **0.8.44 → 0.8.45**.
- **Deployed md5** `b782057808f8b8453ef6b2092f7bb672` (build = Steam host = SecondDarkwood client).
- **Runtime:** code-only until dual-box playtest.

---

## 0.8.44 — Padlock/key host triggers + EventTriggers N-peer occupancy + dialog-lock disconnect

Batch 8 (NEW systems dig — door/lock, GameEvents occupancy, dialogue lock). Protocol **25** unchanged.

- **Padlock client unlock drops story GEs (P0):** Client `Padlock.unlock(true)` fires
  `onTryToOpenLocked` / `onUnlockPadlock` locally, but one-shot `GameEvents.fire` is
  Prefix-blocked. Host `ApplyPadlockUnlock` only called `unlock(false)` → combination
  unlock never ran story on anyone. Host now synthesizes both triggers when `wasLocked`
  (pending flush safe; late-join echo skipped).
- **Locked key/lockpick host `onActivate` (P1):** Same hole for `Locked.unlock` after
  client key/lockpick — InputScript `onActivate` was client-only/blocked. Host synth
  `onActivate` when `wasLocked`.
- **EventTriggers N-peer occupancy (P1):** Proxy enter used vanilla multi-collider guard
  (`entered != 0 && isComponentAtPos`) which skipped `entered++` for a second peer → first
  body leaving fired exit while the second was still inside; delayed one-shots could also
  race. Per-proxy id set + fire-only-when-volume-was-empty; local Player Postfix counts
  when vanilla skipped increment because a proxy already occupied.
- **NpcDialogueLock stuck 90s after disconnect (P1):** Host LAN/Steam leave now
  `HostReleaseAllForPlayer` (fan release). PeerRoster prune clears local leases so peers
  are not blocked on "Someone is already talking…" until lease expiry.
- **Skipped / watchlist:** Workbench exclusive lock (parked); hotbar 3D mesh (no small win);
  trap/door mid-lerp (no new smoking gun); weather/time (late-join WeatherSync already);
  inventory stack/ammo double-apply (no new CAN-fix); examination HUD (PersonalFlavorHud
  NearRange already); generators/power beyond Map lights (ApplyGeneratorState covered);
  monologue proximity (covered by flavor HUD + dialog lock).
- Preserves: protocol 25, HostWorldReady 139, beartrap, indoor reverb, CoopWorldPresencePolicy,
  0.8.37–0.8.43 work.
- **Rev1:** Padlock/Locked synth must key off `wasLocked` (not only CurrentReceivePlayerId)
  so pending flush still fires; EventTriggers local Player entered++ only when vanilla
  skipped and body still at pos.
- **Rev2:** clean — occupancy Reset on NetworkResetRegistry; dialog release on roster prune
  belt; no protocol bump.
- Protocol **25** unchanged. Product bump **0.8.43 → 0.8.44**.
- **Deployed md5** `6381a8afac4edb29cad742384a533aa5` (build = Steam host = SecondDarkwood client).
- **Runtime:** code-only until dual-box playtest.

---

## 0.8.43 — Fire-packet muzzle + map marker snapshot + death-bag empty fan

Batch 7 (leftovers dig — workbench/hotbar/death/trade/sleep/map/trap). Protocol **25** unchanged.

- **Weapon-fire muzzle desync (P1):** `HandlePlayerFiredWeapon` ignored serialized
  `PosX/Y/Z` and used lagged proxy `transform.up/right`. Now places muzzle/particles/
  PistolFlash/shot audio from the fire-packet pose + `AimY` axes (vanilla
  `Quaternion.Euler(90, AimY, 0)`), falling back to proxy only if pos is zero.
- **Map marker soft-reconnect dupes + migration owner (P1):** `MapStateSync` late-join
  (including phase-3 AlreadyInWorld) used to `AddRemoteMarker` without clearing → stacked
  green pins every soft reconnect. Snapshot now `ClearRemoteMarkers` then apply;
  `AddRemoteMarker` near-dedupes. Host local markers tagged with `_net.LocalPlayerId`
  (not hardcoded `1`) so post-migration late-join ownership stays correct.
- **Death bag empty linger (P1):** Host `ContainerItem` take/remove that empties a
  `deathDrop` fans `DeathBagLooted` immediately (idempotent via looted set) so peers do
  not keep ghost bags until opener `Inventory.hide` / disconnect. `HandleDeathBagLooted`
  defers Destroy while the local player still has that inventory UI open.
- **Workbench exclusive lock:** skipped — parked product decision (0.7.40 / COOP_COVERAGE);
  both peers may open/use same bench; msg 119 stub ignored. Dual craft is host-validated
  via workbench level + container loot, not exclusive UI.
- **Skipped / watchlist:** Hotbar held-mesh (sprite proxy + light/stream already);
  trade/give (dialog lock + absolute stock); sleep (host clock adopt by design);
  trap/door mid-lerp (8m snap already; no new smoking gun); journal notes shared by
  design (not a leak). Batch 5 ForceAnnounce / Batch 6 ClearAiTargets skimmed — no new hole.
- **Rev1:** DeathBagLooted must not Destroy under open local UI (host empty-fan race).
- **Rev2:** clean — fire pose fallback; map clear-before-apply + LocalPlayerId; death fan
  host-only + defer destroy; workbench stay parked.
- Protocol **25** unchanged. Product bump **0.8.42 → 0.8.43**.
- **Deployed md5** `42b6a1b8205f6befc7aa7cb3b04c5dc3` (build = Steam host = SecondDarkwood client).
- **Runtime:** code-only until dual-box playtest.

---

## 0.8.42 — Disconnect drag-claim fan-out + death/AI leave harden

Batch 6 (combat/death/AI/craft dig — systems less touched by Batches 1–5). Protocol **25** unchanged.

- **N-peer drag claim stuck on disconnect (P0):** Host/Steam peer leave now releases the
  leaver's `_dragClaims` **and** broadcasts reliable DragSync STOP so remaining peers drop
  RemoteDrag maps / kinematic holds. PeerRoster prune also clears local claims (belt if STOP
  lost). Classic "already being moved" forever after a third peer drops mid-drag.
- **AI chase after proxy destroy (P1):** `DestroyRemoteProxy` calls vanilla
  `Character.stopAttacking(proxyT)` before Destroy so target/superTarget do not hold a
  destroyed Transform mid-chase. Dream bunker sticky owner cleared via `ClearIfOwner`.
- **Day-death proxy premature revive (P1):** PlayerState send forces Death1 whenever local
  `!alive` (not only `LocalNightDeath`), so day spectate get-up clips cannot re-alive the
  remote proxy and re-aggro AI on a corpse.
- **Host melee FF-off sensor linger (P2):** `HostMeleeSensorPatch` consumes the MeleeSensor
  when FF is off (same as debounce path) so FixedUpdate does not keep retriggering on proxy
  colliders.
- Preserves: protocol 25, HostWorldReady 139, beartrap, indoor reverb, CoopWorldPresencePolicy,
  0.8.37–0.8.41 work.
- **Dig skips / watchlist:** Trap/door mid-lerp (no smoking-gun resolve path); save/backup races
  beyond 0.8.33 (no new poison path found); workbench exclusive lock still stubbed; hitscan/
  ProxyDamage/HostMelee paths already debounce via ProxyCombatRelay + FF debounce — no new
  double-apply CAN-fix; hotbar/weapon-fire VFX no concrete wrong-resolve; logs still likely
  stale 0.8.34 — runtime prove on dual-box after deploy.
- **Rev2:** `LocationEnterExitNetHandlers.Announce.cs` partial (ForceAnnounce/deferred create/place) so hub stays <500 lines; NetMessageContract HostWorldReady/_Highest=139 aligned with product.
- Protocol **25** unchanged. Product bump **0.8.41 → 0.8.42**.
- **Deployed md5** `46ac90d1b473e3abb8fff02b8a9d8e9b` (build = Steam host = SecondDarkwood client).
- **Runtime:** code-only until dual-box playtest.

---

## 0.8.41 — Soft-reconnect sticky ForceAnnounce + deferred create flush

Batch 5 (soft-reconnect mid-`ol.loading` membership gap + fresh dig). Protocol **25** unchanged.

- **Sticky ForceAnnounce (P0 residual):** Phase-3 `ForceAnnounceLocalOutsideLocationEnter` no longer
  silently no-ops while `playerInOutsideLocation` is still false mid `OutsideLocations.loading` /
  `loadingGame`. Queues a sticky reason when mid-load or previous pad membership is known; Tick
  `TryFlushPendingForceAnnounce` and location settle clear/fire it. World-map reconnect stays a
  silent no-op (no forever pending). Does not call `createLocation`.
- **False LocationExit suppress mid-load (P1):** PlayerState Tick no longer fans `LocationExit` while
  `Core.loadingGame` or `ol.loading` (brief `playerInOutsideLocation=false` flicker). Real
  return-to-world still exits; pending ForceAnnounce alone does not suppress Exit.
- **Deferred remote createLocation flush (P1):** Host pads deferred by the 0.8.39 `ol.loading` /
  `loadingGame` create guard are retried after local settle / Tick flush, still under rate-limit +
  dream skip + grid-prefer guards (no createLocation spam).
- **Settle / return hygiene:** `OnLocalOutsideLocationSettled` clears sticky ForceAnnounce (settle
  already announces) and flushes deferred creates; `OnLocalReturnedToWorld` clears sticky;
  soft-reconnect membership clear drops stale sticky from the prior session.
- Preserves: protocol 25, HostWorldReady 139, beartrap, indoor reverb, CoopWorldPresencePolicy,
  0.8.37–0.8.40 work (NetId recycle, dream proxy filter, ol.loading create defer, soft-reconnect
  membership clear + force announce + missing-proxy place).
- **Dig (no smoking-gun CAN-fix this batch):** Combat hit/damage asymmetry, death/downed/wake,
  workbench/craft/drag/carry ownership, save/backup races beyond 0.8.33, night chase/scent, container
  pending hitch leftovers, AI aggro/player-index for remotes, door kick/open loops — code review only;
  both install `LogOutput.log` still from **0.8.34** sessions (DLL 0.8.40 deployed, no fresh 0.8.40
  playtest). Trap/door mid-lerp skipped (no smoking gun).
- **Skipped / Batch 6:** Trap/door resolve during peer mid-lerp; runtime prove soft-reconnect sticky
  on dual-box; combat/death/AI/craft/save dig needs 0.8.41 playtest logs.
- **Reviewer Pass 1:** LocationExit suppress must not key off pending ForceAnnounce alone (would
  swallow real return-to-world Exit if Tick ran before return Postfix). Deferred create flush must
  not synthesize `HandleLocationEnter` with PlayerId=0 (host uses `CurrentReceivePlayerId`).
- **Reviewer Pass 2:** clean — LocationExit suppress loading-only; deferred create is host-only path (clients return before defer add); no HandleLocationEnter PlayerId=0; soft-reconnect clears stale sticky before handshake re-queue.
- Protocol **25** unchanged. Product bump **0.8.40 → 0.8.41**.
- **Deployed md5** `45082a81c6f2cbc3158bc3e3db43d9a3` (build = Steam host = SecondDarkwood client).
- **Runtime:** code-only until dual-box playtest.

---

## 0.8.40 — Soft-reconnect LocationEnter re-place + force announce

Batch 4 (soft-reconnect sticky LocationEnter / proxy place). Protocol **25** unchanged.

- **Soft-reconnect membership clear (P0):** Phase-3 soft reconnect destroys remote proxies but
  used to keep `RemoteOutsideLocation`. Host `SyncExistingLocationsTo` LocationEnter then saw
  `firstEnterThisLoc=false` and skipped `PlaceRemoteProxyInOutsideLocation` unless local was on
  the same pad — host/peer proxies stayed gone after AlreadyInWorld. LAN + Steam soft paths now
  `ClearMembershipForSoftReconnect` (membership + `_pendingPlaceOnLocationResolve`).
- **Force LocationEnter on AlreadyInWorld handshake (P1):** Client Handshake OK (phase 3) calls
  `ForceAnnounceLocalOutsideLocationEnter` immediately so host re-learns our pad without waiting
  for the ~1 Hz sticky Tick heartbeat (host cleared membership on the brief disconnect).
- **Place-if-proxy-missing belt (P1):** `HandleLocationEnter` treats a missing/destroyed proxy as
  `shouldPlace` even when membership was already sticky (covers any path that tears proxies
  without clearing the dict).
- Preserves: protocol 25, HostWorldReady 139, beartrap, indoor reverb, CoopWorldPresencePolicy,
  0.8.37–0.8.39 work (NetId recycle grace, dream proxy filter, ol.loading createLocation defer).
- **Skipped / Batch 5:** Trap/door resolve during peer mid-lerp — still no fresh smoking-gun
  beyond 0.8.38 8m object snap; needs dual-box playtest. AnimLib / HelpMessage / AudioSource
  Destroy already shipped 0.8.37–0.8.38 (stale 0.8.34 log NREs).
- **Reviewer Pass 1:** ResyncWorldLightsForPeer also on pendingPlace / proxyMissing re-place
  (not on every localSameLoc heartbeat). Soft-reconnect clear + force announce + missing-proxy
  place belt unchanged.
- **Reviewer Pass 2:** clean — no further code changes.
- Protocol **25** unchanged. Product bump **0.8.39 → 0.8.40**.
- **Deployed md5** `0731681bd71cedd954a8f959a6b75611` (build = Steam host = SecondDarkwood client).
- **Runtime:** code-only until dual-box playtest.

---

## 0.8.39 — NetId recycle grace + dream proxy filter + *_done enter match

Batch 3 (entity id lifecycle + dream N-peer + location name twin). Protocol **25** unchanged.

- **NetId recycle grace (P2):** Host `CharacterTracker.Remove` holds freed ids for 2.5s before
  `GetCollisionFreeId` reuse. Client `ApplyHostDespawn` immediately `CharacterTracker.Remove`s
  (not map-clear only), drops pending rows, and ignores EntityState for that id for 2.5s so
  deferred Destroy + late snapshots cannot same-name-claim a crow/rabbit twin.
- **Dream ResyncDreamProxiesAfterLocalLoad (P1):** Only place peers with
  `DreamSyncManager.IsRemoteInDream` — dreams are shared-session / party-once but peers enter
  individually. Stamping every `RemoteProxy` yanked overworld peers onto the pad and polluted
  `RemoteOutsideLocation`.
- **firstEnterThisLoc / localSameLoc (P2):** Use `CoopWorldPresencePolicy.LocationNamesMatch`
  instead of raw `Equals` so `foo` ↔ `foo_done` is not treated as a fresh enter (re-place +
  light re-push thrash).
- **OutsideLocations.loading createLocation defer (P1 dig):** Soft-reconnect / mid-transfer
  sticky `LocationEnter` no longer stacks `createLocation` while local `ol.loading` is true.
  Verified vanilla `createLocation` already uses `transportAfterSpawn:false` (no host yank);
  pressure was load/grid race during the loading screen.
- Preserves: protocol 25, HostWorldReady 139, beartrap, indoor reverb, CoopWorldPresencePolicy,
  0.8.37 peer lifecycle, 0.8.38 interp/createLocation guards.
- **Skipped / Batch 4:** Trap/door resolve during peer teleport mid-lerp — 0.8.38 8m object
  snap already covers pad teleports; no fresh smoking-gun without playtest. Soft-reconnect
  AlreadyInWorld LocationEnter re-announce beyond `ol.loading` guard — monitor dual-box logs.
- **Reviewer Pass 1:** Despawn must `CharacterTracker.Remove` (not ClearId only) so
  FindByPositionAndName cannot claim the deferred-Destroy GO for a new same-name id;
  client ignore window matched host recycle grace (2.5s).
- **Reviewer Pass 2:** clean — no further code changes.
- Protocol **25** unchanged. Product bump **0.8.38 → 0.8.39**.
- **Runtime:** code-only until dual-box playtest.

---

## 0.8.38 — Entity hard-snap + proxy reverb + createLocation guard

Batch 2 (entity/object snap + proxy audio + location create pressure). Protocol **25** unchanged.

- **Entity hard-snap (P1):** `ClientEntityInterpolationService.UpdateInterpolation` hard-snaps
  display + rigidbody at the same thresholds as `RemotePlayerProxy` (>150 XZ / >40 Y) on first
  drive and large teleports (unload/reload, claim from distant twin, knockback). Resets
  first-frame equivalent so LateUpdate does not lerp map-wide.
- **Proxy AudioSource vs reverb (P1):** `RemotePlayerProxy.Spawn` mutes/disables native
  `AudioSource`s instead of `Destroy`. Keeps indoor remote inventory reverb
  (`open_drawer` + `AudioReverbFilter` parented to proxy) without
  "Can't remove AudioSource because AudioReverbFilter depends on it".
- **createLocation guard (P1):** Missing remote pad: prefer host `TryEnterLocationGridNearRemotes`
  (split-map / `CoopWorldPresencePolicy`). Skip `createLocation` on clients, during
  `loadingGame`, and rate-limit remote-only creates (2.5s). Local/host pad entry still uses
  LocationTransport / doors. Does not strand remote sim (grid wake + eventual create).
- **Free-body SetObjectTarget snap (P2):** Generic object apply hard-snaps jumps ≥
  `ClientPushSnapDistance` (8m) like the push path — trap/door/object teleports across pads.
- **ResyncOutsideLocation (bonus):** `ResyncRemoteProxiesForOutsideLocation` uses
  `ResolveOutsideLocation` + `LocationNamesMatch` instead of `ContainsKey` only.
- **SyncExistingLocationsTo:** host LocationEnter uses `LocalPlayerId` instead of hardcoded `1`
  (host-migration safe).
- Preserves: HostWorldReady 139, beartrap rescue, indoor reverb behavior, CoopWorldPresencePolicy,
  protocol 25, Batch 1 / 0.8.37 peer disconnect + HelpMessage + AnimLib pending.
- **Reviewer Pass 1:** Keep `RemoteOutsideLocation` while deferring create (rate-limit /
  loadingGame / client skip) so `CoopWorldPresencePolicy` does not lose remote membership;
  `_pendingPlaceOnLocationResolve` forces proxy place once the pad resolves; clear pending
  on LocationExit / disconnect. Entity 150/40 thresholds leave fast movers alone; object
  8m snap matches push path (throws under 8m still lerp).
- **Reviewer Pass 2:** clean — no further code changes.
- Protocol **25** unchanged. Product bump **0.8.37 → 0.8.38**.
- **Runtime:** code-only until dual-box playtest. Deployed md5
  `70182ab6f3c0ce9981d1f0207268bb75` (build = Steam host = SecondDarkwood client).

---

## 0.8.37 — Peer disconnect cleanup + HelpMessage NRE + AnimLib pending

Batch 1 (peer lifecycle + location membership + log NRE). Protocol **25** unchanged.

- **Peer disconnect cleanup (P0):** Host LAN/Steam disconnect now captures outside-location
  membership, runs `TryLeaveUnoccupiedOutsideLocation`, broadcasts reliable `LocationExit`,
  and immediately pushes `PeerRoster`. Remaining peers destroy the frozen proxy and clear
  `RemoteOutsideLocation` (N-peer: bunker stays live while another peer is inside; two
  same-frame disconnects leave only when the last occupant is gone).
- **PeerRoster proxy prune (P0):** `ApplyPeerRosterLocal` prunes client proxies whose ids
  vanished from the roster (never the local player). Complements LocationExit fan-out;
  roster is sent before LocationExit so clients hit the destroy path instead of teleport.
- **HandleLocationExit defer hole (P1):** Host leave-unoccupied always runs after
  `RemoteOutsideLocation.Remove`, even when `CanSpawnRemoteProxies` is false. Only proxy
  teleport/place is deferred during `loadingGame`.
- **HelpMessage suppress NRE (P0):** `UiDisplayHelpMessageSuppressPatch` no longer
  bool-skips `UI.displayHelpMessage` (that nulled `__result` and crashed
  `GameEvent.fire` on `actionToDisable`). Always create, then hide + zero alpha for
  out-of-range peers so hideout tutorials stay personal.
- **AnimLibrary pending (P1):** `HandlePlayerAnimLibrary` stashes when proxy is null
  (like PendingPlayerLights); flush on `EnsureRemoteProxy`. Sticky `SyncCurrentAnimLibrary`
  on join / late-join bulk mirrors light sticky.

## 0.8.36 — Bear-trap co-op rescue + remote inventory indoor reverb

Dual-box playtest follow-ups on top of 0.8.34/0.8.35.

- **Bear-trap co-op rescue:** Sprung traps become vanilla `isDroppedItem` with loot
  type often `junk` ("Scrap metal"). Picking that up sent `WorldObjectRemoved` for
  junk; peer `DestroyObjectByPos` matched the beartrap via the junk slot and
  destroyed it without a reliable free, while container `RemoveItem` was denied and
  refunded the grant. Fix: never match occupancy/world traps on a non-trap needle;
  send trap GO name on rescue pickup; exclude trap inventories from container sync
  during pickup; strengthen `ReleaseLocalBearTrapIfNear` (flag belt + NetId); XZ
  occupancy resolve (`ResolveOccupyingTrapId` tall sphere + XZ filter) so
  `TrapNetId` is non-zero. Host-stuck/client-pickup and reverse covered; each peer
  frees its local body on destroy (N-peer).
- **Double snap sound:** Host `activateSound` was forwarded via PlayerAudio while
  peers also played it in `ApplyTrapState`. Trap-owned activate sounds are no longer
  forwarded (TrapState owns peer FX).
- **Remote inventory indoor reverb:** `open_drawer` / `close_drawer` now always
  parent to the remote proxy after `CharBase.checkGround()` so
  `AudioController` sees `isInside` and applies `AudioReverbFilter` like local bag
  open. Shared path covers other stick-to-sender presence SFX.
- Does not regress door scrape, footsteps, HelpMessage proximity, ClientBackup,
  container hitch, or HostWorldReady **0.8.35**.
- **Reviewer Pass 1 nits:** `DarkwoodMP.Mod.csproj` Version/InformationalVersion
  **0.8.36**; collapse identical `ReleaseLocalBearTrapIfNear` branches in
  `DestroyObjectByPos`; `TrapPickupGuard.IsGuarded` matches exact inventory only
  (null `_inv` no longer guards all); drop host `activateSound` Play after
  `ApplyTrapState` (ApplyTrapState already plays it); Prefix clears guard on throw
  so Postfix is not required for that path.
- Protocol **25** unchanged. Product bump **0.8.35 → 0.8.36**.
- **Runtime:** code-only until dual-box playtest.

---

## 0.8.35 — Clients wait until the host is fully in-world

If a client joined (or pressed JOIN / auto WorldRequest) while the host was
still loading or entering the chapter, world download could start mid-load and
cascade into join bugs. Gate is host-authoritative and works for any peer id
(2nd, 3rd, late joiner) — not “first client only.”

- **Host ready gate:** `HostHasShareableWorld` no longer treats `Core.loadingGame`
  (or profile / WorldGenerator alone) as shareable. Requires a live `Player` past
  load (`loadedGame` / `coreStarted`) — no `!mainMenu`-only shortcut (avoids
  mid-transition true). Sticky `mainMenu` with a live loaded player still counts
  (keeps the 0.8.x dual-box share fix).
- **Host→clients signal:** new `HostWorldReady` (msg **139**, protocol **25**
  unchanged). Host broadcasts `Ready=true` on rising edge of fully in-world; also
  sends to a peer that handshakes or WorldRequests while already ready. When host
  leaves fully-in-world, broadcasts `Ready=false` so every waiting title peer
  clears WAIT/HOST READY (N peers). Soft reconnect / share-in-flight ignore the
  clear so there is no deadlock.
- **Client wait:** title join shows **WAIT HOST…** / status “waiting for host to
  enter world” until HostWorldReady Ready=true or `WorldSaveBegin` (Begin also
  marks ready for missed-signal / older-host compat). Soft reconnect
  (`AlreadyInWorld`) sets ready immediately and still skips world share — no
  deadlock. Ready=false after share has started is ignored until ENTER WORLD.
- **Share triggers:** handshake delayed share, sticky TickHostWorldShareWhenReady,
  HostEnterWorldSharePatch (`Player.Start`), and WorldRequest all require the
  strict gate. Mid-load `Player.Start` defers to the tick rising edge.
- **Connect path:** host no longer dumps late-join sticky bulk to peers while not
  fully in-world (title / mid-load waiters); phase-3 reconnect still queues bulk
  from handshake.
- Does not break 0.8.32–0.8.34 fixes (trace logging, container hitch, bird-trap
  rescue, HelpMessage proximity, stale backup / AlreadyInWorld sticky share skip).
- Protocol **25** unchanged. Product bump **0.8.34 → 0.8.35**.
- **Runtime:** code-only until dual-box (and imagined 3rd peer) playtest.

---

## 0.8.34 — Playtest: container hitch, bird-trap rescue, location HelpMessage leak

Dual-box playtest follow-ups on top of 0.8.32/0.8.33.

- **Container open hitch:** `FindInventoryByPos` always ran `SceneScanCache<Inventory>` (`FindObjectsOfType` ~45–50ms, `footType=Inventory`) even after OverlapSphere already found the wardrobe/corpse. Overlap now uses `maxDist`, returns immediately on hit, and client `ContainerStateSync` prefers the already-opened inventory.
- **Bird / bear trap co-op rescue:** Occupied-trap pickup is allowed. Removing or picking up the trap frees the stuck player (`interruptAllActions(stopBeartrap)` via `ReleaseLocalBearTrapIfNear` on local destroy and on `WorldObjectRemoved`). Occupancy distance checks use **XZ only** (trap Y≈-10 vs player Y≈16 was false-negative). Host-stuck/client-pickup and client-stuck/host-pickup both covered.
- **Location hint leak:** Hideout tutorials use `UI.displayHelpMessage` (`GameEvent` `isHelpMessage`), not `Player.displayMessage` — the 0.8.32 gate never saw them. Also `GameEvents.fire()` only *starts* delayed coroutines, so try/finally `SuppressCount` around `fire()` missed delayed HelpMessage/displayMessage. Fix: gate `UI.displayHelpMessage`; re-check `NearRange` (**250→60** XZ) against the GE transform when delayed `GameEvent.fire` MoveNext actually displays (no process-wide `_forceSuppressUntil` blacklist that blanked local examine/help); chat/system tips `BeginBypass`. Proxy enter/exit rely on the same MoveNext proximity gate (not a short DefaultSuppressSeconds window).
- **Trade stock NRE:** `Inventory.refreshReputation` during early `TradeInventorySync` is try/caught (join/title race).
- Protocol **25** unchanged. Product bump **0.8.33 → 0.8.34**.

---

## 0.8.33 — Stale client backup no longer voids a join

Dual-box rejoin restored a July character snapshot (molotov / gasBomb hotbar, Y=16 at Z≈89) over a correct offline load near the hideout, so the client appeared in nowhere with weird cocktails. Host also pushed the shared legacy `client_backup.json` to the wrong player id.

- Host no longer falls back to shared `client_backup.json` when loading a per-player backup.
- Stale detection uses **CampaignId** (stable), not ContentFingerprint inequality (hashes churn every Save; host/client diverge after share).
- Empty-CampaignId legacy migrate **and** host `SaveBackupFile` stamp are refused when the snapshot looks like July poison/spoil (missing/ancient timestamp, absurd item stacks, or null-fp lvl-1+ hotbar/inv) — **including on day 2+**, so hotbar/inv cannot slip through after a position-only skip.
- Pose-vs-live absurdity is **client/offline apply only** — host `LoadBackupFileForPlayer` / late-join push never compares a peer backup against host `Player.Instance` (peers far apart must not false-positive skip a good null-fp campaign backup).
- Matched campaign-scoped backups are trusted; optional belt on client apply is missing fingerprint **plus** absurd pose vs the already-loaded body.
- `RestoreFromBackup` returns bool success; host push sets `_receivedHostClientBackup` only when restore actually applied so a rejected stale push still allows local self fallback.
- Restore refuses the whole snapshot when stale (inv/hotbar/skills/pose), not pose alone.
- Soft age floor for legacy spoil is **14 days** (weekends must not false-positive); missing/unparseable timestamps still count as poison for empty-CampaignId files.
- Position restore is still skipped when the backup is far from the pose already loaded from `sav.dat` (XZ or Y) as belt-and-suspenders.
- Phase-3 AlreadyInWorld peers are not counted as waiting for sticky-mainMenu world share (stops the useless re-share the client ignored).
- Protocol **25** unchanged. Product bump **0.8.32 → 0.8.33**.

---

## 0.8.32 — Trace is max dual-box capture

`LogPreset=Trace` now also emits `LegacyInfo` dumps (previously Dev-only), so one preset covers Legacy + Verbose gates + full Trace categories.

- Dual-box playtest configs on this machine: host + client set to **Trace** / MinLevel Trace, VerboseLogging + VerboseEntitySync + VerboseLightSync on, BepInEx disk/console `LogLevels=All` and `WriteUnityLog=true`.
- **Door hinge scrape no longer loops forever on the client.** `door_rotating` / `door_metal_rotating` stay local to `Door.Update` (start/stop on each peer). Networking them via 0.8.31 `ForwardWorldObjectSound` left orphan loops because Stop was never forwarded.
- **Client runner hears one footstep stream.** Proxy footstep playback sets `ApplyingFromNetwork`, and world-object forward again suppresses foot/walk_clothes IDs, so host proxy steps do not echo back to the runner as a second `PlayerAudio` stream.
- **Location/proximity narrative hints stay on the observer.** Remote `GameEventsFired` apply suppresses `displayMessage` unless the local listener is within `PersonalFlavorHud.NearRange` (250 XZ). Proxy area enter/exit always suppress — host must not show the client's hint while auth-firing.
- Protocol **25** unchanged. Product bump **0.8.31 → 0.8.32**.

---

## 0.8.31 — The leftovers before a real playtest

Four things could still split a long session.

- Two traders with the same name keep their own stock. A purchase hits the one you are standing at.
- Another crate with the same name can be moved. Only the crate actually being dragged, or the one at that spot, stays locked.
- If you were still on the title screen during the opening movie, the movie waits until your world exists, and a late join still wakes with everyone else after the movie has finished.
- The banshee scream plays on the person it saw. Sounds from a stove, a radio, or a crate travel from that object instead of staying on the host.
- Protocol **25** unchanged. Product bump **0.8.30 → 0.8.31**.

---

## 0.8.30 — A trap update stays on that trap

If the nearby search missed a trap, the game looked up the name anywhere in the world. Another trap with the same name, even across the map, could spring or reset instead.

- That name lookup now has to be within 20 steps of the trap that actually changed.
- Protocol **25** unchanged. Product bump **0.8.29 → 0.8.30**.

---

## 0.8.29 — The nearest door and window are the ones that move

Opening, hitting, or barricading a door could land on a different door in the same room. The search kept the first door it found inside the range, not the closest one. Windows did the same.

- Door, window, and the other position searches now keep the closest match.
- Protocol **25** unchanged. Product bump **0.8.28 → 0.8.29**.

---

## 0.8.28 — A dream can end when the dreamers are dead

Dying in a dream waited until every connected player was dead. Someone who never entered the dream, still outside in the woods, kept the dream from ending. The people inside stayed stuck watching.

- The dream now ends when everyone who was pulled into it has died. The host remembers those peers when the dream starts. Someone who left the game no longer holds it open.
- Protocol **25** unchanged. Product bump **0.8.27 → 0.8.28**.

---

## 0.8.27 — Loot comes out of the container you opened

Two containers next to each other could share a search. Taking an item from one could empty the other, because the first container the search touched won, not the closest.

- The container search now keeps the closest match, and it will not reach across the room to a different crate or a different body.
- Protocol **25** unchanged. Product bump **0.8.26 → 0.8.27**.

---

## 0.8.26 — A chapter change still arrives if the save is slow

When the story moved to the next chapter, other players waited for the new save. If that save was still transferring when the backup timer ran out, the backup gave up. They could stay on the old map while the host had already moved on.

- The backup now waits twice more while the save is still moving, then loads the chapter anyway. An older backup from a previous chapter does not load over the new one.
- If the save finishes in time, that backup does nothing.
- Protocol **25** unchanged. Product bump **0.8.25 → 0.8.26**.

---

## 0.8.25 — Picking something up does not delete a different container

If the picked object could not be matched by name, an empty container within a few steps could be removed instead. A crate or barrel you had emptied could vanish because someone picked up something else nearby.

- A pickup now removes an object only when the name matches. A nearby barrel, crate, or mushroom is not taken just because it is close. If two copies share a name, the nearest one within range is the one removed.
- Protocol **25** unchanged. Product bump **0.8.24 → 0.8.25**.

---

## 0.8.24 — Lightning flashes for everyone in the rain

Only the host saw lightning. The flash and the thunder are decided on the host's clock, and other players do not run that clock.

- When the host's storm flashes, every peer who is outside sees and hears it.
- If the host is indoors, people standing in the rain still get the flash.
- Someone underground does not get a bolt in the basement.
- Protocol **25** unchanged. Product bump **0.8.23 → 0.8.24**.

---

## 0.8.23 — The scripted event that was named is the one that runs

When a peer did not have an exact copy of a scripted event, a shorter name inside that name was enough. The wrong door, lamp, or scene could run.

- A match now needs the full event name. A shorter name that only sits inside it is left alone.
- Protocol **25** unchanged. Product bump **0.8.22 → 0.8.23**.

---

## 0.8.22 — Creature sounds stay on the creature

A growl or footstep from an enemy was pinned to the host's body. Everyone heard the animal walking with the host, even when it was across the clearing.

- Sounds that belong to a player still follow that player, so indoor echo stays right.
- Creature sounds are marked as not the player's, so they stay where the animal was, even in melee.
- Protocol **25** unchanged. Product bump **0.8.21 → 0.8.22**.

---

## 0.8.21 — Night scenes play for everyone

The host played the night's knocks, whispers, and scripted scenes. Other players never heard them, because their clock does not run those events.

- When the host starts a night scene, each peer who is at a hideout plays that same scene. Timed scenes are included, not only the ones that can fire at any moment.
- A creature that scene would spawn is not created again, and the scene does not hit the door a second time. The host's creature and the host's door are what everyone sees.
- Protocol **25** unchanged. Product bump **0.8.20 → 0.8.21**.

---

## 0.8.20 — The morning trader stays while anyone is still in the hideout

Walking out of the hideout after night ended the morning for the whole party. The trader vanished and time started again while other people were still inside.

- The morning now waits until the hideout is empty. Walking back in counts again. If the last person inside dies or disconnects, the morning ends.
- Killing the night trader still ends the morning immediately.
- Protocol **25** unchanged. Product bump **0.8.19 → 0.8.20**.

---

## 0.8.19 — An enemy chases the player who is there

Walking into any trigger remembered one remote player for eight seconds, for the whole map. With three people, a wolf next to you could turn and run to whoever last touched a door somewhere else.

- Ordinary enemies pick the nearest living player. Touching a door somewhere else does not pull them off the person standing next to them.
- The dream bunker spirit only remembers a player who entered a trigger within 2000 of the dream. A meadow door does not spawn that spirit on them.
- Protocol **25** unchanged. Product bump **0.8.18 → 0.8.19**.

---

## 0.8.18 — The trader you bought from is the one who loses the stock

Shop updates searched for the first NPC with that name. A dream copy, or another person with the same name earlier in the scene, had their shelves cleared instead. A third player then saw the wrong shop.

- Trade stock now says whether it belongs to the dream copy. An overworld purchase does not clear the dream twin just because someone else is dreaming.
- Protocol **25** unchanged. Product bump **0.8.17 → 0.8.18**.

---

## 0.8.17 — Letting go unsticks the crate you actually moved

Stopping a drag looked up the first object with that name and then gave up. In a house full of identical furniture, often with a third player, the crate that was really moving stayed frozen and another one was released.

- A second identical crate can be picked up. Only the body that is actually moving stays locked.
- Protocol **25** unchanged. Product bump **0.8.16 → 0.8.17**.

---

## 0.8.16 — The opening movie plays for everyone

A new game's prologue (the title card, the intro video, then the tutorial) only ran on the host. Other players either skipped it or played their own copy out of time.

- The host still plays the vanilla opening. Everyone else in the session plays that same movie and wakes up when it ends, including a third player.
- A client does not start a private opening on top of that.
- Protocol **25** unchanged. Product bump **0.8.15 → 0.8.16**.

---

## 0.8.15 — A new chapter reaches players already in the game

The host moved to the next chapter and sent the new world. Anyone already playing ignored that package, and the backup load treated “a game is loaded” as “the new chapter is already here,” so it never ran.

- An in-game chapter change accepts the host world, writes it onto the current profile, and loads the chapter without sitting on the title screen.
- If that package never arrives, the backup load still switches chapter after a short wait.
- Protocol **25** unchanged. Product bump **0.8.14 → 0.8.15**.

---

## 0.8.14 — Dialogue finds the named person

A requested name was treated as a match when it merely started another NPC's name, or the other way around. The trade or story result could land on the wrong person.

- `door_underground` still matches `door_underground_act1`. A different story branch such as `talkingtree_darkside` does not match `TalkingTree`.
- Protocol **25** unchanged. Product bump **0.8.13 → 0.8.14**.

---

## 0.8.13 — Dream dialogue does not open the overworld bunker

Ending the dream door conversation was still allowed to fire leave-door events while the dream pad was not loaded. Those events exist on the overworld bunker too, so the real-world door could open.

- If a dream is active and the pad is missing, or the only matching NPC is the overworld copy, the close does not fire that copy's triggers.
- The overworld conversation still fires its own nearby leave-door event when nobody is dreaming.
- Protocol **25** unchanged. Product bump **0.8.12 → 0.8.13**.

---

## 0.8.12 — A second hit in one swing still lands

The host was ignoring any melee that hit the same character again within a fifth of a second. That was meant to stop a remote player's extra colliders from taking the same swing twice. It also cancelled a real follow-up hit on an enemy, and every untracked body shared one timer.

- The guard now applies only to the remote player's body, and only for the same attacker. A different enemy can still land a hit in that same moment.
- Protocol **25** unchanged. Product bump **0.8.11 → 0.8.12**.

---

## 0.8.11 — Forest spirit can still visit the host

When the other player was far away, every forest spirit spawn was moved to them. The host's own night event never ran. Dogs and worms already used a coin flip. The spirit now does too, so a split party shares the visits.

- Protocol **25** unchanged. Product bump **0.8.10 → 0.8.11**.

---

## 0.8.10 — Dream bunker door stays on the dream pad

The dream bunker and the overworld bunker share `door_underground`. If the dream pad was not ready, the search still accepted that overworld copy, however far away, and opened it. The same window also broadcast every opened door in the world.

- While a dream is active and the pad is not loaded, door force-open and the open-door poll do nothing.
- The dialogue NPC has to be the real `door_underground` and within 200 of the event. A name that merely contains "door" and "underground" is not enough.
- Protocol **25** unchanged. Product bump **0.8.9 → 0.8.10**.

---

## 0.8.9 — The named door and lamp are the ones that change

A message named "door" or "lamp" could hit any nearby object whose name merely contained those letters. Opening, unlocking, or switching the light then happened on the wrong one.

- **Doors** match the full name only, and only when that door is near the position in the message. A different door a few steps away is not opened just because it is the only one nearby.
- **Lamps** match the full name only, including the usual "(Clone)" suffix.
- Protocol **25** unchanged. Product bump **0.8.8 → 0.8.9**.

---

## 0.8.8 — Client chase matches the host's aggro

- **Movement** coasts a short way if the next update is late, then sits on the host spot. It does not slide a full extra step past the enemy, and it does not freeze and then jump.
- **What the enemy is doing** (idle, chase, investigate, flee) is copied onto the client. A gap with no clip keeps the last frame during a chase. A blank sprite still falls back to idle.
- **Rabbits and crows** that were only investigating a noise still flee when you get close. That was being treated as "already running away."
- Protocol **25** unchanged. The behaviour rides in spare bits of the existing entity flags byte. Product bump **0.8.7 → 0.8.8**.

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
