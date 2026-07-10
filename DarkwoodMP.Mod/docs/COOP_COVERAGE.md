# DWMP Horde Remaster — Co-op Coverage Checklist

Living audit of every sync domain against vanilla Darkwood.  
Protocol baseline: **19** · Plugin: **0.4.15** · Mode: **N-player LAN (3+ supported), host-authoritative**

**Target:** host + multiple clients (not dual-only). Happy path and playtests must consider **at least 3 humans** (host + 2 clients) unless a domain is explicitly host-local.

## Verdict key

| Status | Meaning |
|--------|---------|
| `Unchecked` | Not audited yet |
| `OK` | Code path + fallbacks look sound for multi-peer; dual- and triple-client smoke recommended |
| `Partial` | Works for 2p or happy path; known gaps (late-join, 3+, edge cases) |
| `Broken` | Proven wrong path or missing critical authority |
| `Uncovered` | No mod surface found for a vanilla system that co-op needs |

## Audit rubric (every domain)

1. **A Surface** — vanilla classes / events  
2. **B Coverage** — messages, patches, services, host vs client  
3. **C Happy path** — host does X / client A does X / client B does X  
4. **D Fallbacks** — late join, reconnect, mid-action, null IDs, bulk on connect  
5. **E Story/unique** — chapter one-shots, dialogs, dreams, location-only logic  
6. **F Failure modes** — desync, double-apply, ghosts, wrong authority  
7. **M Multi-peer (3+)** — per-`playerId` state, forward-to-others, no “only peer” assumptions, 3rd joiner while 1st is mid-action  
8. **G Verdict** + notes / next action  

### Multi-peer baseline (always check)

| Concern | Pass condition |
|---------|----------------|
| Identity | Stable `PlayerId` ≥ 2 per client; proxy/state keyed by id |
| Fan-out | Host rebroadcasts / `RemotePlayerForward` so client B sees client A actions |
| Targeting | Damage, death, dreams, drag claims address the right player |
| Late join | 3rd peer gets bulk + live entities; existing peers do not freeze |
| Disconnect | Mid-player leave cleans that id only; session continues for others |
| Persistence | Client backups / saves do not overwrite each other |

---

## Layer 0 — Infrastructure

| ID | Domain | Status | Notes |
|----|--------|--------|-------|
| 0.1 | Session / handshake / protocol | **OK** | Code closed 2026-07-09. Reliable handshake/session; multi-peer IDs; max players; dream-join reject; Direct forward reliable. WorldSession still advisory (no hard wrong-save reject — F0.5 / later). Playtest smoke still recommended. |
| 0.2 | Save / world share / load | **OK** | Code closed 2026-07-09. Per-PlayerId host backups; local self backup for ManualSave; SaveSync + WorldSaveShare to all. Mid-campaign still “load matching / Resend.” |
| 0.3 | Time / day / night cycle | **OK** | Code closed 2026-07-09. Full `isAfterNight` mirror; reliable TimeSync; immediate on join. Host owns trader/morning rewards (no client startAfterNight). |
| 0.4 | Flags bulk + delta | **OK** | Code closed 2026-07-09. Host deltas reliable; join bulk; pending apply if Flags not ready; host-only setFlag authority. |
| 0.5 | Network apply guards / reset | **OK** | Code closed 2026-07-09. Nested NetworkApplyGuard; ResetAll try/catch; expanded registry + TraverseHack transient clear. |
| 0.6 | Pause / focus / menu | **OK** | Code closed 2026-07-09. Co-op no-pause: map, journal, padlock, dialogue, leveling, skill menus, interactive item UI. FreezeTracker still freezes. Menu audio path OK (5.x polish). |

### Layer 0 audit log (2026-07-09, code review)

#### 0.1 Session — CLOSED (code) 2026-07-09
- **B:** `Handshake` (proto + PlayerId), `WorldSession`, `OnPeerConnected` bulk pack (journal/flags/rep/scenario/hideout/workbench/map + death bags + weather), connection key accept, `MaxPlayers`, dream-join reject, `RemotePlayerForward` fan-out.
- **C:** Host starts → client connects → handshake both ways → host bulk → client world session note. Menu shows LocalId / peer count / handshake flag.
- **D:** Protocol mismatch → disconnect. Peer leave: host cleans proxy/claims/death-night; client `StopNetwork`. Multi-peer: `_handshakeComplete` stays true for already-ready peers when new peer joins.
- **M:** `_nextPlayerId++`, `_peers`/`_handshakedPeers` per peer; bulk targets new peer only; host stays up when one client leaves.
- **Fixes applied (this closeout):**
  1. `Handshake` + `WorldSession` now `ReliableOrdered` (were default Unreliable — lost packet left client as LocalId=1).
  2. Direct `Forwardable` rebroadcast uses `ReliableOrdered` (was Unreliable).
  3. Multiplayer menu session line: role, LocalId, client/peer count, handshake complete.
- **Deferred (not 0.1 blockers):** wrong-save hard reject / UI warning (F0.5). Soft advisory via `ClientSaveBridge` remains.
- **Playtest smoke (user):** wrong password reject; host + 2 clients LocalId 1/2/3; 3rd join mid-session without freezing others; one client disconnect leaves host+other up.

#### 0.2 Save / world share — CLOSED (code) 2026-07-09
- **B:** `SaveSyncPatch` on `SaveManager.Save`; `ClientStateBackup` JSON; `WorldSaveShareService` deflate chunks (`savs.dat`/`sav.dat`/`savch.dat`) same profile id 1–5; `WorldGenSharePatch` after new gen.
- **C:** Either side save → peers save; each client pushes backup to host; new world gen shares to all connected clients; ManualSave self-backup around host-world load.
- **M (fixed):** Host stores `client_backup_p{playerId}.json` keyed by `_currentReceivePlayerId` (+ JSON `PlayerId` field). Local ManualSave uses `client_backup_self.json` (legacy `client_backup.json` load fallback).
- **D:** Manual “Resend world”; busy flag; `_isRemoteSaveInProgress` loop guard; save blocked during partial night death / dream (ManualSaveGUI).
- **Deferred (not 0.2 blockers):** auto-load shared save into running session; wrong-slot education UI (F0.5); client who joins *after* world gen still needs Resend.
- **Playtest smoke:** host save with 2 clients → two host files `client_backup_p2.json` + `p3.json`; new world with both connected; Resend; ManualSave load restores self inventory.

#### 0.3 Time — CLOSED (code) 2026-07-09
- **B:** Host `SendTimeSync` / `SendTimeSyncTo` — `CurrentTime`, `day`, `IsAfterNight`; period 2s; join bulk includes immediate time.
- **C:** Client applies time/day + full after-night bool + `addAfterNightEffect` / `removeAfterNightEffect` (VFX/freeze only).
- **D:** Peer leave hideout can clear host after-night (PlayerState.AfterNightActive); location paths unchanged.
- **M:** Broadcast to all peers; join targets new player only. Clients matching host after-night avoids false `AfterNightActive=false` clearing host freeze.
- **Fixes applied:**
  1. **F0.1** — apply `isAfterNight` true and false (not only clear).
  2. **F0.2** — `SendTimeSyncTo(playerId)` on peer connect.
  3. TimeSync delivery → `ReliableOrdered`.
- **Deferred:** client local time still advances between ticks then snaps (host authoritative). Inventory pause asymmetry → 0.6.
- **Playtest smoke:** survive night → both clients morning freeze VFX; leave hideout → all unfreeze; late join mid-day matches host day/time.

#### 0.4 Flags — CLOSED (code) 2026-07-09
- **B:** `FlagSyncBool/IntPatch` host-only; `TickFlush` 2s pending latest; `FlagBulkSync` per new peer; client apply under `NetworkApplyGuard`.
- **C:** Host `setFlag` → all clients; join bulk ≤4096 flags.
- **D:** Cooldown keeps latest value; bulk creates missing flags; **pending queue** if `Flags.Instance` null (menu/load) then flush when ready.
- **M:** Broadcast to all clients; join bulk targets one peer; no per-client flag authority.
- **Fixes applied:**
  1. FlagSync delivery → `ReliableOrdered` (was Unreliable).
  2. Queue bulk + deltas when Flags not ready; `TryFlushPendingFlags` after poll; clear on disconnect.
- **Deferred (by design → 4.x):** client-local `setFlag` without DialogOutcome/GameEvents stays local — story domain must route choices through host.
- **Playtest smoke:** host sets story flag on all clients; connect from menu then load → bulk applies; rapid toggles within 2s keep final value.

#### 0.5 Guards / reset — CLOSED (code) 2026-07-09
- **B:** `NetworkApplyGuard` wraps every `OnNetworkReceive`; `TraverseHack.ApplyingFromNetwork` getter ORs guard active; registry in `ModRuntime.Start`.
- **C/D:** Nested depth restore; `ResetDepth` + `ResetTransientFlags` on stop; `StopNetwork` → `ResetAll` + combat/session maps.
- **M:** Same for any peer count — disconnect one client host keeps registry; full StopNetwork clears all.
- **Fixes applied:**
  1. `ResetAll` isolates each action (try/catch) so one failure cannot skip remaining cleanup.
  2. `TraverseHack.ResetTransientFlags` (InsideCharacterSounds, explosion/bullet flags) on stop.
  3. Registered missing session maps: Door/Generator trackers, HostCheckStuff, gas trail pending, throwable captures, trade mutex, ClientSaveBridge, DroppedItemIdentifier registry, dialog choice index.
- **Process rule:** new static session state **must** `NetworkResetRegistry.Register` or list in this domain.
- **Playtest smoke:** disconnect during combat; reconnect; no stuck mute-rebroadcast / frozen trade mutex / stale drop GUIDs.

#### 0.6 Pause — CLOSED (code) 2026-07-09
- **B:** `PauseSuppression` + Core pause/unpause prefixes; `FreezeTracker` for intentional multiplayer freezes (dreams).
- **C:** While connected, opening map/journal/padlock/dialogue/leveling/skills/interactive UI does not set `Time.timeScale=0` for that peer.
- **Inventory note:** vanilla inventory does **not** call `Core.pause` — no patch required.
- **M:** Gated on `IsConnected` (offline SP keeps vanilla pause for those UIs when disconnected).
- **Fixes applied (F0.3):**
  1. DialogueWindow SetDialogue + close
  2. LevelingMenu show (hold) + hide
  3. SkillPointsMenu / SkillSlotsMenu open/close
  4. InteractiveItem open/close
  5. `MultiplayerActive = IsConnected`; `PauseSuppression.Reset` on disconnect
- **Still intentional pause:** MainMenu, endgame/death screens, FreezeTracker dreams — not suppressed.
- **Playtest smoke:** host opens dialogue while client fights; leveling dose; map; peer keeps moving / day advances.

---

## Layer 1 — Continuous world state

| ID | Domain | Status | Notes |
|----|--------|--------|-------|
| 1.1 | Player pose / anim / vault | **OK** | Code closed 2026-07-09. ~30Hz pose + event anim/library; vault per-PlayerId (protocol 7); 3+ host rebroadcast PlayerState. Handheld lights split to **1.8**. |
| 1.2 | Entity AI + snapshots | **OK** | Code closed 2026-07-09. Host AI + ~10Hz EntityState to all peers; client AI off; EntitySpawn Broadcast + host relay; round-robin cap. |
| 1.2b | Entity presentation (anims / reactions) | **OK** | Code closed 2026-07-09. Pipeline answers; stop 10 Hz frame scrub; death/clip on pending; legsAnimator. Families: code OK, playtest still recommended. |
| 1.3 | Physics / doors / gens / traps | **OK** | Code closed 2026-07-09. Host multi-center scan (local+proxies); client free-body → host fan-out; doors/traps/gens host auth + event Broadcast. |
| 1.4 | Drag / push / scrape audio | **OK** | Code closed 2026-07-09. Claim + DragSync Forwardable; ForceStop on end; body-push MOS; multi-peer fan-out fixes. |
| 1.5 | Locations enter/exit | **OK** | Code closed 2026-07-09. PlayerId + exit pos; no leaveAllLocations; Forwardable; protocol 8. |
| 1.6 | Weather | **OK** | Code closed 2026-07-09. Host rain/fog events + join bulk; client schedule suppressed; visual apply fixed. |
| 1.7 | Map markers / discovery | **OK** | Code closed 2026-07-09. Marker PlayerId + multi-peer bulk; discovery loop guard; protocol 9. |
| 1.8 | Lights (player + world) | **OK** | Code closed 2026-07-09; residuals stomped same day. Flare B+ + held FX; conditional pose light payload; flashlight stream; ambient full snapshot; world join bulk + ReliableOrdered. Protocol **19** / **0.4.13**. Playtest smoke below. |

