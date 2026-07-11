# DarkwoodMP-YokWare — Deep Multiplayer Audit

**Date:** 2026-07-10  
**Mod:** `C:\MyProjects\DarkwoodMP-YokWare` (YokWare Branch **0.9.2 Path B**, Horde wire **ProtocolVersion=19**)  
**Original SP reference:** `C:\Users\amicu\Desktop\Darkwood DECOMPILED\Scripts\Assembly-CSharp` (~1107 game `.cs` files)  
**Note:** Objective path `C:\Users\amicu\Desktop\Darkwood` is missing; decompiled tree is authoritative.  
**Scope:** Static / IL-path analysis of game logic + mod patches/networking. Live 2-client campaign playtest is residual risk (`docs/TODO.md` short list; detail in `CHANGELOG.md` / `COOP_COVERAGE.md`).

**0.9.2 resolution (code shipped):** Audit **C1** (host-only time), **C2** (dialog world-only + NPC lock H5), **C3** (chapter auto rehost/reconnect; credits residual), **C4** (share fail-loud; no fake chunk seed), **H1** (client→host flags), **H3** (night death world-mutation suppress) are addressed in Path B sources. Findings tables below retain original diagnosis for history; treat CRITICAL rows as **fixed in 0.9.2** unless marked residual.

**Status key (gap map):** **OK** covered correctly · **PARTIAL** incomplete · **BUG** incorrect behavior · **UNSYNCED** missing · **LOCAL** deliberate personal state

---

## 1. Original-game baseline (decompile)

Single-player Darkwood is a **single `Player.Instance` world**: time, flags, dialogues, dreams, night scenarios, and GameEvents all assume one actor. Multiplayer must re-home every “reacts to Player.Instance” and every exclusive save mutation.

### 1.1 Time / day-night

| Symbol | Path | Role |
|--------|------|------|
| `Controller.FixedUpdate` | `Controller.cs` ~916–927 | Advances `CurrentTime` every `timeChangeInterval` when not dreaming / not outside-only; calls `refreshTime()`. |
| `Controller.refreshTime` | `Controller.cs` ~953–1029 | Fires day chain: `startBeforeDay` → `startDay` → `startAfterNight`; night: scenario `setMe()`, ambient, messages. Thresholds use `currentTimeDifference` (== 0) so **edge ticks are one-shot per minute value**. |
| `Controller.startDay` | ~1053 | Day++, despawn night objects, full heal, skills recharge, auto-save. |
| `Controller.startBeforeDay` | ~1106 | Fade, `karmaPoints += 20`, `player_survivedNight`, invuln. Only if `isHardNight`. |
| `Controller.startAfterNight` | ~1151 | Hideout morning: spawn wolf/trader, **reputation bonus**, lights, `timeFreeze` effect (`isAfterNight`). |
| `Controller.endAfterNight` | ~1247 | Clear morning state, destroy trader, bump time. |
| `Controller.skipDay` | ~1039 | Jump to `dayTime` + `refreshTime` (night death path). |
| `Controller.generateChapter` | ~1336 | `saveEmptyChapterSave` optional → `SceneManager.LoadScene("chapter"+id)`. |

**MP implication:** Both machines run `FixedUpdate` time unless patched. Dual `startAfterNight` can double-spawn traders; dual `startDay` double-heals / double-despawns. Host-only TimeSync is not enough without suppressing client clock ownership.

### 1.2 Worldgen & chunks

| Symbol | Path | Role |
|--------|------|------|
| `WorldGenerator` | `WorldGenerator.cs` (~2237 lines) | Chunk grid, biomes, locations, landmarks, `onFinished`, `spawnRandomObjects`, chapter finish → save/load. |
| `RandomWorldObjects` | `RandomWorldObjects.cs` | Daily / night object pools (ungseeded in vanilla). |
| `CharacterSpawnPoint.actuallySpawn` | spawn chance rolls | Location activation RNG. |

**MP implication:** Vanilla RNG is **not** multiplayer-safe. Path B design is **host gen + WorldSaveShare** (files to client slot 5), not dual independent gen. Residual: location/landmark *placement* order if both generate without share (`docs/TODO.md`).

### 1.3 Flags / GameEvents / EventTriggers

