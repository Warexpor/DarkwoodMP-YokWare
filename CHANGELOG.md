# Changelog

Unreleased Path B work after **0.9.2** tag lives under **0.9.2+** sections below (newest first). Protocol stays **19**; optional message IDs **112–126**. Keep this file updated whenever playtest/audit fixes land — do not leave them only in plans or COOP_COVERAGE.

**Agent rule:** every ship of playtest fixes / features / regressions must add a **0.9.2+** section here in the same change (see root `AGENTS.md` → Changelog discipline).

## 0.9.2+ — Gas bomb / molotov host-auth fire (client wild + flame cover + stutter)

Playtest: host gas bomb looked normal; client looked **wild**. Molotov flame cover not 1:1. Possible stutters around both.

### Root cause
- Both peers ran full `Explodes.spawnObjects()` with **independent random offsets** → different puddle layouts.
- Client `MuteThrownCombat` only zeroed damage, not `spawnObject` — client kept local scatter **and** often skipped host secondaries while local bomb still had `spawnObject` set.
- Bidirectional `GasTrail` / `startBurning` sync doubled density and dual `waitToBurnNeighbors` fire sims (stutter + divergent flame).

### Fix (host-authoritative gas layout + fire)
- **`MuteThrownCombat`:** also null `spawnObject` / `objectAmount` (keep boom `explosionPrefab` VFX). Host combat copy owns scatter.
- **Gas trail TX host-only;** client local `Items/GasolineTrail` spawn blocked unless network apply flag.
- **Object AddPrefab** path for `GasolineTrail` (gas bomb uses Object overload, not string) → host `GasTrail` channel; skip dual `ExplosionSpawnObject` for the same puddles.
- **`Liquid.startBurning` host-only** invent; client only ignites when applying host `GasIgnite` (neighbor spread is host-driven).
- Slightly wider trail dedupe + nearest-liquid ignite match; host trail flush batch dedupe (less packet spam).

## 0.9.2+ — Coordinated multi-save + permanent copy refresh + same-world join + open-door melee

Product intent: **when any player initiates Save, every connected player Saves on their machine** with the vanilla **Saving** indicator, and each machine’s **permanent co-op copy is refreshed** (sav files + fingerprint meta). Join reuses an exact local copy without forced overwrite.

### Save (live session) — coordinated fan-out + permanent copy always updated
- **Any role** finishes a local `SaveManager.Save` → `SaveSync` → peers `Save(force + Saving UI)`; no rebroadcast loops.
- After **every** local Save (initiator + SaveSync apply): `CoopWorldCopyMeta.RefreshAfterLocalSave()` re-fingerprints on-disk `sav.dat`/`savs.dat`, updates day/chapter/`LastRefreshedAt`.
- Logs: `Permanent co-op copy updated after Save → slot N …`.
- Clients also send `ClientStateBackup` to host.

### Join — same-world skip overwrite
- After download, SHA1 of inflated package compared to local slots; **exact match → reuse that profile**, no overwrite, go straight to **ENTER WORLD**.
- Ignore duplicate world-share begins while already slot-picking / awaiting ENTER WORLD.
- Title auto-`WorldRequest` **suppressed** while slot pick / ENTER WORLD / download active (was double-downloading and forcing a second overwrite).
- Mid-menu still used when no match; `[SAME AS HOST]` when meta fingerprint matches package.

### Open-door client melee
- Client redirect skipped vanilla `Door.getHit` → open door never got `bodyRB.AddForce` on the striker.
- **Fix:** predictive open-door swing (same −50000 force, 2-frame delay) on redirect; suppress network re-force for that strike.

### Files
- `CoopWorldCopyMeta`, `WorldSaveShareService`, `SaveSyncPatches`, `HandleSaveSync`, `MainMenuMultiplayerInject`, `ClientWorldMeleePatches`, `JoinWorldSlotPicker`

## 0.9.2+ — Throwables / lights / workbench / peer flashlight SFX (playtest batch)

Dual-box residuals after flare pass. Protocol **19** unchanged.