### Layer 1 audit log

#### 1.1 Player pose / anim / vault — CLOSED (code) 2026-07-09
- **B:** `PlayerState` ~30Hz (pose, locomotion, clips, after-night flag); `PlayerAnimation` / `PlayerAnimLibrary` events; `VaultState`; proxies via `RemotePlayerProxy` / `SecondPlayerAnimController`. Lights moved to **1.8**.
- **C:** Each peer Broadcasts own state; host applies client → proxy + `SendToAllExcept` for 3+; client applies host + forwarded peers by `state.PlayerId`.
- **M:** PlayerState rebroadcast on host; anim ForwardablePlayer; vault Forwardable + **PlayerId**.
- **Fixes applied:** vault PlayerId + Forwardable; AnimLibrary ReliableOrdered; protocol 7.
- **Playtest smoke:** walk/run both ways; vault window; 3rd peer sees both remotes; weapon equip changes proxy torso library.

#### 1.8 Lights (player + world) — CLOSED (code) 2026-07-09
- **B / vanilla:** Flashlight (`processFlashlight` continuous radius/color); torch emitters + particles; hotbar lantern ambient (`modifyLightDot`); held flare (`Flare` flicker + particles/sprite, not `activated`); thrown flare prefab Light2D; world `Item.isLight`/`switchable` turnOn/Off + generator fan-out.
- **B / mod:**
  - **Flare B+ light:** continuous sole owner; light **parented to proxy** + local hand offset; no event `HasItemLight` for flares.
  - **Flare FX (residual stomp):** rising-edge held prefab clone (`RemoteFlareFx_P*`) — particles + sprite; strip `Flare`/physics/`Light2D` on clone so longevity doesn’t kill light; destroy with light.
  - **Flashlight stream:** radius/intensity/color when active (dirty / rising / 1 Hz force).
  - **Payload (proto 19):** `LightFlags` byte — inactive = **1 byte**; flare/flash fields only when active; params conditional.
  - **Events:** `PlayerLightState` for torch emitters, ambient, flashlight edge; join `SyncCurrentLightState`.
  - **World:** `LightState` **ReliableOrdered**; host `SyncExistingWorldLightsTo` on peer connect.
  - **Throw:** `EnsureThrownFlareLight` on `ThrowableSpawn`.
  - **Debug:** `VerboseLightSync` config — transition logs only (`[LightSync]`).
- **C:** Equip torch/flashlight/flare; throw flare; toggle world lamp; gen-linked lights; late join with lamps already on.
- **M:** Continuous lights inside `PlayerState` (host rebroadcast); `PlayerLightState` ForwardablePlayer; world LightState Forwardable.
- **Fixes applied:**
  1. Unify held flare to continuous B+ (kill dual path).
  2. Ambient full snapshot (no clobber).
  3. Burnout resync for all light-capable types.
  4. World lights ReliableOrdered + join bulk.
  5. Flashlight continuous stream.
  6. Protocol **18** / **0.4.12** (initial domain).
  7. Residual stomp: held flare FX + conditional payload + smoke docs — **Protocol 19** / **0.4.13**.
- **Residuals:**
  | Residual | Status |
  |----------|--------|
  | Held flare light-only | **Stomped** — FX clone on rising edge |
  | Always-on float tax | **Stomped** — LightFlags + conditional params |
  | Dual-client proof | **Process** — smoke sheet below; not auto-signed |
- **Deferred (non-blocking):** mid-session location-load world light bulk if lamps spawn after join without toggle.
- **Playtest status:** code closed; **human dual/triple smoke not signed off** (use sheet below).

##### 1.8 mandatory smoke sheet (dual / triple client)

Set `VerboseLightSync = true` on at least one client for log greps. Both installs must be **0.4.13 / protocol 19**.

| # | Steps | Pass if |
|---|--------|---------|
| 1 | Host flashlight on; aim near/far | Peer cone strength changes; log `flashlight ON` |
| 2 | Equip torch | Peer hand light + fire particles |
| 3 | Hotbar lantern, empty hand then melee | Ambient only; no double vision cone |
| 4 | Equip flare | Peer: **one** light + flame FX on hand (not body-center only) |
| 5 | Throw flare | Hand light/FX off; ground light on |
| 6 | World lamp / gen on-off | Peers match |
| 7 | 3rd peer joins lit hideout | Lamps already on; log `World lights → pN: on=` |
| 8 | Burn light durability to 0 | Peers go dark |
| 9 | Rapid switch torch ↔ flashlight ↔ empty | No stuck emitters / lights |
| 10 | Unequip flare / death while holding flare | No orphan `RemoteFlareLight` / `RemoteFlareFx` |

Log greps: `[LightSync]`, `[BulkSync] World lights`, `[ThrowableSpawn] flare`.

#### 1.2 Entity AI + snapshots — CLOSED (code) 2026-07-09
- **B:** Host `EntityStateBroadcastService` (~10 Hz, unreliable) to all peers; client `ClientEntityInterpolationService` + phantom match; `ClientAIDisable*` (host-authoritative AI); host multi-player awareness via `PlayerPositionManager` + HostAIPatches; Characters via stable ID (not EntitySpawn); non-character prefabs via `EntitySpawn`.
- **C:** Host runs AI near any player; clients only interpolate; range 3500u of host **or any remote**.
- **M:** EntityState multi-peer send; remotes in cull via `IsAnyRemoteWithinSq`; Host AI nearest-player helpers multi-dict.
- **Fixes applied:**
  1. `EntitySpawn` **Broadcast** (was `Send` = first peer only).
  2. Host **relays** client-originated EntitySpawn to other clients.
  3. Snapshot **round-robin** scan + cap **192** (was linear 128 starvation).
  4. Skip id=0 snapshots; clear `_paused` on `Stop()`.
- **Deferred:** deeper host combat targeting polish if any (3.x). Presentation depth closed in **1.2b**.
- **Playtest smoke:** client-only combat sees host AI chase; split map two clients both get nearby mobs; host traps/items spawn on all clients.

#### 1.2b Entity presentation (anims / reactions) — CLOSED (code) 2026-07-09

**Purpose:** 1.2 = authority pipeline. 1.2b = presentation depth on clients (clips, hitreacts, deaths).

##### Pipeline answers

| # | Question | Answer |
|---|----------|--------|
| 1 | `EntityState` payload? | **Pos, RotY, Clip, ClipFrame, Alive, HealthPct, EntityName, PrefabPath** (~10 Hz Unreliable). |
| 2 | Attack/hitreact/death events? | **No dedicated anim events.** Clip names ride dirty EntityState. SFX = **EntitySound (5.1)**; damage = **3.1**. |
| 3 | Phantom match? | Name+position; phantom from PrefabPath; swap when real chunk entity appears. |
| 4 | Death? | `Alive=false` → client `die()` + death clip; next dirty packet ~100 ms if one drops. |
| 5 | Cull? | Host sends within 3500 of host **or any remote**. |
| 6 | 3+? | EntityState to all peers; multi-remote range. |

##### Systemic fix this closeout

| Issue | Fix |
|-------|-----|
| **10 Hz `SetFrame` scrub** | Alive + same clip → natural tk2d FPS (attack/hitreact no longer stutter) |
| **Frame snap** | Only on **clip change**; **dead** holds host death frame |
| **Pending match** | Apply presentation when matched / activated / phantom-spawned |
| **Animators** | Host samples `Character.animator`; client drives body + **legsAnimator** |

##### Shared P1–P10 (all families via same pipeline)

| # | Behaviour | Status |
|---|-----------|--------|
| P1 Spawn | PrefabPath / Characters/name + idle | **OK** (code) |
| P2 Locomotion | Pos lerp + RotY + host walk/run clips | **OK** (code) |
| P3 Aggro | Host clip change → client Play | **OK** (code) |
| P4 Attack | Clip change + natural play | **OK** (code) |
| P5 Hitreact | Host clip if any | **OK** (code) |
| P6 Death | Alive=false + death clip + freeze frame | **OK** (code) |
| P7 Burn | **3.3** EntityBurning | **OK** (cross) |
| P8 Special | Clip-driven + **4.10** infection etc. | **Partial** |
| P9 Audio | **5.1** EntitySound | **OK** (cross) |
| P10 Late join | Pending + phantom + snapshots | **OK** (code) |

##### Family matrix

| ID | Family | Status |
|----|--------|--------|
| 1.2b.1–6 | Chompers, dogs, livestock, humanoids, spiders, swamp | **OK** (code) |
| 1.2b.7 | Elite / named | **Partial** — playtest bosses |
| 1.2b.8 | Burning variants | **OK** (+3.3) |
| 1.2b.9 | Night shadows | **OK** (primary **3.5**) |
| 1.2b.10 | HidingPlace AI | **OK** (+4.11 spawn) |
| 1.2b.11 | NPC non-combat | **OK** (code) |

##### Deferred

Dual-client smoke per family (especially elites); separate legs-clip channel if bipeds desync; reliable death one-shot if needed.

**Status: OK (code).** Presentation pipeline closed; elite playtests recommended.

#### 1.3 Physics / doors / gens / traps — CLOSED (code) 2026-07-09
- **B:** `PhysicsState` ~10 Hz bulk; event `SendDoorState` / trap / generator (ReliableOrdered Broadcast); client `TrapTriggered` → host; door/gen patches; `WorldPhysicsSyncService` apply + interp.
- **C:** Host owns doors/traps/gens; free rigidbodies bidirectional (client push → host apply + rebroadcast); host scan includes all remote proxies.
- **M:** Client origin PhysicsState `SendToAllExcept`; door/trap/gen events already Broadcast; multi-center free-body scan on host (was host-player-only).
- **Fixes applied:**
  1. Host physics OverlapSphere around **local + every remote proxy** (deduped by instance id).
  2. Trap detect included in multi-center scan (removed separate proxy-only trap loop).
- **Already solid:** client door open/close → host + fan-out event style; strip bulk door/trap/gen from client free-body packets; drag skip.
- **Playtest smoke:** open door far from host; client push furniture both see; trap spring both; gen fuel far client.

#### 1.4 Drag / push / scrape audio — CLOSED (code) 2026-07-09
- **B:** `DragSync` (~30 Hz, Forwardable); drag claims; `ItemMovingSoundHelper.ForceStop` + MOS; body-push via PhysicsState → `NotifyBodyPushStarted/Stopped` + `PlayerAudio` stop signal.
- **C:** One claimer per object name; remote kinematic lock on client; host rebroadcasts DragSync; scrape stops on release (vanilla 0.5s fade, immediate decision).
- **M:** DragSync Forwardable; disconnect clears claims + ForceStop; body-push audio excludes pusher client, Broadcast when host is pusher.
- **Fixes applied:**
  1. Clear `_dragClaims` on `StopNetwork`.
  2. Drag-end payload includes `ClaimedByPlayerId`; local claim clear + ForceStop; claim only set while `IsDragging`.
  3. Peer disconnect ForceStop on that player's claimed objects.
  4. `BroadcastBodyPushSound`: `SendToAllExcept` only when receive id valid; else full Broadcast.