| Symbol | Path | Role |
|--------|------|------|
| `Flags.setFlag` (bool/int) | `Flags.cs` ~164–208 | Story switches; saved in `Flags.SaveState` with NPC + dialogue states. |
| `Flags.setCh2flags` | ~305 | Maps ch1 doctor/piotrek outcomes into ch2 flags. |
| `GameEvents.fire` | `GameEvents.cs` ~195 | `fired && !multipleFire` one-shot; runs `GameEvent` coroutines; optional destroy. |
| `GameEvent` | `GameEvent.cs` (~2032) | Spawns, flags, doors, items, dreams, teleports — full story scripting. |
| `EventTriggers.OnTriggerEnter` | `EventTriggers.cs` ~263 | **Only** `other.gameObject == Player.Instance.gameObject` (or sight of Player). Proxies never fire vanilla. |
| `EventTriggers.fireEventTrigger` | ~317 | Requirements + remoteTriggers fan-out. |

**MP implication:** Client walking into a volume does nothing on host without proxy patches. Client one-shot `GameEvents.fire` must not run independently or story doubles.

### 1.4 Dialogue outcomes

| Symbol | Path | Role |
|--------|------|------|
| `DialogueButton.onPress` | `DialogueButton.cs` ~20–29 | Marks `alreadyShown`, `displayDialogue(destDialogueName)`. |
| `DialogueWindow.displayDialogue` / `displayNextBoard` | `DialogueWindow.cs` ~793+ | Board outcomes: `giveItem`, `removeItem`, journal, **reputation**, `worldFlag`, `fireWorldEvent`, `startDream` / `endDream`, transport, exit. Applied to **`Player.Instance`**. |
| `DialogueWindow.acceptTrade` | ~388 | Exchange trays ↔ NPC inventory. |

**MP implication:** Replaying a peer’s dialogue choice via host `displayDialogue` applies **host** inventory/rep/flags — not the client’s bag. Concurrent dual talks on same NPC corrupt `alreadyShown` / board state.

### 1.5 Dreams / chapter / epilogue

| Symbol | Path | Role |
|--------|------|------|
| `Dreams.prepareDream` | `Dreams.cs` ~286 | Save state, special save for `epilog_part1a_dream`, `OutsideLocations.prepareLocation`. |
| `Dreams.startDreaming` | ~353 | Clear inv, set dream hotbar, teleport to dream spawn, freeze world time context. |
| `Dreams.initiateEndDreaming` | ~460 | Transition via outcome preset. |
| `Dreams.endDreaming` | ~487 | Restore inv/time/position; outcome effects (items, chained dreams, fire GameEvents). |
| `Player.die` (in dream) | `Player.cs` ~7381 | `outcome = "playerDeath"` → `initiateEndDreaming`. |
| `Player.die` (epilogue crawl) | ~7332 | Separate camera-pan / crawl path — **not** normal dream death. |
| `Player.onDeath` | ~7415 | Bags, home transport, **night → `skipDay`**, flags, save, `isAfterDeath`. |
| `EpilogueOutcomes.goToCredits` | `EpilogueOutcomes.cs` | LoadScene credits. |

**MP implication:** Death dream / night skipDay is world-shared in SP. Co-op must choose partial-spectate vs all-dead morning. Chapter load tears scene — network must rejoin or deliberately stop.

### 1.6 Combat & damage

| Symbol | Path | Role |
|--------|------|------|
| `Player` / `CharBase.getHit` | `Player.cs`, Character hierarchy | Health, effects, die. |
| `MeleeSensor.OnTriggerEnter` | melee | Hits `Character` / world props via collider. |
| `Player.spawnBullet` / projectile | ranged | Hitscan / projectiles. |
| `CharacterSpawner.spawnCharacterAround` | night/random events | Dynamic enemy spawn choke point. |

**MP implication:** Proxies need colliders for FF; client must not authoritatively kill host-owned AI.

### 1.7 Inventory / containers / trade

| Symbol | Path | Role |
|--------|------|------|
| `Inventory` | `Inventory.cs` | Player + world itemInv + deathDrop. |
| `InvSlot.grabItem` / place | container UI | Slot moves. |
| `Player.spawnDroppedInvItem*` | drops | World ground items. |
| `Player.dropBody` | death bags | Random subset of inventory into `DeathDrop`. |
| NPC trader + `DialogueWindow.acceptTrade` | trade | Stock + personal rep. |