### Match throw + held light
- **Short land / force mismatch:** in-flight `ThrownItem` excluded from physics snapshot kinematic lock; vanilla flight rebuild from throw origin + `setFallSpeed`; re-assert velocity next frame after spawn.
- **Held match no peer light:** local held match no longer required `activated` for continuous light TX (`TryGetLocalHeldMatchLight` on `heldItem`).
- **Peer match flicker:** stream stable cruise intensity (not live flame flicker thrash); RX position-only while match active.

### Lantern (parked)
- Ambient lantern is `Player.lightDot` / vision pipeline, not a discrete item `lightEmitter` — proxy clone + FOV copy fought double-light vs no-light.
- Iterations (RemoteLanternAmbient bare `Light2D`, neutralize stock dots, stop FOV lightDot copy) still left **no reliable peer lantern**. Parked for a later dedicated pass; flares/torch/match continuous path remain.

### Workbench
- **Stuck “someone is already using…”:** `Workbench.close()` is empty in vanilla — real close is `Inventory.hide` / `closeInventory` with `inv.workbench`. Host-auth `WorkbenchOpenLock` now releases on hide/closeInventory and clears all locks for a player on disconnect.

### Flashlight peer SFX
- Peer on/off always had **indoor reverb + snappy cutoff** (proxy parent `CharBase` reverb path + forced 3D spatial).
- **Fix:** `LocalAudioService.IsPrefer2dNetworkOneShot` for activate/deactivate-class one-shots; `HandlePlayerAudio` plays them as 2D `AudioController.Play` (no reverb/lowpass parent, `spatialBlend=0`).

## 0.9.2+ — Combat residual closeout + harvest + night spectator

Playtest-driven residual combat/damage gaps closed from code (before full dual-box edge soak). Protocol **19** unchanged.

### Combat / damage
- **`MaxPlayerAttackRange` 350 → 3500** (`GameplayConstants`) so open-map / long-gun client `PlayerAttack` is not dropped as “too far” while entities still stream at activation range.
- **Target resolve:** stable id → position+name within `PlayerAttackNameMatchRadius` (80u) → capped loose name; skip dead targets; sanitize damage ≤0.
- **FF:** debounce **0.08s → 0.02s** and key includes damage (shotgun multi-pellet no longer collapsed to one hit); ignore hits on night-dead victims / local night-death.
- **`DamagePlayer` / hitscan / proxy / host melee:** sanitize + skip dead / night-dead; MeleeWorldHit door/window find uses looser radius helpers.
- **Night-dead proxies:** `DeathStateTracker.IsRemoteNightDead` — do **not** revive from non-Death clips while night-dead (get-up while spectating was re-aggroing dogs on a “zombie” corpse). Force dead + colliders off until morning.

### Harvest / traps
- **`TrapDisarmHarvestSync`:** successful `Item.disarm` → `switchToTriggered` broadcasts silent `TrapState` (`OccupantSilentDisarm`); peers keep GO/sprite (no boom, no `WorldObjectRemoved` vanish). Stomp path still full boom.

### Spectator / night death UX
- Night spectator FOV defaults restored; mute get-up SFX while spectating (`AudioSuppression` / vision path).
- Proxy stay-dead while peer night-dead (see combat).

### Save index (client join)
- Receive path: **`MergeProfileIntoDiskIndexAndSave`** (full disk index + slot 5) so offline Save cannot rewrite `profs.dat` with **only** the receive profile. Isolated SecondDarkwood save root makes disk write safe.

### Perf (follow-on)
- Pending **GameEvents** flush rate-limited (1s) + max age drop — was scanning every frame when hideout events unloaded (night FPS crater).
- Physics / door / scenario random-event touch-ups in the same batch.

## 0.9.2+ — Docs: kill redundant matrices / stale plans

Removed leftover docs superseded by `CHANGELOG` + `COOP_COVERAGE`: `SYNC_MATRIX`, `MERGE_MATRIX`, bloated `TODO` archive, `YOKYY_FEATURE_AUDIT`, `PLAN_INWORLD_AUDIO_FX`, `DEFERRED_FEATURES_PLAN`, root `docs/LOGGING.md` (use `DarkwoodMP.Mod/docs/LOGGING.md`). `docs/TODO.md` is a short residual list only.