- **Already solid:** DragClaimStart/Stop patches; residual vel Sleep in ForceStop; no PhysicsState thrash while claimed.
- **Playtest smoke:** E-drag chair scrape starts/stops clean; second player blocked with message; body-push across map; 3rd peer sees drag move.

#### 1.5 Locations enter/exit — CLOSED (code) 2026-07-09
- **B:** `LocationEnter` / `LocationExit` (~1 Hz while inside + edge); OutsideLocations spawn/enter geometry; proxy teleport.
- **C:** Entering peer activates location children + snaps **that** proxy to playerSpawn; exit snaps **that** proxy to sender world pos. Local player never transported by remote messages.
- **M:** Messages Forwardable; PlayerId in payload (Direct forward no longer teleports host proxy by mistake).
- **Fixes applied:**
  1. Removed `leaveAllLocations()` on remote exit (was nuking local player location state).
  2. Exit uses **sender world position**, not local `positionCopy`.
  3. PlayerId on enter/exit; `[Forwardable]` both.
  4. Protocol **8** / plugin **0.4.2**.
- **Playtest smoke:** host stays outside while client enters bunker (host still in world); both enter same basement; third client sees both proxies; exit returns proxy to woods not wrong coords.

#### 1.6 Weather — CLOSED (code) 2026-07-09
- **B:** Host `WeatherSync` on rain/fog start/stop; `SendWeatherSyncTo` on handshake; message carries rain/fog/lightning timers.
- **C:** Host Rain is authority; clients apply visuals + host timers; multi-peer = Broadcast to all (bulk/all).
- **Fixes applied:**
  1. HandleWeatherSync client-only; do not pre-set `raining` before `Raining` setter (was skipping startRain visuals).
  2. Re-apply host lightning/duration after startRain.
  3. Client `Rain.onUpdateTime` suppressed for autonomous schedule (keep local lightning FX only).
- **Already solid:** join-time weather push; host-only patches for start/stop.
- **Playtest smoke:** host rain starts → all clients rain; stop → all stop; late join mid-rain; fog in/out.

#### 1.7 Map markers / discovery — CLOSED (code) 2026-07-09
- **B:** Custom co-op markers (RMB on map); `MapMarker` / `MapMarkerRemove`; `MapElementDiscovered`; join `MapStateSync`.
- **C:** Local markers blue, remote green; discoveries shared via showElement.
- **M:** Marker messages carry **PlayerId** (Direct forward no longer mis-attributes to host); MapStateSync includes all known markers with owners.
- **Fixes applied:**
  1. PlayerId on place/remove; skip own id as remote.
  2. MapStateSync multi-owner bulk; apply as remote not LocalMarkers.
  3. Discovery re-broadcast blocked under `IsApplyingRemoteState` / TraverseHack.
  4. Protocol **9** / plugin **0.4.3**.
- **Playtest smoke:** both place markers different colors; remove; late join sees all; discover landmark → other maps update.

## Layer 2 — Inventory & economy

| ID | Domain | Status | Notes |
|----|--------|--------|-------|
| 2.1 | Containers | **OK** | Code closed 2026-07-09. Item take/place/search; open state request→host; state sync to requester only; Forwardable deltas. |
| 2.2 | Dropped items | **OK** | Code closed 2026-07-09. GUID spawn/pickup; late-join bulk; consume-guard; Forwardable. |
| 2.3 | Death bags | **OK** | Code closed 2026-07-09. BagId spawn/loot/dedup; water; late-join; looted-id set; Forwardable. Playtest still recommended. |
| 2.4 | Journal / notes / keys | **OK** | Code closed 2026-07-09. Live JournalItem Broadcast + Forwardable; join bulk + pending; late-join world despawn. |
| 2.5 | Trade | **OK** | Code closed 2026-07-09. Absolute `TradeInventorySync`; host restock; join bulk; success-only post-trade. Protocol **10** / **0.4.4**. |
| 2.6 | Reputation | **OK** | Code closed 2026-07-09. Model C: shared live+bulk; night-trader per-player + ClientStateBackup. Protocol **11** / **0.4.5**. |
| 2.7 | Workbench / construction / hideout | **OK** | Code closed 2026-07-09. Workbench Broadcast; constructible late-join + skip-dup; hideout join bulk. |
| 2.8 | Compressor / oxygen | **OK** | Code closed 2026-07-09. Convert handler unblocked; any-peer compressor; inv+hotbar. |
| 2.9 | Saw / fuel | **OK** | Code closed 2026-07-09. Absolute SawState; late-join bulk; pending; quiet fuel-only. Generator fuel already via PhysicsState (1.3). |
| 2.10 | Skills / XP | **OK** | Code closed 2026-07-09. Per-player (no live sync); ClientStateBackup XP+skills+points restore. |

### Layer 2 audit log

#### 2.1 Containers — CLOSED (code) 2026-07-09
- **B:** `ContainerItem` (Take/Place/Remove/Searched); open → client `ContainerStateRequest` → host `ContainerStateSync`; patches on grab/transfer/place/search.
- **C:** Shared container invent host-authoritative after open sync; live deltas Forwardable to all peers.
- **M:** State snapshot **SendToPlayer(requester)** only (was Broadcast stomping multi-open); Searched Broadcast when client opens; ContainerItem Broadcast + Forwardable.
- **Fixes applied:**
  1. ContainerStateSync targeted to requester (not all clients).
  2. On client open request, host marks searched + Broadcast Searched for hover parity.
  3. SendContainerAction always Broadcast (client→host path uses Broadcast→host).
- **Deferred:** simultaneous same-slot dual-loot (optimistic local take) — host empty warning only; full host-reject refund is 2.x polish.
- **Playtest smoke:** host loot crate → client open empty; client take → host empty; place item shared; two clients different crates; 3rd sees deltas.

#### 2.2 Dropped items — CLOSED (code) 2026-07-09
- **B:** `DroppedItemSpawn` / `DroppedItemPickup` (GUID); `DroppedItemIdentifier`; drop from inventory patches; Forwardable.
- **C:** Dropper spawns local + Broadcast GUID; peers spawn mirror; pickup Broadcast destroys all mirrors + consume set.
- **M:** Spawn/pickup Broadcast + Forwardable; late-join `SyncExistingDroppedItems`.
- **Fixes applied:**
  1. Late-join bulk of live GUID drops to new peer.
  2. Mark GUID consumed on local pickup send; block re-pickup if already consumed (destroy ghost).
  3. Pickup handler always tries destroy even if GUID already marked.
- **Deferred:** true simultaneous dual-grant (both get item same frame) without host inventory authority.
- **Playtest smoke:** drop → peer sees; pick → peer gone; late join sees ground loot; double-click ghost blocked.

#### 2.3 Death bags — CLOSED (code) 2026-07-09
- **B:** `DeathBagSpawn` / `DeathBagLooted` with **BagId**; `DeathBagNetworkId`; dropBody Postfix; Inventory.hide empty; late-join `SyncExistingDeathBags`; Forwardable both.
- **C:** Die → bag all peers; empty loot → destroy all peers; water prefab flag; mid-loot via deathDrop container path (2.1).
- **M:** Broadcast + Forwardable; registry BagId; looted-id blocks re-spawn.
- **Fixes this closeout:**
  1. `_lootedDeathBagIds` — skip spawn/retransmit after loot; clear on session reset.
  2. Late-join skips empty + looted bags.
  3. Local loot marks looted + unregisters before Broadcast.
- **Prior work kept:** BagId vs fragile float keys; water; dedup on spawn.
- **Playtest smoke:** die → peer bag; empty → gone all; die in water; late join sees bag; third peer gets spawn/loot.

#### 2.4 Journal / notes / keys — CLOSED (code) 2026-07-09
- **B:** `JournalItem` (Note/Key/QuestItem/JournalEntry); join `JournalBulkSync`; patches on note/key/quest pickup + `addJournalEntry`.
- **C:** Shared journal (all peers get notes/keys/entries); host bulk is source of truth at join.
- **M:** Live pickups **Broadcast** + **Forwardable** (was host `Send` → first peer only); bulk `SendToPlayer` on join.
- **Fixes this closeout:**
  1. `JournalSyncHelpers` uses `Broadcast` so host pickups reach all N clients.
  2. Pending journal bulk if `UI.journal` not ready (flag-style flush).
  3. After bulk: despawn world notes/keys/quest already in journal; retry when Player/Controller exists.
- **Already solid:** ReliableOrdered; ContainsKey guards; NetworkApplyGuard blocks re-send; destroy on live `HandleJournalItem`.
- **Deferred:** streamed-chunk ghosts until area load/re-interact; dual same-frame pickup popup spam.
- **Playtest smoke:** host note → all journals + world gone; client key → host + 3rd; late join has keys + no ground ghosts; story entry fans out.

#### 2.5 Trade — CLOSED (code) 2026-07-09
- **B:** Live post-`acceptTrade` absolute stock (`TradeInventorySync`); join bulk all `NPC.trader`; host-only `randomizeTraderInv` + push; legacy remove-delta `TradeSync` still handled.
- **C:** Shared merchant assortment (model C); reputation untouched (2.6).
- **M:** `TradeInventorySync` **Broadcast** + **Forwardable** (was host `Send` first-peer-only on live path); join `SendToPlayer`.
- **Fixes this closeout:**
  1. Success-only sync (failed trade leaves trays → no peer stock mutate).
  2. Absolute type→amount snapshot after trade (covers buy depletion + sell-into-shop).
  3. Clients skip independent trader restock; host randomize → Broadcast stock.
  4. Late-join `SendTradeInventoriesTo`; pending queue if NPC not loaded yet.
  5. Protocol **10** / plugin **0.4.4**.
- **Deferred:** simultaneous dual-trade same unique item; per-slot durability in shop stacks; empty-accept no-op push noise.
- **Playtest smoke:** host buy last item → peers stock gone; client sell into shop → peers see item; morning restock same on all; late join matches host shop.

#### 2.6 Reputation — CLOSED (code) 2026-07-09
- **B:** Live `ReputationSync` on `NPC.set_reputation`; join `ReputationBulkSync`; morning `startAfterNight` / `MorningRepPatch`; `ClientStateBackup.NightTraderReputations`.
- **C:** Model **C hybrid** — shared story NPCs; per-player `isNightTrader` (NightTrader + The Three).
- **M:** Live **Broadcast** + **Forwardable** (was host-only first-peer `Send`); bulk apply skips night-trader rep (dead still synced).
- **Fixes this closeout:**
  1. Shared rep emits from any peer (client trade → host → other clients).
  2. Host applies live ReputationSync (not client-only).
  3. Bulk never overwrites night-trader standing; creates missing shared states; applies dead for all.
  4. `ReputationSyncUtil.IsPerPlayerReputationNpc*` prefers `Character.isNightTrader`, name fallback.
  5. ClientStateBackup collect/restore night-trader rep per playerId.
  6. Protocol **11** / plugin **0.4.5**.
- **Already solid:** MorningRepPatch skips +rep when `SkipMorningRepBonus` (local night death).
- **Deferred:** direct `Flags.npcStates.reputation` writes from GameEvent (bypass setter); host push of stored peer night-trader rep on rejoin without local restore; full 3.7 night-death orchestration polish.
- **Playtest smoke:** host Doctor rep → all; client trade Doctor → host+3rd; NightTrader morning +rep only for survivors; bulk join does not clobber client NightTrader standing; save/restore night-trader via backup.

#### 2.7 Workbench / construction / hideout — CLOSED (code) 2026-07-09
- **B:** `WorkbenchLevel` live + `WorkbenchLevelSync` join; `ConstructibleConstruction` live + join site bulk; `HideoutUpgrade` live + `HideoutStateSync` ovens join.
- **C:** Shared workbench level, constructions, oven on/off (host join bulk authoritative).
- **M:** Workbench **Broadcast** + Forwardable (was first-peer `Send`); constructible/hideout already Broadcast+Forwardable.
- **Fixes this closeout:**
  1. Workbench emit only when level actually increases; Broadcast to all N peers.
  2. Join `WorkbenchLevelSync` uses same UI refresh path as live handler.
  3. Constructible: skip if already `constructed` (no double gameEvent); pending queue if GO not loaded; host site registry + join bulk; scan remaining `constructed==true`.
  4. Hideout patches require `IsConnected`; join apply client-only; single machine scan per bulk.