### 1.8 Save / load

| Symbol | Path | Role |
|--------|------|------|
| `SaveManager.Save` | `SaveManager.cs` (~2725) | Full world + static + profile. |
| `SaveableObject` | IDs | Prefab + uniqueId identity. |
| Profile slots under persistentDataPath | Core profiles | Same-PC dual client = shared AppData. |

### 1.9 Night siege / scenarios

| Symbol | Path | Role |
|--------|------|------|
| `NightScenarios.setCurrentScenario` | `NightScenarios.cs` ~256 | Pick scenario for night. |
| `NightScenario.setMe` / `checkFrequencies` | scenario runtime | CustomEvent fire schedule. |
| `CustomEvent` / `RandomEvent.fire` | day random + night | Dual fire = double spawns. |
| `CharacterSpawner.spawnNightChar` | night defense | Around player. |

### 1.10 Key NPCs / story actors

Wolfman, NightTrader / The Three (`isNightTrader`), Doctor, Piotrek, Musician, Talking Tree, village/swamp flags, sister, epilogue Baba — almost all gate on **`Flags`** + dialogue outcomes + hideout location. Morning trader rep is hideout-id scaled (`startAfterNight` repGain 100–250).

---

## 2. Deep mod bug audit (severity-ranked)

Live load path: `DarkwoodMP.Mod` (Horde). Ironbark (`DarkwoodMP.Protocol`, `DarkwoodMP.Server`) is **parallel/deferred** for dedicated — **not** the BepInEx LAN wire (`PluginInfo.ProtocolVersion = 19`).

### CRITICAL

| ID | Finding | Location | Root cause vs symptom | Roles | Consequence |
|----|---------|----------|----------------------|-------|-------------|
| **C1** | **Client clock still advances and runs full `refreshTime` logic** | Vanilla `Controller.FixedUpdate`; mod `HandleTimeSync` sets `CurrentTime`/`day` then **`refreshTime()`** (`LanNetworkManager.Handlers.cs` ~4880–4922). **No patch** suppresses client `CurrentTime++` or client day/night edge handlers. | Symptom: “time drifts / double morning”. Root: dual sim of SP time authority. | Host+Client | Dual `startDay` / `startAfterNight` / night `setMe` edges; double heal; **client can spawn morning wolf/trader** at *their* hideout while host does the same; TimeSync can re-fire thresholds when snapping. |
| **C2** | **Client dialogue outcomes applied on host to host `Player.Instance`** | `DialogOutcomeSendPatch` + `HandleDialogOutcomeSync` → `dw.displayDialogue(TargetDialogueName)` (`LanNetworkManager.Handlers.cs` ~3990–4031); vanilla outcomes in `DialogueWindow` ~926+ give/remove items, rep, flags on **Player.Instance**. | Symptom: free items on host / wrong rep. Root: host replaying peer story UI against wrong inventory. | Client→Host | Host inventory/journal polluted; host rep changed by client talk; client already applied outcomes locally → **asymmetric personal state** + world flags may only stick if host path runs. |
| **C3** | **Chapter / credits transition stops multiplayer** | `ChapterTransitionHelpers.ApplyChapterLoad(..., stopNetwork: true)`; `EpilogueGoToCreditsPatch` → `ApplySceneLoad` stops network. | Symptom: “co-op dies at ch2”. Root: deliberate tear-down without rehost. | All | Session ends at chapter boundary / credits; peers load solo unless manual re-host. Continuous co-op through credits is **not** claimed. |
| **C4** | **World layout depends on save share; no live per-chunk seed in Path B** | `WorldGenSharePatch` only shares after host `onFinished`; **no** `ChunkGenSeed` / `InitState` patches in live `DarkwoodMP.Mod`. TODO #5 residual placement. | Symptom: “different forests”. Root: docs (MERGE 0.2 / TODO archive) describe Yokyy determinism that **is not in Path B tree**; truth is file transfer. | Host gen / Client load | If share fails / late / same-PC profile race, worlds diverge permanently. |

### HIGH