## 0.9.2+ — Late-join sticky world bulk (host-auth)

- **Gap:** light late-join dump skipped deathbags / drops / barricades / gas / locks / constructibles / trade / weather / shadows / locations (methods existed, not called — host freeze history).
- **Fix:** same host-auth pipeline. Light dump + registry bulk (locations, shadows, drops) immediately; heavy FindObjects phases **one per frame** (weather → trade → construct → locks → barricades → gas → deathbags). Scenario bulk still skipped (night unique-event re-fire). No client authority.

## 0.9.2+ — Traps + lights full-scope (beartrap / flashlight / flare / match)

Nothing deferred from the trap+light gap plan. Optional wire: **ThrowableDespawn=125**, **TrapBulk=126**. PlayerState/TrapState/ThrowableSpawn trailers (same-build dual-box).

### Beartraps
- **TrapNetworkId** host-minted stable ids on trap GOs (not float-rounded luck alone).
- **Per-trap occupancy:** `PlayerState.TrapNetId` + remote state; loot/disarm/context guards use `IsTrapOccupied(trap)` — trap T2 free while peer stuck in T1.
- **Host-auth trigger:** client `TrapTriggered` (+ id) → host apply + immediate `TrapState` Broadcast; pending queue when GO not loaded; flush each frame / bulk.
- **Late join:** `TrapBulk` full table (id + triggered + occupant).

### Flashlights
- Continuous stream params forced ~6.6 Hz (was 1 Hz).
- **FlashAimY** streamed; proxy Flashlight rotation follows aim; create Flashlight child if missing.

### Flares + matches
- **Match** continuous held light (`LightFlagMatch` + same offset/params path); remain01 trailer.
- Held **flare remain01**; extinguish when remain=0 or flag off (no orphan light).
- **Thrown:** `ThrowId` + `LongevitySec` on ThrowableSpawn; host `TickThrownLightExpiry` → **ThrowableDespawn**; late-join re-sends active thrown lights.

### Smoke (dual-box)
1. A in T1, B loots T2 OK / T1 blocked → free → OK  
2. Far/close trap spring both see  
3. Late join mid-trap: occupancy + sprung  
4. Flash aim near/far both ways  
5. Hold flare/match to end: both dark same second  
6. Throw flare: both ground light → both dark on expire  
7. Late join with flash/flare/thrown on  

## 0.9.2+ — Host AI: client ≈ host player identity

- **Before:** proxy only acquired when host not in `charactersInSight` (client second-class); `onlyAttackPlayer` only grabbed host; proxy often missing from `charactersInSight`.
- **Now:** closest valid player among **host + all proxies** for acquire/chase; proxy `CharBase` added to sight list; flee uses nearest player; near-sight `attackCharacter(proxy)` like SP on Player. Sniffer / grid / melee→proxy / growl already multi-player.

## 0.9.2+ — Client co-op FPS: interest cull (host leave fixes FPS)

Playtest: **poor FPS only while host connected**; normal after host leaves. Host entity broadcast uses **~3500u**; client `EnsureEntityAwake` forced SetActive/isActive on far WorldGrid-culled NPCs (logs matched dogs/rabbits at 9k–12k while player in hideout) → map-wide wake while co-op live; when host leaves, snaps stop → cull recovers.

- Client entity apply / pending / phantom spawn only within **`ClientInterestDistance` 1400u** of listen pos; far ids stop driving.
- Client physics free-body apply same interest cull (no far FindOrSpawn/full scan).
- Prior purge removal + soft reconnect + ENTER WORLD + deferred grant Save still apply.

## 0.9.2+ — Client FPS: stop mass NPC purge + softer host grant

- Removed first-snapshot mass purge; promote reclaim + deferred Save; WorldGrid refresh.

## 0.9.2+ — Join: ENTER WORLD gate + phase-3 soft reconnect (FPS)

- Soft `ConnectToHost` when already in-chapter; ENTER WORLD before offline load.

## 0.9.2+ — Peer audio range