- **Deferred:** constructibles destroyed after build (component gone) only recoverable via world save / gameEvent side effects; dual same-frame construct races.
- **Playtest smoke:** upgrade workbench → all see level; build furniture → peers construct; late join sees prior builds + oven states + workbench level.

#### 2.8 Compressor / oxygen — CLOSED (code) 2026-07-09
- **B:** `OxygenTankStash` (empty tank fan-out); `CompressorTankConvert` (empty→full on compressor GameEvents).
- **C:** Shared empty-tank grant; shared convert when any peer runs compressor (co-op swamp gear).
- **M:** Both Forwardable + Broadcast (already); convert detect was host-only → any peer.
- **Fixes this closeout:**
  1. **Critical:** `CompressorTankConvertHandler` no longer early-outs on `IsApplyingRemoteState` (always true under NetworkApplyGuard → convert never ran).
  2. Host also applies convert (client compressor use fills host tanks).
  3. Compressor detect on any peer (not host-only).
  4. Convert / has-empty checks inventory **and** hotbar.
  5. `IsConnected` gates on acquire/detect patches.
- **Deferred:** late-join tank inventory parity (per-player inventory; not shared world state); compressor name-heuristic false positives.
- **Playtest smoke:** P1 gets empty tank → P2 gets one; host/client use compressor → others empty→full (hotbar too).

#### 2.9 Saw / fuel — CLOSED (code) 2026-07-09
- **B:** `SawState` on `addFuel` / `convert` (fuel + woodLog + wood amounts); join `SendSawStatesTo`.
- **C:** Shared hideout saw stock/fuel (absolute snapshot).
- **M:** Broadcast + Forwardable (already solid).
- **Fixes this closeout:**
  1. Late-join bulk of all loaded saws.
  2. Pending queue if Saw not in scene yet.
  3. Convert SFX only when wood stock changes (not pure fuel top-up).
  4. Safe refresh when convertFuelBtn UI not wired.
  5. Shared `SawSyncHelpers` + dual apply-guard check.
- **Related (not this domain):** generator fuel/on via `PhysicsState` multi-center (1.3).
- **Deferred:** dual same-frame convert race (optimistic local).
- **Playtest smoke:** add fuel → peers fuel match; convert log → peers wood/logs/fuel; late join matches host saw.

#### 2.10 Skills / XP — CLOSED (code) 2026-07-09
- **B:** No live skill/XP network traffic. Reserved `PlayerSkillsSync` (102) is a permanent no-op. Persistence via `ClientStateBackup` on save (host stores per-playerId).
- **C:** **Per-player** experience, level, skill points, chosen skills, skill uses — never host-authoritative shared.
- **M:** N/A for live; backup JSON path already multi-client keyed (`client_backup_p{id}.json`).
- **Fixes this closeout:**
  1. **Restore gap:** backup collected `Skills` / `AvailableSkillNames` but never re-applied — now restores via progressionSkills match + `initialize(false)` (vanilla loadValues shape).
  2. Collect/restore `SkillPoints`.
  3. Skill name key = `gameObject.name` (vanilla save type).
  4. Documented no-op `HandlePlayerSkillsSync` (defense against stale packets).
- **Already solid:** leveling/skill menus do not pause world (0.6); XP/level numbers were already restored.
- **Deferred:** full recipe list restore (collected, not domain-critical); host auto-push of peer backup skills without local restore path.
- **Playtest smoke:** two players different levels/skills; save/reload client keeps own skills; peer kill XP does not change host level.

**Layer 2 complete (code).** Next: Layer 3 combat.

## Layer 3 — Combat & threats

| ID | Domain | Status | Notes |
|----|--------|--------|-------|
| 3.1 | Melee / hitscan / projectiles | **OK** | Code closed 2026-07-09. Host-auth PvE; targeted DamagePlayer; FF debounce + AttackerPlayerId; blood for all victims. |
| 3.2 | Friendly fire / explosions | **OK** | Code closed 2026-07-09. Reliable ExplosionTrigger; player-blast respects FF; env blasts always hurt; targeted proxy dmg. |
| 3.3 | Throwables / gas / fire | **OK** | Code closed 2026-07-09. Throwable host combat; gas trail bulk join; nest-safe ignite; trail dedupe. |
| 3.4 | Barricades / world melee | **OK** | Code closed 2026-07-09; re-audit fix pass same day. Join bulk: partial door HP + furniture; window vanilla setBarricadeState; destroy dedupe; FX suppress; door find fallback. |
| 3.5 | Shadows | **OK** | Code closed 2026-07-09. Spawn+state sync; reliable death; join bulk; multi-proxy aggro. |
| 3.6 | Night spawn / scenarios / random events | **OK** | Code closed 2026-07-09. Join ScenarioStateSync applies; client events host-only; RandomEvent client block; multi-proxy spawn redirect. |
| 3.7 | Night death / spectator | **OK** | Code closed 2026-07-09. Host-only morning; hold death state; spectate retarget 3+; participant count raise on join. |
| 3.8 | Player death lifecycle | **OK** | Code closed 2026-07-09. Die→proxy death pose; bag spawn/loot anti-echo; revive via PlayerState; bags 2.3. |

### Layer 3 audit log

#### 3.1 Melee / hitscan / projectiles — CLOSED (code) 2026-07-09
- **B:** Client `PlayerAttack` (melee/hitscan/projectile → host); host `getHit` on entities; `DamagePlayer` targeted to victim; `FriendlyFire` host-relay for PvP; `PlayerFiredWeapon` VFX; `BulletImpact` FX; `MeleeWorldHit` doors/windows/items; host melee→proxy via `HostMeleeSensorPatch`.
- **C:** Host authoritative PvE damage; PvP via host FF relay with VictimPlayerId; local shooter VFX + peer muzzle from PlayerFiredWeapon.
- **M:** DamagePlayer is **SendToPlayer only** (legacy broadcast refuses multi-peer); FF uses AttackerPlayerId + VictimPlayerId; weapon fire ForwardablePlayer.
- **Fixes this closeout:**
  1. FF host debounce (ProxyDamagePatch + OnCollisionEnter double path).
  2. ProxyDamagePatch client path sets **AttackerPlayerId**.
  3. Proxy collision bullet relay local debounce.
  4. FF blood VFX for remote victims (not only host self-hit).
  5. HostMelee blood spawn preserves TraverseHack outer flag.
  6. Client damage redirect carries `canCutInHalf` from current weapon.
- **Already solid:** SanitizePeerDamage; ClientMeleeSensor debounce; hitscan/projectile redirect; no cone FF on PlayerFiredWeapon; MeleeWorldHit host-only apply.
- **Deferred:** full shotgun pellet parity FX; unsynced far entities name-fallback misses; dual client simultaneous hit races on same NPC.
- **Playtest smoke:** client melee/shoot NPC dies host+client; host shoot client (FF on); client A shoot client B only B takes damage; 3rd peer sees blood/muzzle.

#### 3.2 Friendly fire / explosions — CLOSED (code) 2026-07-09
- **B:** `FriendlyFire` (3.1); `ExplosionTrigger` + visual path; `ExplosionSpawnObject`; host `ExplosionFriendlyFirePatch` on `Explodes.explode`; client mute local throw combat; `ThrowableSpawn` (host combat / client visual).
- **C:** Host authoritative blast damage to remotes; environmental always hits all; player-thrown respects `FriendlyFireEnabled` for teammates (thrower still takes own blast).
- **M:** DamagePlayer targeted per proxy; ExplosionTrigger **ReliableOrdered** (was default Unreliable); ExplosionSpawnObject reliable Broadcast.
- **Fixes this closeout:**
  1. `SendExplosionTrigger` → ReliableOrdered (lost packets desynced barrels/molotovs).
  2. Player-sourced blasts: if FF off, skip non-thrower proxies; env blasts unchanged.
  3. Skip dead proxies; clamp explosion peer damage to MaxPeerDamage.
  4. Explosion spawn apply preserves TraverseHack outer flag.
- **Already solid:** client visualOnly throws; host combat spawn; proxy-spawned throw skip double ExplosionTrigger; ExplosionFriendlyFire uses SendDamagePlayer(playerId).
- **Deferred:** chain-reaction timing parity; explosion canSee LOS edge cases.
- **Playtest smoke:** barrel kills all remotes; molotov FF on hurts all; FF off molotov hurts thrower+NPCs only; third peer sees FX.

#### 3.3 Throwables / gas / fire — CLOSED (code) 2026-07-09
- **B:** `ThrowableSpawn` (host combat / client visual+mute); `GasTrailSpawn` batch; `GasIgnite`; `LiquidStopBurning`; `EntityBurning` / `PlayerBurning`; molotov combat path ties to 3.2 explosions.
- **C:** Host authoritative thrown combat; shared gas puddles + ignition; burn VFX on entities/proxies.
- **M:** Throwable ForwardablePlayer; gas Forwardable Broadcast; PlayerBurning ForwardablePlayer; join `SendGasStateTo`.
- **Fixes this closeout:**
  1. **Nest-safe** `TraverseHack` in SpawnGasTrail/IgniteGasAtPos (nested ignite no longer clears apply flag mid-call → re-broadcast risk).
  2. Gas trail **dedupe** if liquid already near position (late-join / double pour).
  3. **Late-join bulk** of flammable liquids + burning state (cap 256).
  4. `IsConnected` gates on gas/throw/burn patches; nest-safe burn apply handlers.
- **Already solid:** client throw mute; host combat spawn; gas batch flush; entity burn host-only DoT.
- **Deferred:** gas trail transform sync after pour drift; burn duration full effect parity on proxies.
- **Playtest smoke:** pour gas all peers see trail; ignite chain; late join sees puddles on fire; molotov throw host+client; player burn on proxy.

#### 3.4 Barricades / world melee — CLOSED (code) 2026-07-09
- **B / vanilla:** `Door.getHit` (boards then main HP; metal needs `canDamageMetal`); `Window.getHit` **only if barricaded** (no glass HP); `Item.getHit` if destructible; `MeleeSensor` uses `barricadeDamage`.
- **B / mod:** `BarricadeEvent` build/damage/destroy (type 0 door / 1 window / 2 item); client `MeleeWorldHit` → host `getHit`; join `SendBarricadeStateTo`; pending queue; `_processingBarricadeEvent` loop guard.
- **C:** Shared fortification; host-auth world melee; late join matches boards + partial door HP + furniture HP.
- **M:** BarricadeEvent Forwardable Broadcast; MeleeWorldHit host-only apply.
- **Fixes (initial closeout):** join bulk destroyed/barricaded; pending; `SanitizePeerDamage`; item apply-guard skips.
- **Fixes (re-audit pass):**
  1. **B1** Join bulk partial main door HP (`health < baseHealth`).
  2. **B2** Join bulk destructible items (destroyed or `health < maxHealth`), cap 256.
  3. **B3** Window Built via `setBarricadeState` (graph tags); destroy via `destroyBarricade(silent)`.
  4. **B4** destroyBarricade patch skip while inside getHit (GetHit owns Destroyed event).
  5. **B5** Client redirect FX suppress set → apply skips hit FX for striker.
  6. **B7** Door find: tracker 0.5→1.5 + OverlapSphere fallback.
  7. Log spam gated on `VerboseLogging`.
- **N/A:** non-barricaded window “panel HP” — not a vanilla system.
- **Deferred:** guns-on-doors playtest; dual-client smoke sign-off.
- **Playtest smoke (dual/triple):**
  1. Board door both sides; 3rd peer sees planks  
  2. Partial door main HP → late join matches  
  3. Smash boards / door → single destroy, not double event spam  
  4. Window board build/break + AI path  
  5. Client melee furniture; late join half-dead wardrobe  
  6. Client melee door FX not doubled; host AI smash boards near client