| ID | Finding | Location | Root cause | Roles | Consequence |
|----|---------|----------|------------|-------|-------------|
| **H1** | Flag deltas are **host→client only** | `FlagSyncBoolPatch` / `FlagSyncIntPatch` require `Role == Host` | Client-side `setFlag` (examine, local GE residual, death flags) never reaches host | Client story | Host missing flags → wrong chapter gates / ch2 mapping (`setCh2flags`) |
| **H2** | Flag 2s cooldown + pending last-value | `FlagSync*Patch` CooldownSec=2 | Intermediate true→false→true collapses to last | Host story | Missed one-shot consumers that edge-trigger on transitions |
| **H3** | Night death partial: dead player still runs most of `onDeath` | `NightDeathSkipDayPatch` only blocks `skipDay`/`Save`; vanilla still `transportToHome`, `respawnAllEnemies` (host), flags | SkipDay is not full death rewrite | Night death | Host mid-night: home teleport + enemy respawn on death; survivor’s night may soft-desync |
| **H4** | Dream start request uses `prepareDream` only | `HandleDreamStartRequest` → `StartCoroutine(prepareDream)` | Does not mirror host `DreamSession.TryBegin` preconditions / multiplayer enter fan-out fully if host already mid-transition | Client-initiated dream | Stuck request, ignored if session active, or host-only dream without peers |
| **H5** | Concurrent dual dialogue on same NPC | No interaction lock on `DialogueWindow` | SP `alreadyShown` / board state is singular | Both | Garbled options, double outcomes, trade race with stock model C |
| **H6** | Container simultaneous loot race | `ContainerSyncPatches` absolute slot actions, pending remove | No lock; last packet wins | Both | Dupe / ghost items / empty wrong side under lag |
| **H7** | Docs claiming Ironbark as live LAN wire | Historical matrices removed; live wire = Horde 19 (`PluginInfo.ProtocolVersion`) | Was doc lag after Path B merge | Dev ops | Use `README.md` / `COOP_COVERAGE.md` only |
| **H8** | Dedicated server not on live Path B peer path | `DarkwoodMP.Server` + Ironbark vs Horde 19 | Product split | Dedicated | Server cannot host Path B LAN sessions as-is |

### MEDIUM

| ID | Finding | Location | Consequence |
|----|---------|----------|-------------|
| **M1** | Journal keys/notes/entries broadcast to all | `JournalSyncPatches` | Shared keys (good) also clone personal journal noise; reverse journal-from-dialog host pollution (C2) |
| **M2** | Reputation live broadcast both ways for non-night traders | `ReputationSyncPatch` | Client dialog-driven rep changes overwrite peers; night traders correctly excluded |
| **M3** | `GameEventsFired` match by name+radius 2.5–3m | `ApplyGameEventsFired` | Dense story areas can fire wrong neighboring GameEvents |
| **M4** | Infection host-only spread | `InfectionStatusSyncPatches` | OK authority; client-only infection sources never grow without host near |
| **M5** | Examine host runs full `examine()` for client request | `HandleExamineObject` | Host may get examine FX/UI side effects while client already saw text |
| **M6** | Night spawn redirect only when proxy ≥1000 units | `NightSpawnRedirectPatches` | Mid-range co-op nights still host-centric; far client gets redirected spirits only sometimes |
| **M7** | Death bag upgrades not on wire (known) | Dropped item state / death bag | Rare weapon upgrade loss on mirror |
| **M8** | Same-PC dual instance shares AppData | JOIN_HOST_AUDIT J3/J11 | Slot 5 mitigates overwrite; profile wipe risk remains if share wrong |
| **M9** | `FinalDreamsceneManager.AllDead` requires remotes in proxy set | `FinalDreamsceneManager` | Late proxy spawn: solo-death fallback may wrongly allow vanilla end while peer exists |
| **M10** | Epilogue credits 8s delay + stop network | `EpilogueSyncPatches` | Peer desync if packet late; no co-op after credits |
| **M11** | Host migration unsupported | Design / TODO archive | Host drop = session death |
| **M12** | SyncCheck not in Path B | Deferred (see `docs/TODO.md` / PATH_B inventory) | No automatic digest heal for flags/time desync |

### LOW / residual

| ID | Finding | Notes |
|----|---------|-------|
| **L1** | Live 2-instance campaign soak | `docs/TODO.md` / `docs/PLAYTEST.md` |
| **L2** | Location/landmark placement residual | TODO #5 |
| **L3** | Enemy target nudge only | Host AI not true multi-target |
| **L4** | Physics free-body deferred | Caps omit PhysicsState |
| **L5** | Controller-free multiplayer menu | Documented UX limit |
| **L6** | Item double-pickup co-op scale | Intentional balance; can confuse economy |