- `LocalAudioService` default hear/cull + spatial max **500 → 650** (+30%) so players hear each other a bit farther (guns/equip/footsteps/entity/MOS).

## 0.9.2+ — Title MULTIPLAYER lifecycle overhaul (Yokyy presentation kept)

Native look stays Yokyy (clone `quitBtn` → strip art → `tk2dTextMesh` + `PositionMe`). **Lifecycle rewritten**:

- **Edge-triggered inject** when Menu0 becomes active / Menu0 instance rebuilds / owned button dead — not re-Inject every poll.
- **`YokWareUiTag` ownership** — purge only our mp/panel nodes under Menu0 / quit parent (no scene-wide Find thrash; vanilla buttons untouched).
- **DestroyImmediate** for stale clones (deferred `Destroy` left ghost colliders → no hover/click on host after inject storm).
- **Root collider only** + re-wire OnFire/textMesh when still interactive; res-change re-layouts without rebuild.
- Legacy untagged `YokWare_MultiplayerBtn` / panel names still purged once.

Also (same playtest batch): `player_in*` FlagSync local-only (client stutter); physics full-scan rate limit; PeerRoster → Trace.

## 0.9.2+ — Pause MULTIPLAYER stack + client join stutter (playtest)

- Superseded by **Title MULTIPLAYER lifecycle overhaul** above (kept for history).

## 0.9.2+ — Join load: sav/savs pair consistency (playtest)

- **Symptom:** client phase-2 offline load → Player.log `ERROR WHEN LOADING DYNAMIC AND STATIC SAVE` + NRE in `SaveManager.Load`; `ChapterResume` stuck `loadingGame=True`; host sees peer detach mid join; phase-3 never reconnects.
- **Root cause:** late-join share packed on-disk files with **no force Save**. Host `sav.dat` and `savs.dat` can diverge (playtest: ~20h mtime skew) while host still runs from RAM.
- **Fix:** late-join force-saves once when the pair is incomplete or mtime skew &gt; 30s; log pair sizes/mtimes on every pack; ChapterResume clears stuck `loadingGame` after 45s so client is not softlocked for 180s.

## 0.9.2+ — Host grant on host crash (n+ migration — full)

Optional **PeerRoster = 123**, **HostHandoff = 124**. Config `Network.HostMigrationEnabled` (default true).

- **Crash path:** host timeout/drop → survivors elect lowest player id → elect soft-promotes; others reconnect to elect LAN IP + session port.
- **Graceful leave:** host Disconnect → `HostHandoff` elect announce → short delay → `StopNetwork` (title + F2 menu).
- **Roster:** LAN IPv4 + session listen port (~4s + on handshake); not ephemeral outbound ports.
- **Promote reclaim:** clear client entity host-sync freeze, `DoUpdateTime=true`, drop stale proxies/claims, checkpoint `Save()`, TimeSync when peers return.
- **Stable ids:** preferred PlayerId rebind on reconnect; handshake `HostPlayerId` (host may be ≠1).
- **False migration guard:** intentional `StopNetwork` / join tear sets suppress so offline load does not steal host grant.
- **Port busy:** promote tries sessionPort..+5 if primary bind fails.
- **Limits:** LAN; in-memory world + checkpoint Save (not full mid-session file mirror); no WAN/NAT; old host process rejoins as client.

## 0.9.2+ — Night/day transition audit

Optional **AfterNightEndRequest = 122**.

- **Leave hideout / morning freeze:** client no longer runs SP `endAfterNight` (trader destroy + time++). Sends `AfterNightEndRequest`; host ends once + TimeSync. Fixes TimeSync re-freezing a client who left first.
- **Edge TimeSync:** host flushes TimeSync on `startDay` / `startAfterNight` / `endAfterNight` / `skipDay` (was up to 2s lag).
- **Client personal morning:** day roll via TimeSync heals + skill recharge (host `startDay` never ran on client).
- **Trader ghost:** client despawns morning trader when `IsAfterNight` clears.
- **FixedUpdate:** client only forces `DoUpdateTime=false` (clock) — inventory refresh still ticks (supersedes earlier “skip whole FixedUpdate” wording under 0.9.2 C1).