#### 3.5 Shadows — CLOSED (code) 2026-07-09
- **B:** `ShadowEvent` (night skill flags); `ShadowSpawn` + id; `ShadowStateUpdate` (pos/dead); host `ProxyShadowController` per remote; light-protection blocks attacks near lit peers.
- **C:** Host-authoritative shadow sim; clients visual+position follow; each remote gets proxy-targeted shadow copies.
- **M:** Host Broadcast to all clients; join `SendShadowsTo`; multi-proxy light check.
- **Fixes this closeout:**
  1. **Death reliable** — dead Flag uses ReliableOrdered; UnregisterShadow emits final dead update (was silent drop → client ghosts).
  2. BroadcastShadowStates sends death packet before removing dead entries.
  3. **Late-join bulk** ShadowEvent + living ShadowSpawn for all tracked shadows.
  4. Session reset clears client shadow lookups + id counter.
  5. ShadowAttack skips ProxyShadowController (own sensor path); IsConnected gate.
- **Already solid:** multi-proxy spawn on host shadow appear; ShadowSyncInfo id map; light protection range check for all proxies.
- **Deferred:** client-side AI residual between 0.3s ticks; immortal shadow type on late-join fallback spawn.
- **Playtest smoke:** night shadows appear all clients; kill shadow → death anim all; late join mid-swarm sees shadows; torch near teammate blocks host shadow attack.

#### 3.6 Night spawn / scenarios / random events — CLOSED (code) 2026-07-09
- **B:** `ScenarioSync` / `ScenarioStateSync` (current scenario name); `ScenarioEventFired` (custom event index); host night spawn redirect around far proxies; client blocks night/char/shadow/worm/spirit spawns + RandomEvent.fire.
- **C:** Host picks scenario + fires custom events; clients mirror scenario and only fire host-pending custom events; night spawns host-only with ~50% around far remotes.
- **M:** Reliable Broadcast; join bulk; multi-proxy far selection (not first-peer-only).
- **Fixes this closeout:**
  1. **Critical:** `HandleScenarioStateSync` now **applies** scenario (was log-only → late join never got night scenario).
  2. Client `frequencyMet` blocked unless host-pending event (stops independent client night events).
  3. Pending scenario/event queue until NightScenarios ready.
  4. Client blocks **all** `RandomEvent.fire` when connected (was redneck-only).
  5. Host scenario/event send + night-spawn redirect `IsConnected` gates.
- **Already solid:** client disable night spawn/despawn; forest spirit / redneck / worm redirects for far proxies.
- **Deferred:** RandomEvent types beyond entity snapshot; full custom-event VFX catalog audit.
- **Playtest smoke:** night starts same scenario all peers; host custom event fires client-side once; late join gets scenario; night mobs appear near far client; no client redneck dupe.

#### 3.7 Night death / spectator — CLOSED (code) 2026-07-09
- **B:** `PlayerDied` (ForwardablePlayer); `NightDeathState` AllDeadTrigger; `DeathStateTracker`; `NightDeathSkipDayPatch` / Save suppress; `SpectatorModeController`; host `TryResolveNightMorning` + skipDay.
- **C:** Partial night death → spectator follow living peers; all dead → host morning + broadcast exit spectate; morning rep skip per dead player (2.6).
- **M:** Host-only morning resolve; disconnect adjusts participant count + TryResolve; multi-proxy spectate targets.
- **Fixes this closeout:**
  1. **Never Reset** death state when no spectator target / ForceExit mid night death (was wiping SkipMorningRepBonus).
  2. Spectator **retarget** living proxies when current target dies/despawns; F4 already cycles.
  3. Stable **PlayerId-ordered** living follow target on enter.
  4. Night participant snapshot can **raise** on mid-night join.
  5. Guard invalid PlayerDied playerId.
- **Already solid:** host-only TryResolve; client skipDay always blocked; disconnect TryResolve; AllDeadTrigger ExitAndRespawn.
- **Deferred:** UI “waiting for survivors”; explicit NightDeathState partial roster broadcast.
- **Playtest smoke:** 1 of 3 dies → spectates living; second dies → retarget; last dies → morning all; disconnect last survivor → morning; dead player no morning rep.

#### 3.8 Player death lifecycle — CLOSED (code) 2026-07-09
- **B:** `PlayerDied` → proxy dead + colliders; `dropBody` → `DeathBagSpawn` (BagId+items); empty bag → `DeathBagLooted`; day/night branch; proxy revive when PlayerState leaves Death clips; late-join bags (2.3).
- **C:** Shared corpse bags; per-player death/respawn; night path ties to 3.7 spectator.
- **M:** Broadcast bag spawn/loot + Forwardable; PlayerDied ForwardablePlayer; multi-peer bag registry.
- **Fixes this closeout:**
  1. Immediate **proxy death pose** (`PlayDeathClip`) on PlayerDied (not wait for state tick).
  2. DeathBagLoot **anti-echo** (IsApplyingRemoteState + already-looted skip).
  3. Remote bag destroy under **NetworkApplyGuard**; mark looted id from found component.
  4. dropBody sync skips under apply guard.
- **Already solid:** BagId spawn/loot/dedup (2.3); proxy revive on non-death clips; water bag prefab; join SyncExistingDeathBags.
- **Deferred:** mid-loot item-level bag sync without reopen (uses 2.1 container when open); HasDropBag field unused (dropBody is source of truth).
- **Playtest smoke:** day death bag all peers; loot empty → gone all; respawn proxy walks; night death bag still looted while spectating; 3rd peer gets bag.

**Layer 3 complete (code).** Next: Layer 4 story.

## Layer 4 — Story, dialogs, unique events

| ID | Domain | Status | Notes |
|----|--------|--------|-------|
| 4.1 | Dialogs / choices | **OK** | Code closed 2026-07-09. Client→host `DialogOutcomeSync` with **TargetDialogueName**; host apply without open UI. Protocol **12** / **0.4.6**. |
| 4.2 | GameEvents one-shots | **OK** | Code closed 2026-07-09. Host-auth fire; EventName lookup; multipleFire sync; pending; client one-shot block. Protocol **13** / **0.4.7**. |
| 4.3 | Event triggers / requirements | **OK** | Code closed 2026-07-09. Host proxy area enter/exit; client volume suppress; proxy sight LOS. No protocol bump (uses 4.2 GameEvents). |
| 4.4 | Dreams (all levels) | **OK** | Code closed 2026-07-09. Shared session; multi-peer door/audio/item Broadcast; dream death spectate retarget. No proto bump. |
| 4.5 | Final dreamscene / epilogue | **OK** | Code closed 2026-07-09. Epilogue mode on remote load; crawl death not spectated; credits SceneLoad. Protocol **14** / **0.4.8**. |
| 4.6 | Cutscenes / movies | **OK** | Code closed 2026-07-09. Host-auth CutsceneManager + skip; proxy hide; CutsceneSync. Protocol **15** / **0.4.9**. |
| 4.7 | Unique / quest / locks | **OK** | Code closed 2026-07-09. Padlock/Locked/Interactive apply guard + 2.5m find; pending; join bulk. No proto bump. |
| 4.8 | Chapter progression | **OK** | Code closed 2026-07-09. Host `generateChapter` + world share + ChapterTransition. Protocol **16** / **0.4.10**. |
| 4.9 | Major locations checklist | **OK** | Code closed 2026-07-09. Location matrix; first-enter-only spawn snap; join LocationEnter bulk. No proto bump. |
| 4.10 | Infection / special status | **OK** | Code closed 2026-07-09. Host infection spread/despawn; host+client effect flags; poison/bleed bits. No proto bump. |
| 4.11 | Hiding / examinable | **OK** | Code closed 2026-07-09. ExamineObject host triggers + state; HidingPlace client spawn suppress. Protocol **17** / **0.4.11**. **Layer 4 complete (code).** |

### Layer 4 audit log

#### 4.1 Dialogs / choices — CLOSED (code) 2026-07-09
- **B:** Client `DialogOutcomeSync` on `DialogueButton.onPress` (index + board + dialogue + **TargetDialogueName**); host applies via `displayDialogue(target)` or legacy button click; choice index tags on `addDecision`.
- **C:** Host-authoritative story outcomes (flags/items/rep from dialogue nodes); host local choices rely on FlagSync; client local UI still advances for the talker.
- **M:** Client→host ReliableOrdered; not Forwardable (host applies, FlagSync fans world flags).
- **Fixes this closeout:**
  1. **Critical:** Host no longer requires matching open dialogue UI — applies `TargetDialogueName` on found NPC.
  2. Close host dialogue UI after silent apply if host was not already talking (avoid UI steal).
  3. `IsApplyingRemoteState` guard on client send (no echo).
  4. Protocol **12** / plugin **0.4.6** (message field added).
- **Already solid:** decision index tagging; displayNextBoard index reset; no-pause dialogue (0.6).
- **Deferred:** multi-client simultaneous talk to same NPC; host choice UI mirror to spectators; porter transport special decisions edge cases.
- **Playtest smoke:** client talks alone → host flags/journal update; both in talk host can still play; second client sees flag-driven world after FlagSync.

#### 4.2 GameEvents one-shots — CLOSED (code) 2026-07-09
- **B:** Host `GameEvents.fire` → `GameEventsFired` (pos + **EventName**); clients `fire()` local matching component; client one-shot block when connected.
- **C:** Host-authoritative story/world one-shots; compressor exception (2.8); multipleFire still syncs each run.
- **M:** Reliable Broadcast to all clients; pending if GO not loaded.
- **Fixes this closeout:**
  1. **multipleFire** was never synced (early-out when `fired` already true) — fixed.
  2. Message includes **EventName**; client prefers name match within 3m, else nearest 2.5m.
  3. Pending queue + flush when GameEvents appears.
  4. Client blocks non-compressor one-shot `fire` when connected (host only).
  5. Protocol **13** / plugin **0.4.7**.
- **Already solid:** host-only send; NetworkApplyGuard blocks rebroadcast; vanilla fired guard.
- **Deferred:** client-near trigger when only host runs Player.Instance triggers; late-join bulk of already-fired one-shots.
- **Playtest smoke:** host triggers cutscene event → all clients fire; multipleFire lever re-syncs; compressor still works on client.

#### 4.3 Event triggers / requirements — CLOSED (code) 2026-07-09
- **A:** `EventTriggers` (area enter/exit, sight, onTime, spawn/activate); `EventTrigger` → `GameEvents.fire` / `fireExit`; `EventTriggerRequirement` (worldFlag, playerState, location, items, time, NPC dead, …).
- **B:** Host-authoritative volumes:
  1. **Proxy area enter/exit** — host Postfix `OnTriggerEnter`/`Exit`: `RemotePlayerProxy` counts like `Player.Instance` (`entered`/`exited`, `Helpers.isComponentAtPos` multi-collider guard) → `fireEventTrigger(area)` / exit → 4.2 GameEvents sync.
  2. **Client suppress** — clients skip `OnTriggerEnter`/`Exit` while connected (host owns; avoids double multipleFire).
  3. **Proxy sight** — host `isCurrentlyInSightOfPlayer` OR any proxy with `Core.canSee` / radius `Player.canSee` (approx FOV when radius=0).
  4. **Requirements** — evaluated on host at fire time; `worldFlag` / `gameEventsFired` / time / GO active covered by FlagSync + 4.2; host-centric `playerState` / `locationState` / host journal keys remain host Player.Instance.
- **C:** Client walks into story volume → host proxy collider hits trigger → host fires GameEvents → all clients. Host enter still vanilla. Exit when all players out (`exited >= entered`).
- **M:** Any number of proxies; counters shared so multi-player in same volume does not premature exit.
- **No protocol / version bump** — no new messages (4.2 path).
- **Deferred:** host-only `playerState`/`haveItem`/`locationState` requirements when only a remote has the item or is in the location; exact proxy FOV (approx 55° half-angle); client-only non-volume triggers without host world; late-join bulk of already-fired one-shots (4.2).
- **Playtest smoke:** client alone enters area one-shot → all peers fire; two players in volume, one leaves → no exit yet; host sight OR client proxy LOS fires onInSight; offline SP enter unchanged.