### Domain coverage checklist (systematic)

| Domain | Audited surfaces | Verdict summary |
|--------|------------------|-----------------|
| Session / join | Handshake proto 19, WorldSaveShare, HostEnterWorldShare, late-join bulk, slot 5 | **PARTIAL** — join hardened (JOIN_HOST_AUDIT) but same-PC + share failure + chapter stop remain |
| World | TimeSync, doors/barricades/movables, weather, map, worldgen share | **BUG/PARTIAL** — C1 time dual-sim; C4 layout |
| Economy | Containers, drops, trade absolute, workbench, hideout | **PARTIAL** — races H6; trade restock host-OK |
| Combat | Client redirect, host AI stream, FF, death bags | **PARTIAL** — combat architecture sound; night death H3; playtest residual |
| Story | Flags, GE, EventTriggers proxy, dialog, dreams, chapter, epilogue, infection, examine | **BUG** — C2/C3/H1/H5 critical story paths |
| Death / spectate | NightDeath*, DeathStateTracker, SpectatorMode, FinalDreamscene | **PARTIAL** — design present; edge cases |
| Wire / protocol | NetMessageType Horde 19; Ironbark v2 separate | **OK LAN / BUG docs** — H7/H8 |

---

## 3. Story multiplayer edge cases

### 3.1 Concurrent dialogues

**Vanilla:** One `DialogueWindow`, outcomes hit `Player.Instance` (`DialogueWindow.cs` boards).  
**Mod:** Client sends `DialogOutcomeSync`; host `displayDialogue` (Handlers ~3990). No NPC talk lock.  
**Risk:** Dual talk; host bag pollution (**C2**); `alreadyShown` divergence.  
**Need:** Host-authoritative dialogue *session* (one speaker), apply world outcomes only (flags/events) without `giveItem` to host, or apply to virtual speaker inventory.

### 3.2 Exclusive flags / one-player-ahead

**Vanilla:** Flags global in save.  
**Mod:** Host broadcasts flags; client blocked from one-shot GE; EventTriggers proxy on host.  
**Risk:** Client-only flag writers (**H1**); 2s flag coalesce (**H2**); player A completes doctor line while B is elsewhere — B gets flag sync but may miss journal/items.  
**Need:** Treat story flags as host SOOT; client requests; journal/key policy explicit (shared keys vs personal notes).

### 3.3 Dreams while peers exist

**Vanilla:** `Dreams` freezes normal world time loop (`FixedUpdate` skips when dreaming), swaps inv, outside location.  
**Mod:** `DreamSession` + `DreamStartPatch` (client request host); death → `FinalDreamsceneManager` spectate; `DreamEndDreamingAuthorityPatch` defers client story end.  
**Risk:** Host ignores request if session active (**H4**); incomplete peer enter; death AllDead set race (**M9**); epilogue crawl correctly excluded.  
**Need:** Barrier: all peers `prepareDream`/`startDreaming` before any continues; reject joins (`ShouldRejectNewConnections` exists).

### 3.4 Chapter load races

**Vanilla:** `generateChapter` LoadScene local.  
**Mod:** Host blocks client local generate; broadcasts `ChapterTransition`; optional WorldSaveShare; **stops network**.  
**Risk:** Share timeout 12s fallback; session always ends (**C3**).  
**Need:** Persist session tokens / auto-rehost after chapter load, or stopNetwork=false with reconnect handshake.

### 3.5 Epilogue / final dream / credits

**Vanilla:** `inEpilogue` crawl/cam; `EpilogueOutcomes.goToCredits`.  
**Mod:** Coordinated `SceneLoad` credits + stop net; dream death not used for epilogue.  
**Risk:** One peer missing SceneLoad; crawl is per-player health — asymmetric epilogue progress.  
**Need:** Host-gated epilogue beats; shared credits only after all ready.

### 3.6 Reputation / morning