## 0.9.2+ — Light system audit fix (pre-dream)

- **Join bulk dead wire:** `SyncExistingWorldLightsTo` + new `SyncExistingGeneratorsTo` actually called from `SendLateJoinGameplayBulk` (docs claimed on connect; was never invoked).
- **Gen fan-out bug:** removed `Generator` turnOn/turnOff/powerDown → blanket `LightState` for `powerItems`. Vanilla only `restorePower`/`cutPower` (lamp `isOn` sticky); LightState `turnOff` stomped `isOn` and broke re-start.
- **Unloaded grid:** `ApplyLightState` queues misses; `TryFlushPendingLights` each frame; host re-pushes lights+gens on peer first `LocationEnter`.
- **empDisable:** apply-guard so remote apply does not echo.
- **Lamp click SFX:** remote `ApplyLightState` calls `ItemSounds.playSwitch()` (many lamps put click only in `switchSound`, not start/end).

## 0.9.2+ — Dream sync full-scope harden

Optional message IDs **120–121** + trailers on DreamStarted/Ended.

- **Session snapshot:** completed presets + `hadDreamAtLvl*` on DreamStarted/Ended trailers and late-join `DreamSessionBulk` (120).
- **Start race:** host `TryBegin` at prepare / StartRequest before location spawn.
- **Story end:** host runs `initiateEndDreaming` (outcome transition) for client story ends — not hard `endDreaming`.
- **transferToDream:** host `DreamChainStart` (121); remotes load next pocket without session Idle; no reflection name-guess.
- **Remote entry:** `saveCurrentPlayerState` before load; cleanup order restore → destroy → unfreeze → world events.
- **All-dead:** participant set from handshaked peers + proxies.
- **Dream physics/entities:** free bodies in dream pocket sync; stream dream NPCs (skip frozen overworld).
- **Doors:** apply guard on DoorOpen receive (no re-broadcast thrash).
- **Transition skip:** also hits `startTransition` when playing.

## 0.9.2+ — Stations / sleep / workbench lock (friend feedback)

Optional message IDs **116–119**.

- **Feeder + Lure:** Path B mirrors Yokyy station coverage beyond Saw. `FeederState` (116) on `activate` → absolute inactive; `LureState` (117) absolute health with ~1s coalesce. Join bulk pushes saw/feeder/lure. Buff on feeder stays personal.
- **Client sleep:** `Player.onEndSleep` — host immediate `TimeSync`; client sends `SleepEndRequest` (118); host **forward-only** adopts clock (no full `refreshTime` day-chain) then TimeSyncs all.
- **Workbench open-lock:** exclusive open via `WorkbenchLock` (119), same host-auth pattern as NPC dialogue lock. Level sync unchanged. Not full InteractionLock matrix (containers stay take/refund H6).

## 0.9.2+ — In-world audio + FX + entities (playtest batch)

Mushrooms, scrapes, ambients, death/blood, loot, hit SFX — dual-box playtest fixes.

### Explosions / mushrooms

- **Remote secondary FX:** `SpawnExplosionVisual` runs vanilla `spawnObjects()` (white secondary debris) + main prefab; no full `explode()` damage on remotes.
- **Dedupe:** `HandleExplosionSpawnObject` skips when local `Explodes.spawnObject` already owned by visual path (stomper / race no double pile).

### Body-push + E-drag scrape (MOS)

- **Single owner:** local push = native `ItemSounds` only; remote / drag-observed = MOS only (no double scrape).
- **Stop lag:** first quiet tick + player intent `ForceStop` (reliable quiet); drop multi-second hold tail. Body-push and E-drag aligned on `posDelta` / intent.
- **Drag scrape arm:** MOS does not start on first grab packet; `ScrapeActive` from player walk intent + hysteresis (same feel as push). Quiet mid-drag → `NoteStationary`.

### World / player audio