#### 4.4 Dreams (all levels) — CLOSED (code) 2026-07-09
- **A:** Vanilla `Dreams` (prepare/start/end/initiateEnd), dream levels via presets (`hadDreamAtLvl*`), dream doors/items/audio, death→end.
- **B:** Existing shared-session stack kept:
  1. **DreamSession** — one active session; completed presets; join reject while active (`AllowJoinDuringDream` config).
  2. **DreamStartPatch** — block re-entry; client → `DreamStartRequest`; host `TryBegin` + `startDreaming`; `DreamStarted` Broadcast (Forwardable).
  3. **DreamEndDreamingAuthorityPatch** — death → spectate (`FinalDreamsceneManager`); client story end → host; host story end vanilla.
  4. **DreamSyncManager** — freeze world AI/time; remote load + transition; proxy freeze until `DreamEntered`; outcome cleanup; transferToDream chain.
  5. **FinalDreamsceneManager** — N-player all-dead (`local` + all remote proxies); disconnect prune; `FinalDreamsceneDeath` ForwardablePlayer.
  6. **Dream doors / audio / item pickup** — dedicated messages while entity broadcast paused.
- **Fixes this closeout (3+ multi-peer):**
  1. **DoorOpen / DreamAudio / DreamItemPickup** used `Send()` (first peer only) — switched to **Broadcast** (host→all, client→host).
  2. **DoorOpen** marked **`[Forwardable]`** so client opens fan out even if host door already open.
  3. **Dream death spectator retarget** — same hold/retarget as night death when follow target dies/despawns (`FinalDreamsceneManager.IsLocalDead`).
- **C:** Any peer triggers dream → host starts → all enter same preset; story end host-auth; death spectate until all dead or story end; doors/SFX/loot visible to remaining peers.
- **M:** Session not dual-only; death set keyed by remote `PlayerId`; door/audio/item reach all peers; F4 cycle among living proxies.
- **No protocol / version bump.**
- **Already solid:** dream-join reject; save blocked mid-dream; world freeze / IsHostDreamEntity; DoorOpenPatch defers to dream DoorOpen; night parity model.
- **Deferred → 4.5:** epilogue / final dreamscene cutscene polish; exact per-level preset matrix playtest; mid-dream peer leave edge cases beyond disconnect prune; chained transferToDream stress; completed-preset bulk to late joiners (host already rejects re-request).
- **Playtest smoke:** host + 2 clients enter dream together; host opens dream door → both clients; die and spectate other living peer when first target dies; all dead ends dream; client story outcome ends for all; no join mid-dream.

#### 4.5 Final dreamscene / epilogue — CLOSED (code) 2026-07-09
- **A:** Epilogue locations (`Location.isEpilogueLocation`, `epilog_part1a_dream`), `Player.inEpilogue` crawl/death camera pan, `EpilogueOutcomes` → credits, shared dream death (`FinalDreamsceneManager`), `GameEvent.endGame` / `UI.showEndGame`.
- **B:** Built on 4.4 session + death tracking:
  1. **Epilogue mode on remote load** — `ApplyEpilogueModeIfNeeded`: set `inEpilogue`, hide UI, FireMaskCam, showEpilogueText (vanilla `onLocationSpawned` never runs on co-op remote path).
  2. **Epilogue death not spectated** — Host/Client `onDeath` + `initiateEndDreaming` skip FinalDreamscene redirect when `inEpilogue` (crawl / burn camera pan stay vanilla).
  3. **Death tracking harden** — refresh proxy set if empty; accept late remote ids; `_ending` re-entry guard; spectator prefer living proxies by PlayerId.
  4. **Credits SceneLoad** — `EpilogueOutcomes.goToCredits` multiplayer path Broadcasts **`SceneLoad`** then coordinated fade + `StopNetwork` + `LoadScene("credits")` (idempotent).
  5. **endGame** — still via 4.2 GameEventsFired when host fires that event type.
- **C:** All peers enter epilogue with `inEpilogue`; one player crawl-dies without ending session for others; goToCredits pulls everyone to credits.
- **M:** SceneLoad Forwardable; death set N-player; spectator retarget (4.4).
- **Fixes this closeout:** items 1–4 above; **Protocol 14 / plugin 0.4.8**.
- **Deferred:** mid-epilogue peer leave; exact burned vs sleep outcome matrix playtest; EarlyAccessEndScreen polish; full epilogue multi-hour run.
- **Playtest smoke:** enter `epilog_*` as client → inEpilogue + title text; crawl on first death not force-spectate; complete outcomes → all peers credits; normal dream all-dead still ends session.

#### 4.6 Cutscenes / movies — CLOSED (code) 2026-07-09
- **A:** `CutsceneManager` (init/startScene/end/prologue_end), `DreamTransition` video/audio overlays (cutscene + dream), `PlayMovie` (marker only), permadeath video (`UI.PreparePermadeathVideo`), chapter intro transitions.
- **B:** Host-authoritative session cutscenes:
  1. **Client block `CutsceneManager.init`** while multiplayer — wait for host.
  2. **Host `CutsceneSync` ActionBegin** after first init → clients `ApplyBegin` (init/startScene under apply guard).
  3. **Host ActionEnd** on `prologue_endCutscene` → clients unlock FOV/player/input.
  4. **DreamTransition.skip** Broadcast ActionSkipTransition → all peers skip overlay.
  5. **Proxy hide** while cutscene playing (renderers off; restored on end).
  6. Dream entry videos already via 4.4 `StartRemoteDreamTransition`; story movies via 4.2 GameEvents enabling cutscene GOs on host then Begin sync.
- **C:** Host prologue/cutscene → all clients immobilised + same manager; end unlocks all; skip video ends for all.
- **M:** Reliable Broadcast + Forwardable CutsceneSync; N proxies hidden.
- **Protocol 15 / plugin 0.4.9.**
- **Deferred:** per-shot camera lockstep if client joins mid-cutscene; permadeath video multi-peer; chapter1/2 transition freeze edge cases; PlayMovie component (unused shell).
- **Playtest smoke:** host starts prologue cutscene → clients freeze/hide proxies; cutscene ends → all free; skippable transition skip reaches peers; offline SP cutscene unchanged.

#### 4.7 Unique / quest / locks — CLOSED (code) 2026-07-09
- **A:** `Padlock` (combination unlock), `Locked` (key/lockpick), `InteractiveItem` (levers/switches/wells).
- **B:** Existing Forwardable messages kept; harden apply path:
  1. **Find radius 0.5 → 2.5 m** (missed targets after pos rounding).
  2. **`IsApplyingRemoteState`** around unlock/switch apply (no rebroadcast / trigger echo).
  3. **Padlock** remote `unlock(false)` — no UI spam; host manual unlock still fires EventTriggers → 4.2 GameEvents.
  4. **Null-safe InteractiveItem** if on/off trigger missing (set isOn only).
  5. **Pending queues** + poll flush when objects not loaded yet.
  6. **Join bulk** `SyncExistingLocksAndInteractives` — unlocked padlocks/Locked + interactive isOn=true to new peer.
  7. Patches require **IsConnected**; switchMe skips no-op toggles.
  8. Wells still only sync repair (false→true), not heal use.
- **C:** Any peer unlocks padlock/door → all see open; lever flip → peers; late join sees already-unlocked state.
- **M:** Broadcast + Forwardable fan-out; join bulk targets new peer only.
- **No protocol / version bump.**
- **Already solid:** 0.6 no-pause padlock/interactive UI; messages Forwardable.
- **Deferred:** random padlock combination if world RNG diverges; interactive isOn=false bulk (only on=true at join); dual-player same padlock UI.
- **Playtest smoke:** client unlocks padlock → host door usable; lockpick door → all; well repair both; late join after unlock → open.

#### 4.8 Chapter progression — CLOSED (code) 2026-07-09
- **A:** `Controller.generateChapter` (ch1↔ch2 LoadScene), GameEvent transport with generateChapter, `DreamTransition` isChapter1/2 intro, prologue dream, epilogue (4.5 SceneLoad credits).
- **B:** Host-authoritative chapter load:
  1. **Client block** local `generateChapter` (story fires on host via 4.2 GameEvents).
  2. **Host + generateSave:** `saveEmptyChapterSave` → Broadcast **ChapterTransition** (ExpectWorldShare) → **WorldSaveShare then** host LoadScene; clients load via existing share ClientApply → `chapterN`.
  3. **Host without generateSave:** Broadcast ChapterTransition → all peers `ApplyChapterLoad` (flags + LoadScene, stop network).
  4. **Client ExpectWorldShare:** defer LoadScene to share apply; **12s fallback** LoadScene if share fails.
  5. **ScheduleHostShareThen** on WorldSaveShareService (afterShare even on share failure so host is not stuck).
- **C:** Host reaches ch2 transition → clients get save + chapter2 scene; re-host co-op after scene load (session ends like credits).
- **M:** Forwardable ChapterTransition; world share to all connected clients before host leaves scene.
- **Protocol 16 / plugin 0.4.10.**
- **Already solid:** WorldSession chapter advisory; WorldGenShare new world; epilogue/credits SceneLoad (4.5); dreams (4.4).
- **Deferred:** seamless re-handshake without manual rehost; prolog skip multi-peer; mid-share disconnect; automatic post-chapter lobby rejoin.
- **Playtest smoke:** host ch1→ch2 story event → both clients load chapter2; offline generateChapter unchanged; Resend world still works.

#### 4.9 Major locations checklist — CLOSED (code) 2026-07-09
- **A:** OutsideLocations (basements, bunkers, cellars, borders) + overworld grid landmarks; enter/exit via `playerInOutsideLocation` / `currentLocationName`.
- **B:** Built on **1.5** LocationEnter/Exit + layer 2–4 systems per location type:

| Location class | Examples | Co-op path | Status |
|----------------|----------|------------|--------|
| Hideout / player base | ch1/ch2 home | Barricades 3.4, saw 2.9, workbench/hideout 2.7, oven HideoutUpgrade, night death 3.7, after-night leave | OK |
| Wells | forest wells | InteractiveItem repair-only sync 4.7 | OK |
| Village / NPCs | outside_village_ch1, ch2 cellars | LocationEnter geometry + Dialogs 4.1 + FlagSync + rep C | OK |
| Doctor border | border_doctor_01 | Location + dialog/flags | OK |
| Church underground | outside_church_underground_* | LocationEnter/Exit + triggers 4.3 + GameEvents 4.2 | OK |
| Train / border wrecks | border_main_trainWreck, gate bridge | Overworld + optional outside enter | OK |
| Swamp / ch2 grid | lakes, black trees, MI-17 | Same as overworld physics 1.3 + compressor 2.8 | OK |
| Dreams / epilog | dream_*, epilog | 4.4–4.5 | OK |
| Chapter load | chapter1/2 scenes | 4.8 ChapterTransition | OK |

- **Fixes this closeout:**
  1. **Critical:** LocationEnter ~1 Hz retries no longer **snap proxy to playerSpawn** every second (only on **first enter** / location change). Stops basement/bunker jitter.
  2. Track `_remoteOutsideLocation` for membership; clear on exit/disconnect/reset.
  3. **Join bulk** `SyncExistingLocationsTo` — host + remotes’ outside locations to new peer.
  4. Async `createLocation`: clear tracking so first successful enter still gets one spawn snap.