**Vanilla:** `startAfterNight` repGain to hideout trader; `isAfterNight` timeFreeze.  
**Mod:** TimeSync mirrors `isAfterNight` VFX without spawning trader; MorningRep skips rep if night death; night traders per-player.  
**Risk:** Dual clock still runs real `startAfterNight` on both (**C1**) → dual traders / dual rep paths fighting model C.  
**Need:** Client suppress `startAfterNight`/`startDay`/`startBeforeDay` bodies; host-only morning, then sync trader entity + isAfterNight flag.

### 3.7 Infection / examine

**Vanilla:** `Infection.spread` local; `Examinable.examine` local triggers.  
**Mod:** Host infection spawn broadcast; client examine request → host examine.  
**Risk:** Host UI side effects (**M5**); infection only grows near host simulation.  
**Need:** Host silent examine apply (triggers only); infection OK if host AI always owns splats.

### 3.8 Scenario exclusivity

**Vanilla:** One `currentScenario` per night.  
**Mod:** Client blocks `setCurrentScenario`; host ScenarioSync + CustomEvent frequency gate.  
**Risk:** Client still hits nightTime `setMe` if scenario assigned (**C1** interaction); RandomEvent client blocked.  
**Need:** Client suppress scenario `setMe` / checkFrequencies entirely; only ScenarioEventFired drives FX.

### 3.9 Death: night partial vs all-dead vs day

**Vanilla:** Night death → `skipDay` morning for the solo player.  
**Mod:** Partial night death → spectate, suppress skipDay/Save; all dead → host skipDay + broadcast. Day death normal bags.  
**Risk:** Dead player onDeath side effects (**H3**); living peer’s night objects vs host respawnAllEnemies.  
**Need:** On partial night death: skip transport/respawn/save entirely; pure spectate until morning or revive policy.

### 3.10 One-player-ahead chapter / dream

If host triggers chapter GameEvent while client is in a dream session: chapter stops network, client dream state discarded.  
**Need:** Dream-active blocks chapter transition until session ends (or force End then chapter).

---

## 4. Sync contract vs implementation (gap map)

### 4.1 Contract classes

| Class | Meaning | Examples |
|-------|---------|----------|
| **HA** Host-authoritative continuous | Host simulates; clients mirror | AI, night spawns, infection growth, random day events |
| **ER** Event-replicated | Any peer can emit; absolute/idempotent apply | Doors, containers, drops, journal keys, barricades |
| **CW** Continuous unreliable | Pose/anim streams | PlayerState, EntityState |
| **LOC** Deliberately local | Personal progression | Skills XP spend, hunger ticks, FOV mesh, personal map pin? |

### 4.2 Domain gap map (code-verified)

| Domain | Contract | Implementation | Old matrix claim | Actual |
|--------|----------|----------------|--------------------|--------|
| Handshake / IDs | HA | Horde 19 handshake | 0.1 done | **OK** |
| World save share | HA file | WorldSaveShare + slot 5 | 0.2 done | **PARTIAL** (failure modes) |
| Time / day-night / isAfterNight | HA | TimeSync 2s + **dual FixedUpdate** | 0.3 done | **BUG (C1)** |
| Flags bulk+delta | HA | Host-only FlagSync | 0.4 done | **PARTIAL (H1/H2)** |
| Apply guards | — | NetworkApplyGuard | 0.5 done | **OK** |
| Pause freeze | shared | PauseSuppression + FreezeTracker | 0.6 done | **OK** intent |
| Join bulk | HA | Light late-join bulk; skip scenario bulk | 0.9 done | **PARTIAL** (intentional skips) |
| Player pose/anim | CW | PlayerState | 1.1 done | **OK** (playtest residual) |
| Entity AI | HA stream | EntityState + client AI disable | 1.2 done | **PARTIAL** |
| Doors/gens/traps | ER | patches | 1.3 done | **OK**/playtest |
| Trade stock | HA absolute | TradeInventorySync | 2.5 done | **OK** model |
| Reputation | hybrid | live + night trader local | done | **PARTIAL** (C2 interaction) |
| Combat FF / redirect | HA | HostCombat + ClientCombat | 3.x done | **OK** architecture |
| Night death spectate | HA morning | DeathStateTracker | 3.6 done | **PARTIAL (H3)** |
| Scenarios | HA | ScenarioSync + frequency gate | 3.7 done | **PARTIAL** |
| Dialogs / GE / triggers | HA | DialogOutcome + GameEventsFired + proxy ET | 4.1–4.2 done | **BUG (C2)** / **OK** proxy ET |
| Dreams | HA session | DreamSession | 4.3 done | **PARTIAL (H4/M9)** |
| Epilogue/credits | coordinated | SceneLoad stop net | 4.4 done | **PARTIAL (C3)** |
| Chapter generateChapter | coordinated | ChapterTransition stop net | 4.5 done | **BUG continuous co-op (C3)** |
| Infection/examine | HA | patches | 4.6 done | **PARTIAL** |
| Worldgen determinism | seed/share | **share only** in Path B | implied done | **BUG docs / PARTIAL code (C4)** |
| SyncCheck | P digest | **absent** Path B | Yokyy deferred | **UNSYNCED** |
| Dedicated / Ironbark | relay | separate assembly | matrix “done” confusing | **UNSYNCED to Path B LAN** |
| Location placement | deterministic | open TODO #5 | residual | **UNSYNCED residual** |