- **Host ambient leak:** `IsWorldAmbientLocalOnly` blocks outside forest / loop ambients from being treated as peer-spatial world audio.
- **Hit SFX at victim:** parentless `player_melee_hit` (and kin) spatialize at victim proxy / world pos — peers hear it *on them*, not on bystander.
- **Container open:** state-sync path does not re-fire `open_drawer` (local open already played it).
- **Chat:** session/product path still present; input hardening from earlier (Enter/Esc under TextField, SEND, bubble) remains in 0.9.2 body.

### Death / entities / blood / loot

- **Client death freeze:** `EnsureDeathAnimation` on host-synced corpses so death clips finish (dogs etc. no T-pose / frozen mid-hit).
- **Host death freeze:** `CharacterDeathCorpsePatch` only forces client-side corpse `isActive` off — host anims keep processing.
- **Blood:** dual-path + dedupe; `CharacterGetHitBlood` / bullet FX forward so remotes see blood; world-space where needed.
- **Loot double SFX / double nails:** `ItemDoublePickup` adds **personal extra** only (not multiply container stock); container open guards + no state-sync drawer SFX double.
- **Entity stream:** death clip + frame ride dirty EntityState; client AI disabled; host-synced death path above.

## 0.9.2 — Path B audit fixes + dialogue tree sync — 2026-07-10

Release-quality closure of critical multiplayer audit findings (C1–C4, H1, H3, H5). Product version stays **0.9.x** on purpose.

### Loader + docs

- **MelonLoader dual-build restored:** `dotnet build -p:Loader=MelonLoader` → `bin/Release/MelonLoader/`; BepInEx remains default ship + deploy.
- **README / PLAYTEST** aligned to Path B join pipeline (not stale Ironbark/F1 checklist).
- **WorldGenSharePatch** merged client gen-block (removed redundant second `onFinished` patch).

### Residuals closed (where code can close them)

- **Dual-box AppData (M8):** `SaveRootOverride` config + auto-isolate **SecondDarkwood** → `LocalLow/.../Darkwood_Second` via Harmony `persistentDataPath` (no shared tree with host Steam install).
- **Container H6:** host validates Take/Remove (type/slot); deny → `ContainerTakeDenied` (115) refunds client optimistic loot + container state resync; PlaceItem will not overwrite foreign type.
- **Landmark residual (mitigation):** connected clients cannot finish **new** worldgen (`ClientWorldGenBlockPatch`); identity remains host share/load.
- **Credits co-op end:** still permanent (by design — epilogue); log clarifies no resume.

### Chat + drag scrape (playtest) — baseline; see **0.9.2+ In-world audio** for scrape stop/intent harden

- **Chat Enter/Esc:** IMGUI-only KeyDown was dead under TextField focus. Yokyy-style dual path: raw `Input` in `Update` + OnGUI, KeypadEnter, SEND button, one-shot focus, remote speech bubble on proxy.
- **Drag scrape (initial):** MOS no longer starts on first DragSync (grab). Matches body-push: `posDelta >= 0.02` then `NoteMoving`; quiet mid-drag → `NoteStationary`.

### Join fix (J16/J17) — dual-box host freeze + ordered pipeline

- **Symptom:** World share completed then client load NRE / host freeze until client killed.
- **J16:** `updateFilePaths` + prefer `UI.initLoadGame`; host mutes gameplay flood to loading peers.
- **J17 ordered pipeline:** (1) world share on transfer link → (2) client disconnects and loads **offline** → (3) `ChapterSessionResume` reconnects with handshake `AlreadyInWorld` so host **skips re-share** and only late-join bulk + co-op traffic.
- **J18 post-load join:** reconnect waits until **playable** (not sceneLoaded+1.25s mid-SaveManager.Load); remote proxy no longer spawns on local feet; phase-3 bulk settle 1.5s; expected transfer disconnect is not “mid-night” death.

### Dialogue tree (Yokyy DialogueSync port)

- **Consumed-node sync:** On real `DialogueWindow.close`, peers broadcast `CharacterDialogue` tree state (`alreadyShown` / `disabled` / special options / portrait + NPC wantsToTalk/rep) via `DialogTreeState` (msg **113**). Host fans out; late-join bulk includes progressed trees.
- Codec is Yokyy v2-compatible (`DialogTreeWireCodec`); outcomes path still uses `DialogOutcomeSync` + world-only guard.