- **C:** Client deep in bunker while host outside: host sees proxy at client PlayerState pos (not stuck at spawn). Late join sees active bunker geometry if peer is inside.
- **M:** Forwardable LocationEnter/Exit; bulk to new player only; 3+ membership map by PlayerId.
- **No protocol / version bump.**
- **Deferred:** per-landmark unique one-shot playtest matrix; underground ambient/audio 5.x; simultaneous opposite cellars stress; location-state EventTriggerRequirement for remote-only player (4.3).
- **Playtest smoke:** client walks full bunker — host proxy does not teleport to entrance each second; both in same cellar; exit; 3rd join while one is underground.

#### 4.10 Infection / special status — CLOSED (code) 2026-07-09
- **A:** World `Infection` ground splats (spread/disappear); player `CharacterEffect` (poison, bleed, burn, wards, skills); proxy burn VFX.
- **B:**
  1. **Infection host-auth** — clients skip `waitToSpread`; host `spawnInfection` Broadcasts EntitySpawn `Traps/infection_splat` (Object AddPrefab was invisible to string-path spawn patch).
  2. **Infection disappear** — host `WorldObjectRemoved` + DestroyObjectByPos matches `infect*` names.
  3. **Initial spider egg splat** already string-path EntitySpawn (`Traps/infection_splat`).
  4. **PlayerEffectSync both roles** every 2s (host was never sending wards — AI/clients missed host shadowWard).
  5. **Poisoned / Bleeding** bits on PlayerEffectSync → proxy flags + CharBase visual flags (DoT remains local on owner).
  6. Existing: PlayerBurning, EntityBurning, ProxyStatusEffectSuppression, ward AI on HostAIPatches.
- **C:** Host infection spreads → clients see new splats; fade remove; host poisoned → peers know flag; burn VFX still bidirectional.
- **M:** EntitySpawn/WorldObjectRemoved Broadcast; PlayerEffectSync ForwardablePlayer.
- **No protocol bump** (flags fit remaining byte bits).
- **Deferred:** full status icon UI on remote portraits; gas effect bit; client-local infection never; poison overlay screen-space only local.
- **Playtest smoke:** spider lays infection → peers see splat; host waits for spread → peers get rings; disappear cleans clients; host takes poison → flags; host shadow ward → client AI respects.

#### 4.11 Hiding / examinable — CLOSED (code) 2026-07-09
- **A:** `Examinable.examine` (description + `EventTrigger.Type.onExamine`); `HidingPlace` (AI cabinet spawn, not player stealth).
- **B:**
  1. **Client examine** → `ExamineObject` ActionRequest to host; local message UI still runs for the examiner.
  2. **Host examine** (local or request) runs full `examine()` → `sendTriggerInfo(onExamine)` → GameEvents via 4.2.
  3. **Host ActionState** Broadcast `examined` / `displayedDescriptionPool` so peers do not re-fire description-pool one-shots incorrectly.
  4. **HidingPlace OnEnable** suppressed on clients (host-only AI spawn; entity AI 1.2).
- **C:** Client examines note with story trigger → host fires GameEvents → all clients; examined flags match.
- **M:** Forwardable ExamineObject; request client→host, state host→all.
- **Protocol 17 / plugin 0.4.11.**
- **Deferred:** host silent examine (no host player message toast on remote request); late-join bulk of examined flags; player stealth hide spots (vanilla has none beyond AI HidingPlace).
- **Playtest smoke:** client examine key object → story flag all peers; second peer sees examined; host examine works; cabinet AI only host-spawned.

**Layer 4 complete (code).** Next: Layer 5 audio / presentation.

## Layer 5 — Audio / presentation

| ID | Domain | Status | Notes |
|----|--------|--------|-------|
| 5.1 | Player / entity / dream audio | **OK** | Code closed 2026-07-09. PlayerAudio Broadcast; EntitySound host + multi-listener cull; dream no double-forward. No proto bump. |
| 5.2 | Scrape / MOS residual | **OK** | Code closed 2026-07-09. Post-stop suppress window; body-push start Reliable; late start ignore. No proto bump. |
| 5.3 | Spectator culling / volume / menu music | **OK** | Code closed 2026-07-09. Spectate listen/cull/grid; never-cull menu BGM; listener restore. **Layer 5 complete (code).** |

### Layer 5 audit log

#### 5.1 Player / entity / dream audio — CLOSED (code) 2026-07-09
- **A:** `AudioController.Play` / `_PlayAsSound`; `CharacterSounds` (growl/idle/attack/death/gethit); player footsteps; weapon parentless SFX; dream overlays.
- **B:** Existing stack kept + hardened:
  1. **PlayerAudio** — `SendPlayerAudio` Reliable **Broadcast** + ForwardablePlayer; player/child + allowlisted parentless; footsteps via proxy `OnFootstep` (not PlayerAudio).
  2. **EntitySound** — host `CharacterSounds` patches → Broadcast; clients apply by stable id; client AI audio disabled in interp service.
  3. **DreamAudio** — 4.4 Broadcast during `Dreams.dreaming`.
  4. **Distance cull** — `AudioSuppressionPatch` + receive-side `IsNearListener` (spectator-aware).
  5. **Weapon** — fire/reload via item SFX allowlist + PlayerFiredWeapon VFX path.
- **Fixes this closeout:**
  1. **Dream double-forward** — `PlayerAudioHelper` skips while dreaming (DreamAudio owns those plays).
  2. **EntitySound send cull** — `IsNearAnyListener` (local **or any proxy**) so host far / client near still hears AI (3+).
  3. **EntitySound** IsConnected + apply-guard; host handler ignore (no echo); tag Forwardable.
  4. Idle **stop** always sent even if far (empty LoopName).
- **C:** Peer gunshot/equip at range; host AI growl near client B only → B hears; dream SFX once not twice.
- **M:** Broadcast + multi-listener cull; ForwardablePlayer PlayerAudio.
- **No protocol bump.**
- **Already solid:** rate limit 0.08s; menu/BGM not culled; MOS/scrape → 5.2; dream DreamAudioPlayer.
- **Deferred:** full 1.2b P9 per-family SFX matrix; occlusion parity; music sync across peers.
- **Playtest smoke:** client shoots host hears; host chomper near client only → client hears growl; dream door SFX once; equip weapon peers hear getSound.

#### 5.2 Scrape / MOS residual — CLOSED (code) 2026-07-09
- **A:** Vanilla `ItemSounds` moving loop (vel residual); E-drag scrape; body-push furniture scrape.
- **B:** Stack from **1.4** + residual harden:
  1. **MOS** (`MovingObjectSoundService`) owned loop for remote drag / body-push; vanilla 0.5s fade on stop.
  2. **ForceStop** — sleep RB, clear `movingSoundAO`, stop MOS + grass variant.
  3. Drag end **ReliableOrdered** + ForceStop; body-push stop Reliable + ForceStop.
  4. **Post-stop suppress (0.45s)** — ignore NoteMoving / EnsurePlaying / body-push start / PlayerAudio start for that object name (kills late packet re-arm).
  5. Body-push **start** delivery Unreliable → **ReliableOrdered**.
  6. Stale timers in WorldPhysics (0.12s / hold 0.1s) still backstop; MOS.Tick fade/occlusion.
- **C:** Release drag → scrape dies after 0.5s fade, no multi-second ghost; body-push stop no re-screech; 3rd peer same.
- **M:** ForceStop on disconnect claims; Broadcast stop to all; exclude pusher on start still.
- **No protocol bump.**
- **Deferred:** dual-grab scrape ownership polish; name-collision if two objects share GO name.
- **Playtest smoke:** drag chair release → clean stop; body-push crate stop walking → no residual; spam push/stop no stuck loop.

#### 5.3 Spectator culling / volume / menu music — CLOSED (code) 2026-07-09
- **A:** Night/dream spectator follow; WorldGrid culling; Unity AudioListener; menu BGM `DW1` via `Play`→`_PlayAsSound`.
- **B:**
  1. **SpectatorCullingPatch** — `WorldGrid.refreshPosition` uses follow target while spectating (was already).
  2. **Immediate grid refresh** on enter spectate + F4 switch target (load cullables around living peer now).
  3. **AudioListener** bind: Find by name or `GetComponentInChildren`; unparent + follow; restore re-parents with **local zero** pose.
  4. **Listen position** — `LocalAudioService.GetListenPosition` already uses follow target when spectating (distance cull + EntitySound receive).
  5. **Never-cull sounds** — expanded `IsNeverCullSound` (DW*, Music*, UI_*, menu+music); used by suppress + PlayerAudio forward block (no menu track over LAN).
  6. Music/ambience path still never distance-culled (`AudioAmbienceSuppressionPatch`).
- **C:** Dead player F4 follows teammate far away → world loads + hears nearby combat; open main menu mid-session → DW1 not muted by co-op cull.
- **M:** Spectate targets any living proxy; grid/listen follow that transform.
- **No protocol bump.**
- **Already solid:** host multi-center WorldGrid for remotes (HostAIPatches); night/dream spectate retarget 3.7/4.4; menu music DW1 allowlist history.
- **Deferred:** spectator volume slider; host AI audio still host-centric except EntitySound; multi-monitor AudioListener edge cases.
- **Playtest smoke:** die → spectate peer in bunker (hear inside ambient); F4 cycle; exit restore listener under player; F2 menu music plays with co-op connected.

**Layer 5 complete (code).** **1.2b complete (code).** Next: dual/triple playtest backlog + GitHub backup of post–Layer-4 work.

---

## Locked design decisions

### Reputation model **C — Hybrid** (locked 2026-07-09)

| NPC class | Model | Notes |
|-----------|--------|--------|
| Story / village NPCs (Doctor, Musician, etc.) | **Shared** — host authoritative | Live `ReputationSync` + bulk on join |
| **Morning hideout traders** | **Per-player** | Survival bonus, prices, dialogue rep personal |
| Trader **inventory / assortment** | **Shared** | Keep `TradeSync` |

**Both morning traders (same system, different prefab by chapter):**

| Chapter | Prefab | Code path |
|---------|--------|-----------|
| Ch1 (Dry Meadow / Old Woods hideouts) | `Characters/NPC/NightTrader` | `Location.spawnTrader()` |
| Ch2 (Swamp hideout) | `Characters/NPC/TheThree` | same `spawnTrader()` branch |

Vanilla: both use `isNightTrader`, morning `startAfterNight` `reputation +=` by hideout/chapter, and `Flags.npcStates`.  
Detection for “exclude from shared bulk / use per-player table”: prefer **`Character.isNightTrader`**, not only name strings (covers NightTrader **and** The Three).

**Rules:**
1. Host spawns morning trader **once** (no client `startAfterNight` double-spawn).
2. Morning survival bonus granted **per surviving `playerId`** (dead at night → no +rep for that player).
3. `ReputationBulkSync` must **not** overwrite night-trader standing for all clients with host’s value.
4. Live night-trader rep changes stay **per-player** (not host broadcast to all).
5. Shared NPCs: fix live sync to **all** peers (`Broadcast`, not first-peer-only `Send`).
6. Persist night-trader rep via per-player backup / host `playerId → rep` map.

Implement when domain **2.6 Reputation** (and night-death **3.7**) is on the plan — not earlier unless prioritized.

---

## Campaign order (after infrastructure)

Dependency order for audits:

1. Layer 0 (**complete**)  
2. Layer 1 continuous world (**complete**)  
3. Layer 2 inventory  
4. Layer 3 combat/night  
5. Layer 4 story (full location map)  
6. Layer 5 audio polish  
7. QoL / balance (Layer 6) after core verdicts are OK/Partial with playtests  

---

## Layer 6 — Balance / fairness

Policy locked 2026-07-09:

| Lever | Rule |
|-------|------|
| Loot | **A** — progression sinks + barricade mats only |
| NPCs | **A** — named allowlist; **dream presence** (Black Chomper), not night trash |
| Night density | Unchanged (redirect only) |
| Scale | `LootShareMode`: Off / Double / `ScaleWithPlayers` = `1 + ConnectedPlayerCount` |