### 4.3 Docs (current)

| Doc | Use |
|-----|-----|
| `CHANGELOG.md` | Ship + 0.9.2+ deltas |
| `DarkwoodMP.Mod/docs/COOP_COVERAGE.md` | Domain audit living truth |
| `docs/TODO.md` | Short residual list only |
| `docs/JOIN_HOST_AUDIT.md` | Join pipeline bugs + status |
| `docs/PATH_B_FEATURE_INVENTORY.md` | Path B vs Yokyy product inventory |
| `docs/PLAYTEST.md` | Dual-box checklist |
| `IRONBARK_*.md` | Dedicated wire research — not Path B LAN |
| `docs/decompile docs/*` | Game reverse-eng reference |
| Historical `SYNC_MATRIX` / `MERGE_MATRIX` / Yokyy plans | **Removed** as redundant |

### 4.4 Open residual risks

1. **Location/landmark placement** — still open if share fails before gen; Path B has no per-chunk seed rewrite.  
2. **Live 2-instance / campaign soak** — still open; unit tests only protocol + structural.  
3. **SyncCheck / InteractionLock / ItemState** — still deferred.

---

## 5. Recommendations → 0.9.2 status

1. **Client time authority off:** **DONE 0.9.2** — `ClientTimeAuthorityPatches` + TimeSync `refreshTimeNoLogic`.  
2. **Dialog outcome split:** **DONE 0.9.2** — `DialogHostApplyGuard` + personal suppress patches + NPC lock (H5).  
3. **Chapter/credits:** **PARTIAL 0.9.2** — chapter auto rehost/reconnect via `ChapterSessionResume`; credits still permanent stop (documented residual).  
4. **Night partial death:** **DONE 0.9.2** — transport/respawn/despawn suppress + existing skipDay/Save.  
5. **Doc honesty / dead matrices:** **DONE** — live truth is CHANGELOG + COOP_COVERAGE; historical matrices removed.  
6. **Playtest script:** **OPEN residual** — live 2-box campaign not required for release gate (`docs/PLAYTEST.md`).

---

## 6. Verification evidence

| Check | Result |
|-------|--------|
| Decompile symbols exist (Controller, Dreams, Flags, GameEvents, EventTriggers, DialogueWindow, WorldGenerator, NightScenarios, SaveManager, Player, Infection, EpilogueOutcomes, CharacterSpawner, RandomEvent, Examinable) | **Pass** under `Darkwood DECOMPILED\Scripts\Assembly-CSharp` |
| `dotnet test` Protocol.Tests | **13 passed** |
| `dotnet test` PathB.Tests | **30 passed** (structure + CoopPolicy unit + audit fix gates) |
| `AuditStructureTests` | Shipped-source assertions for C1–C4 / H1 / H3 fixes |
| Live 2-client campaign | **Not run** (environment / non-goal residual) |

Log: implementer scratch `test.log` / `build.log`.

---

## 7. Summary (post-0.9.2)

Path B is a **serious host-authoritative Horde co-op**. Audit criticals C1–C4 and highs H1/H3/H5 are **closed in code** for release 0.9.2. Remaining product risk is **live 2-box campaign confirmation**, landmark placement residual (C4 placement), credits end-of-session residual, and deferred dedicated/SyncCheck work. Prefer **code + CHANGELOG** over older MERGE rows that still name Ironbark as live wire.