### Critical fixes

- **C1 Host-only time authority:** Clients no longer dual-simulate the SP day/night chain. Connected clients force `DoUpdateTime=false` (inventory FixedUpdate still runs — see 0.9.2+ night/day) and `refreshTime` is no-logic only. Host `TimeSync` applies fields + ambient/UI; day-chain edges host-only.
- **C2 Dialog without host bag pollution:** Host application of remote dialog outcomes runs under `DialogHostApplyGuard` (world-only): personal `giveItem` / `removeItem` / journal rewards are suppressed on host `Player.Instance`. Flags, world events, NPC dialogue state, and reputation still apply under host authority.
- **C3 Chapter session continuity:** Chapter load still tears the scene network briefly, then **auto rehosts (host) / reconnects (client)** via `ChapterSessionResume` instead of permanent silent solo. Credits still end co-op permanently (documented residual).
- **C4 World share honesty + fail-loud:** Path B identity remains host **WorldSaveShare** (no fake per-chunk worldgen). Share failures surface loud `WORLD SHARE FAILED` status and send `Success=false` end packets so clients do not silently wander into divergent forests. Location/landmark placement residual remains known-limit.

### High fixes

- **H1 Client→host flags:** `FlagSync` is bidirectional — client story flag changes send to host; host applies and fans out to other peers (still under apply-guard so no echo loops).
- **H3 Partial night death:** Beyond skipDay/Save suppress, also blocks `transportToHome`, `respawnAllEnemies`, and death-path `despawnCharacters` while spectating a partial night death.
- **H5 NPC dialogue lock:** One active speaker per NPC (`DialogNpcLock` msg 112 + `initiateDialogue` / `close` patches). Different NPCs may talk in parallel.

### Product

- Version **0.9.2** / Display **0.9.2 Path B (audit fixes)** / Protocol **19** (Horde LAN; optional lock **112**, dialogue tree **113**).
- Pure policy helpers in `CoopPolicy.cs` (unit-tested without Unity).
- Structural + policy tests in `DarkwoodMP.PathB.Tests`.

### Known limits (not shipped as fixed)

- Live 2-instance campaign playtest still open.
- Location/landmark chunk *placement* residual (TODO #5) — heavy worldgen rewrite out of scope.
- Dedicated server Ironbark bridge ≠ Path B LAN peers.
- Host migration after host disconnect unsupported.
- Credits / post-credits co-op not resumed (by design residual).
- SyncCheck, full InteractionLock matrix, ItemState upgrade wire deferred.
- Container simultaneous loot races (H6) not fully locked.

## 0.9.1 — Path B (Horde base) — 2026-07-10

### Breaking / load path

- **Path B rebase:** shippable mod is **Horde remaster** host-authoritative sync, not the Yokyy-core merge.
- Prior Yokyy+partial-port tree moved to `archive/yokyy-merge-0.9/` (do not load).
- Live wire: **Horde protocol 19** (LiteNetLib). Ironbark remains in-repo for tests / future dedicated bridge only.

### Product

- Identity: **YokWare Branch** `0.9.1`, GUID `com.yokware.branch`, GPLv3, Warexpor & Yokyy credit.
- Log tags: `[YokWare/…]`; boot banner documents Path B.
- BepInEx Release build deploys to Steam + SecondDarkwood plugins; removes stale `DWMP_HordeRemaster.dll` if present.
- Feature inventory: `docs/PATH_B_FEATURE_INVENTORY.md`.
- Structural tests: `DarkwoodMP.PathB.Tests`.

### Deferred (documented, not silent)

- Ironbark live client wire, dedicated server↔Horde bridge, MelonLoader dual pack, Yokyy chat, SyncCheck, ItemState, full InteractionLock matrix, IsTimeAuthority elect.

## 0.9.0 — YokWare Branch (Path A merge) — archived

Ironbark v2, dual loader packaging, gap-closure patches on Yokyy structure. Superseded as load path by 0.9.1 Path B after brief testing showed Yokyy residual bugs and sound/sync regressions versus pure Horde.