| ID | Domain | Status | Notes |
|----|--------|--------|-------|
| 6.1 | Loot share sinks + defense mats | **OK** (code) | `CoopBalance` + `ItemDoublePickupPatch`: `isExpItem` + meat/mushrooms + **wood** + **nail**. Same blockers (dropped/player-placed/own inv). |
| 6.2 | Dream named NPC presence | **OK** (code) | Host: allowlist (default `ChomperBlack`) × party mult while dreaming. `Core.AddPrefab` + delayed dream-location scan. Markers prevent double-scale. Config: `NamedNpcScaleEnabled`, `NamedNpcAllowlist`. |
| 6.3 | Softlock / story matrix | **Partial** (docs) | Matrix below; playtests own verification. |

### Softlock / scale matrix

| Class | Action |
|-------|--------|
| Hideout fuels (meat / exp mushrooms / `isExpItem`) | Scale pickups |
| Barricade **wood** / **nail** | Scale pickups |
| Journal keys / notes / quest | Shared identity (2.4) — no scale |
| Trader stock | Shared absolute — no scale |
| Unique story one-shots | No scale |
| Night hideout trash density | No scale (redirect only) |
| Night scenario amounts | No scale |
| Dream allowlisted NPCs (ChomperBlack) | Scale presence × mult |
| Shadows | Already N× (3.5) — leave |
| Ammo / meds / gasoline | **Out** this pass |

### Config

| Key | Default | Role |
|-----|---------|------|
| `DoubleItemsEnabled` | true | Master loot share |
| `LootShareMode` | ScaleWithPlayers | Off / Double / ScaleWithPlayers |
| `NamedNpcScaleEnabled` | true | Dream allowlist presence scale |
| `NamedNpcAllowlist` | ChomperBlack | Comma-separated short names |

### Deferred (balance)

- Night density scale; overworld unique NPC doubles; ammo/meds; trader restock scale; dual playtest of 6.1–6.2

---

## Layer 0 proposed fixes (pending approval)

Do **not** implement until approved. Ranked by risk. **3+ is in scope** — F0.4 is no longer deferred.

| # | Issue | Proposed fix | Risk | N-player |
|---|--------|--------------|------|----------|
| F0.1 | TimeSync never sets client `isAfterNight = true` | **DONE in 0.3** | — | **Done** |
| F0.2 | No time/day on join bulk (wait ≤2s) | **DONE in 0.3** | — | **Done** |
| F0.4 | ClientStateBackup single file | **DONE in 0.2:** `client_backup_p{id}.json` + JSON PlayerId; local self path separate | — | **Done** |
| F0.3 | Inventory/dialogue may pause local world | **DONE in 0.6** (dialogue/leveling/skills/etc.; inventory never paused) | — | **Done** |
| F0.5 | Wrong-save join only advisory | Soft warning UI chapter/day mismatch | Low / QoL | Every joiner |

**Layer 0 fix queue closed (F0.1–F0.4, F0.3).** Remaining deferred QoL: F0.5 wrong-save UI.

### Known multi-peer hotspots (track across later layers)

These are not “2p only works” — they are places **M checks** must pass before a domain can be `OK`:

- `ClientStateBackup` single path (0.2 / F0.4)
- Combat `DamagePlayer` targeting when `_peers.Count > 1` — **DONE in 3.1** (SendToPlayer only; blind broadcast blocked)
- `RemotePlayerForward` coverage for all ForwardablePlayer actions
- Drag claims / death bags / dreams keyed by `playerId`
- Night death: all living vs all dead morning resolution with 3 players — **DONE in 3.7**
- Proxies: N remote bodies on every peer (host sees N-1 clients; each client sees host + other clients via forward)

---

## Change log

| Date | What |
|------|------|
| 2026-07-09 | Created checklist; Layer 0 code audit completed |
| 2026-07-09 | Scope expanded: **3+ players in scope**; rubric +M; F0.4 promoted into first fix batch |
| 2026-07-09 | **0.1 closed (code):** reliable Handshake/WorldSession; reliable Direct forward; menu session line. Next: **0.2** |
| 2026-07-09 | **0.2 closed (code):** per-PlayerId host backups; local self backup; F0.4 done. Next: **0.3** |
| 2026-07-09 | **0.3 closed (code):** full isAfterNight; join TimeSync; reliable. Next: **0.4** |
| 2026-07-09 | **0.4 closed (code):** reliable FlagSync; pending bulk/deltas until Flags ready. Next: **0.5** |
| 2026-07-09 | **0.5 closed (code):** ResetAll hardened; more registry entries; TraverseHack clear. Next: **0.6** |
| 2026-07-09 | **0.6 closed (code):** dialogue/leveling/skills/interactive no-pause. **Layer 0 complete.** Next: **1.1** |
| 2026-07-09 | Locked **Reputation model C**: shared story NPCs; per-player morning traders = NightTrader (ch1) + The Three (ch2), detect via `isNightTrader` |
| 2026-07-09 | **1.1 closed (code):** vault PlayerId + forward; anim lib reliable; **Protocol 7 / 0.4.1**. Next: **1.2** |
| 2026-07-09 | **1.2 closed (code):** EntitySpawn multi-peer; snapshot round-robin. Next: **1.3** |
| 2026-07-09 | **1.3 closed (code):** host multi-center physics scan. Next: **1.4** |
| 2026-07-09 | **1.4 closed (code):** drag claims/end/scrape multi-peer. Next: **1.5** |
| 2026-07-09 | **1.5 closed (code):** location PlayerId/exit pos; no leaveAll; **Protocol 8 / 0.4.2**. Next: **1.6** |
| 2026-07-09 | **1.6 closed (code):** weather visual apply + client schedule suppress. Next: **1.7** |
| 2026-07-09 | **1.7 closed (code):** map markers PlayerId + bulk; discovery guard; **Protocol 9 / 0.4.3**. **Layer 1 complete.** Next: **2.1** |
| 2026-07-09 | **2.1 closed (code):** container state to requester only; searched fan-out. Next: **2.2** |
| 2026-07-09 | **2.2 closed (code):** dropped item late-join + consume guard. Next: **2.3** |
| 2026-07-09 | **2.3 closed (code):** death bag looted-id set + late-join empty skip. Next: **2.4** |
| 2026-07-09 | **2.4 closed (code):** journal Broadcast (3+); pending bulk; world cleanup on join. Next: **2.5** |
| 2026-07-09 | **2.5 closed (code):** TradeInventorySync absolute stock; host restock; join bulk; **Protocol 10 / 0.4.4**. Next: **2.6** |
| 2026-07-09 | **2.6 closed (code):** Model C shared+night-trader; Forwardable live; bulk skip; backup. **Protocol 11 / 0.4.5**. Next: **2.7** |
| 2026-07-09 | **2.7 closed (code):** workbench Broadcast; constructible bulk/pending; hideout join harden. Next: **2.8** |
| 2026-07-09 | **2.8 closed (code):** compressor convert unblocked under ApplyGuard; any-peer fire; hotbar. Next: **2.9** |
| 2026-07-09 | **2.9 closed (code):** saw late-join bulk + pending; quiet fuel-only; safe refresh. Next: **2.10** |
| 2026-07-09 | **2.10 closed (code):** skill restore in ClientStateBackup; PlayerSkillsSync no-op. **Layer 2 complete.** Next: **3.1** |
| 2026-07-09 | **GitHub backup:** `749b0f8` Layer 2 complete (protocol 11 / 0.4.5) → origin/master |
| 2026-07-09 | **3.1 closed (code):** FF debounce/AttackerId; targeted DamagePlayer; victim blood. Next: **3.2** |
| 2026-07-09 | **3.2 closed (code):** reliable ExplosionTrigger; player-blast FF gate; dead-proxy skip. Next: **3.3** |
| 2026-07-09 | **3.3 closed (code):** gas nest-safe ignite; trail dedupe; join bulk; throwable IsConnected. Next: **3.4** |
| 2026-07-09 | **3.4 closed (code):** barricade join bulk + pending; MeleeWorldHit damage clamp. Next: **3.5** |
| 2026-07-09 | **3.4 re-audit fix:** partial door + furniture join bulk; window setBarricadeState; destroy dedupe; FX suppress; door find fallback. |
| 2026-07-09 | **3.5 closed (code):** shadow death reliable + join bulk; multi-proxy aggro kept. Next: **3.6** |
| 2026-07-09 | **3.6 closed (code):** ScenarioStateSync apply; client event gate; RandomEvent client block. Next: **3.7** |
| 2026-07-09 | **3.7 closed (code):** hold night-death state; spectate retarget; participant raise. Next: **3.8** |
| 2026-07-09 | **3.8 closed (code):** proxy death pose; bag loot anti-echo. **Layer 3 complete.** Next: **4.1** |
| 2026-07-09 | **GitHub backup:** `0e11f30` Layer 3 complete (3.1–3.8) → origin/master |
| 2026-07-09 | **4.1 closed (code):** DialogOutcome TargetDialogueName host apply; **Protocol 12 / 0.4.6**. Next: **4.2** |
| 2026-07-09 | **4.2 closed (code):** GameEvents EventName + multipleFire + client one-shot block; **Protocol 13 / 0.4.7**. Next: **4.3** |
| 2026-07-09 | **4.3 closed (code):** proxy area enter/exit + client volume suppress + proxy sight; no proto bump. Next: **4.4** |
| 2026-07-09 | **4.4 closed (code):** dream door/audio/item Broadcast + death spectate retarget; no proto bump. Next: **4.5** |
| 2026-07-09 | **4.5 closed (code):** epilogue mode + crawl guard + SceneLoad credits; **Protocol 14 / 0.4.8**. Next: **4.6** |
| 2026-07-09 | **4.6 closed (code):** host CutsceneSync begin/end/skip + proxy hide; **Protocol 15 / 0.4.9**. Next: **4.7** |
| 2026-07-09 | **4.7 closed (code):** lock find radius + apply guard + pending + join bulk; no proto bump. Next: **4.8** |
| 2026-07-09 | **4.8 closed (code):** generateChapter host-auth + share-then-load; **Protocol 16 / 0.4.10**. Next: **4.9** |
| 2026-07-09 | **4.9 closed (code):** location first-enter snap + join bulk; major locations matrix. Next: **4.10** |
| 2026-07-09 | **4.10 closed (code):** infection host spread/despawn + host effect sync + poison/bleed flags. Next: **4.11** |
| 2026-07-09 | **4.11 closed (code):** ExamineObject host-auth + HidingPlace client suppress; **Protocol 17 / 0.4.11**. **Layer 4 complete.** Next: **5.1** |
| 2026-07-09 | **1.2b drafted (future):** entity presentation matrix (families P1–P10, pipeline Qs); Unchecked. Does not block Layer 5. |
| 2026-07-09 | **1.2b closed (code):** stop frame scrub; pending clip/death; body+legs anim. Families code-OK; elite playtest Partial. |
| 2026-07-09 | **GitHub backup:** `2fbecf6` Layer 4 complete (protocol 17 / 0.4.11) → origin/master |
| 2026-07-09 | **5.1 closed (code):** dream no double-audio; EntitySound multi-listener cull. Next: **5.2** |
| 2026-07-09 | **5.2 closed (code):** scrape post-stop suppress + reliable body-push start. Next: **5.3** |
| 2026-07-09 | **5.3 closed (code):** spectate grid/listen + never-cull menu BGM. **Layer 5 complete.** |
| 2026-07-09 | **1.8 closed (code):** light domain — flare B+, flashlight stream, ambient full snapshot, world join bulk + ReliableOrdered; **Protocol 18 / 0.4.12**. |
| 2026-07-09 | **1.8 residuals stomped:** held flare FX + conditional LightFlags payload + VerboseLightSync + smoke sheet; **Protocol 19 / 0.4.13**. |
| 2026-07-09 | **Layer 6 balance (code):** loot sinks + wood/nail; dream ChomperBlack presence × party mult; **0.4.14** (protocol still **19**). |
| 2026-07-09 | **Public polish 0.4.15:** logging banner fix + Support Container + session stop + LOGGING.md; comment/README hygiene. |
