# Changelog

## 0.9.2+ — Dream NpcScale event-only + client dream audio (2026-07-19)

Log-driven dual-box: church ruins dream (`dream_church_ruins_01`). Host
`[DreamNpcScale] scan:delayed: ChomperBlack mult=2` doubled pre-placed
chompers at load; client got premature `Characters/ChomperBlack` entity + died.
Host `pktRx` was flooded with client `DreamAudio` (30+/2s) while client also
ran local scene audio + host DreamAudio → terrible loud stack.

### Fixed — black chomper balancing ignores spawn events
- **Root cause:** Delayed location scan + `onLocationSpawned` doubled every
  allowlisted NPC already in the dream prefab (~2s after enter), not when the
  event/`CharacterSpawnPoint`/`GameEvent.spawnCharacter` actually spawned them.
  Extras also anchored near remote proxies (free ambush on the client).
- **Event-only scale:** Removed delayed scan and `DreamLocationSpawnedScalePatch`.
  Scale runs only on `Core.AddPrefab` (same path as vanilla
  CharacterSpawnPoint / GameEvent / CharacterSpawner).
- **Extras at trigger pos:** Spawn extras around the original AddPrefab position,
  not near remote player proxies.

### Fixed — client dream audio terribly loud / broken
- **Root cause:** Both peers forwarded every `_PlayAsSound` as `DreamAudio`
  (client→host flood; host→client stack on top of local ambience/music). No
  ambient/music/UI filters on that path. `PlayOneShot` also ignored proper
  volume scaling.
- **Host-only DreamAudio:** Clients no longer broadcast DreamAudio; local scene
  audio stays local; host forwards world one-shots only.
- **Filters:** Skip preset.music, never-cull BGM, `IsWorldAmbientLocalOnly`,
  personal/UI/footsteps — same discipline as PlayerAudio.
- **Player SFX in dream:** `PlayerAudio` still sends during dreams when
  `fromPlayer` (guns/equip); non-player stays EntitySound / host DreamAudio.
- **Receive volume:** `DreamAudioPlayer` applies `msg.Volume * itemScale` once
  via PlayOneShot (no double volume on source).

### Files
- `Patches/NamedNpcScalePatch.cs`
- `Patches/DreamAudioPatches.cs`
- `Patches/PlayerSoundSyncPatches.cs`
- `Sync/DreamAudioPlayer.cs`

## 0.9.2+ — Dream entry fixes: host black screen + client duplicate spawn + loud audio (2026-07-18)

Log-driven dual-box fix for three weird dream-entry bugs. Root cause: the peer path
and vanilla path fought for who enters the dream, leaving the host blind + paralysed,
the client in a double-spawn black void, and both hearing 2x music.

### Fixed — host permanent black screen + paralysis (Bug 1)
- **Root cause:** When the client initiates a dream, `OnPeerDreamEntryTransition` sets
  `Core.EnteringDream=true` + video overlay on the host. The host then enters via its
  own local vanilla path (`prepareDream` → `startDreaming`), but `FadeOutDreamTransition()`
  (the only place that resets `EnteringDream=false`) never runs on the peer path for the
  host → permanent `ClearInpuFlags()` (paralysis) + video overlay left active (blind).
- **LocalEntryFadeoutCoroutine:** In `OnLocalDreamStarted`, if `_earlyEntryTransitionPlayed`,
  waits out the remaining transition time then calls `FadeOutDreamTransition()` + resets
  flags — same cleanup that `ProcessRemoteDreamCoroutine` does for clients.
- **EntryTransitionWatchdog:** Safety timeout armed in `OnPeerDreamEntryTransition` at
  `_earlyEntryTransitionDoneAt + 20s`. If flags are still set without an active session,
  force-clears the overlay + `EnteringDream` + unfreezes world.

### Fixed — client long black void + duplicate scene spawn (Bug 2)
- **Root cause:** Client's vanilla `DreamTransition.onFinishedVideo` ran `prepareDream`
  locally (spawned the dream location) before `DreamStartPatch` blocked `startDreaming`.
  When host's `DreamStarted` arrived, `ProcessRemoteDreamCoroutine` spawned a **second**
  copy of the same dream location, producing the long black void before the bunker appeared.
- **DreamEntryClientPatch:** New Harmony Prefix on `DreamTransition.onFinishedVideo` for
  non-host peers. Returns false (skips vanilla) → sends `DreamStartRequest` to host →
  `FreezeWorld` → `MarkLocalEntryTransitionPlayed()`. Host alone enters vanilla; client
  enters via the single `ProcessRemoteDreamCoroutine` → `LoadDreamSceneCoroutine` path.
- **DreamStartPatch guard:** Client branch checks `EntryTransitionPlayedLocally` before
  re-sending `DreamStartRequest` (prevents double-request when `onFinishedVideo` already
  sent it; dialogue-direct path unaffected).
- **HandleDreamStartRequest empty PresetName:** Extended to handle the random-dream case
  (empty name from `dreamToTransitionTo`). Host runs `prepareDream("")` which rolls via
  `getPreset`; existing `DreamGetPresetPatch` handles `TryBegin` after resolution.

### Fixed — loud audio / doubled music (Bug 3)
- **Root cause:** Two compounding mechanisms: (a) duplicated dream location = duplicated
  ambient/audio sources; (b) `DreamAudioMusicPrefix` forwarded ALL music/ambience plays
  (`_PlayAsMusicOrAmbienceSound`) to peers, while each peer's own `startDreaming` already
  played the same `preset.music` locally → 2× playback at full spatial volume.
- **DreamAudioMusicPrefix deleted:** Each peer generates their own music/ambience locally;
  forwarding was pure duplication.
- **preset.music filter in DreamAudioPlayPrefix:** Added check — if audioID matches
  `Dreams.Instance.preset.music`, skip forwarding. Prevents host's `preset.music` (routed
  through `_PlayAsSound`) from doubling on the client.

### Files
- `Sync/DreamSyncManager.cs` (LocalEntryFadeoutCoroutine, EntryTransitionWatchdog,
  MarkLocalEntryTransitionPlayed, EntryTransitionPlayedLocally, OnLocalDreamStarted cleanup)
- `Patches/DreamEntryClientPatch.cs` (new — onFinishedVideo prefix for clients)
- `Patches/DreamSyncPatches.cs` (DreamStartPatch guard)
- `Networking/LanNetworkManager.DreamHandlers.cs` (empty PresetName handling)
- `Patches/DreamAudioPatches.cs` (removed DreamAudioMusicPrefix, added preset.music filter)

## 0.9.2+ — Restore ENTER WORLD after sticky mainMenu (2026-07-15)

### Fixed
- Client stuck **CONNECTED** (no **ENTER WORLD**): `HostHasShareableWorld` treated sticky `Core.mainMenu` as title-only **before** live player/load — host never shared world.
- Live `Player` + loaded/coreStarted/loadingGame wins over sticky mainMenu.
- **`TickHostWorldShareWhenReady`:** rising-edge auto share when host becomes shareable with waiting title peers (recovery path wiped by earlier session revert — restored).

## 0.9.2+ — Dialog choice no SaveSync fade (2026-07-15)

### Fixed
- **Client dialogue choices** no longer black-fade + coordinated Save for all players.
- Root cause: host world-only `displayDialogue` then vanilla `close()` → autosave → `SaveSync` (Saving UI). Host’s own talk never closed mid-choice.
- **DialogHostSilentClosePatch:** while `DialogHostApplyGuard` active, `close()` is silent (no fade/save); still hands off `startDream`.
- **SaveSyncPatch:** skip fan-out while guard active (belt-and-suspenders).

## 0.9.2+ — Dream sync fixed (2026-07-15)

Log-driven dual-box fix for client-initiated dreams (church ruins): host-auth begin, death spam, `*_done` proxy placement, peer-loss teardown.

### Fixed — dream start race
- **DreamStartPatch:** Harmony Postfix no longer runs `OnLocalDreamStarted` when client Prefix blocks (`__state`). Client sends `DreamStartRequest`, freezes world, waits for host `DreamStarted`.
- **OnLocalDreamStarted:** only **host** broadcasts `DreamStarted` (clients confirm via `DreamEntered` after remote load).

### Fixed — dream death spam
- **ClientDeathPatch / HostDeathSendPatch:** silent return when already `IsLocalDead` in dream (onDeath re-fires while spectating).

### Fixed — location / proxy
- **LocationEnter TX/RX:** strip vanilla `*_done` while dream active; place proxy on live dream pad / `Dreams.dreamLocation`, not completed GO.

### Fixed — peer disconnect mid-dream
- **FinalDreamsceneManager:** if last remote leaves and local is already dead → `EndDreamForBoth` (no zombie spectate / messy promote mid-dream).

### Fixed — entity leak (cheap)
- Client entity spawn during dream skips positions far from dream location transform.

## 0.9.2+ — Network stutters fixed (2026-07-15)

Dual-box LAN co-op: client periodic hitches (poll/FOOT) and host-side entity send allocs. Soft-reconnect visibility fix kept. Dialog world-auth + dream entry dedupe included in the same ship.

### Fixed — stutters
- **WorldQueryHelper:** OverlapSphere first; scene FOOT only with **3s per-type cache** (never `FindObjectsOfTypeAll` on hot path).
- **GameEvents:** no host rebroadcast of ambient `multipleFire` loops.
- **Lure/stations:** stored-pose outbox; interest cull; absolute health apply; pending flushes **1s throttle**.
- **Entity broadcast:** `CharacterTracker.CopyAll` (no 10 Hz `ToArray`); `SendRawToReadyPeers` (no `ConnectedPlayerIds` List alloc).
- **Client RX:** corpse setup off poll path; pending lock/feeder/saw/constructible throttled.

### Fixed — visibility
- PlayerState always sent to loading peers; phase-3 soft reconnect not muted.

### Added — diagnostics
- **CoopPerfProbe** (host + client): `role=`, `top=` pkt types, `footMs/footType`, pending queues, `hostEntSend`.
- Logging docs/config truth; deploy strips orphan `System.*` facades from plugins.

### Changed — dialog / dream (parity)
- Client defers world dialog outcomes; host applies once; source node alreadyShown; tree flush every choice.
- Dream start request dedupe when session already Starting.

## 0.9.2+ — Dialog world-auth + dream entry dedupe (decompile parity)

### Dialogs (vs DialogueWindow / DialogueButton)
- **Client co-op `displayNextBoard`:** defer world mutations — skip `Flags.setFlag`, `Events.fireWorldEvent`, `OutsideLocations.prepareLocation` / `returnToWorld`; clear local `wantToDream` / `dreamToStart` so host owns dialogue dreams.
- Personal give/remove/journal still on speaking client; host C2 suppress unchanged.
- **Host DialogOutcome:** mark **source** node `alreadyShown`/`gossipShown` (vanilla onPress); then world-only `displayDialogue(target)`.
- **Tree flush every choice** (not only close) via `DialogTreeSync.TryBroadcastFromNpc`.
- Client sends `DialogueName` = source node captured in onPress Prefix.

### Dreams
- Host **dedupe** DreamStartRequest when session already Starting same preset (DialogOutcome race) — Session Event log.
- End path unchanged (ApplyRemoteDreamCleanup already applies outcome effects).

### Files
- `DialogClientWorldDefer.cs`, `DialogClientWorldDeferPatches.cs`, `DialogOutcomePatch.cs`, `FlagSyncPatches.cs`, `CoopPolicy.cs`, Handlers, DreamHandlers, ModRuntime

## 0.9.2+ — Full logging audit + deploy hygiene (CoopPerfProbe)

### Logging (prove hitches on both roles)
- **`CoopPerfProbe`** (alias `ClientPerfProbe`): Host **and** Client, 2s Event lines.
- Report fields: `role=`, `poll/upd/physBuild`, `entApply`, **`pktRx` + `top=` message types**, **`footN/footMs/footType`**, **`pend lure/lock/light/trap/…`**, host **`hostEntSend`**.
- FOOT sites (`WorldQueryHelper`, physics Rb scan, inactive Character scan) record type + ms.
- Join bulk one-shots promoted **LegacyInfo → `ModLog.Event(Session)`** (Support packs see join health).
- Docs/config: Support default; LegacyInfo = Dev only; stutter checklist in `docs/LOGGING.md`.

### Deploy hygiene
- Deploy target still only mod + LiteNetLib; **strips orphan `System.*` facades** from host/client plugins if present.

### Files
- `Logging/CoopPerfProbe.cs`, `LanNetworkManager*.cs`, `WorldQueryHelper`, `WorldPhysics`, `TrapNetworkId`, `EntityStateBroadcastService`, `ClientEntityInterpolationService`, `ModConfig`, `docs/LOGGING.md`, csproj

## 0.9.2+ — Client-only stutters (host clean)

### Evidence
- Host: clean. Client: sustained `poll~110` `maxMs~50–60` `findOfType=2`, often `entApply=0.1 applied=0`.
- Far lure health still applied every 1s; `Lure` has no collider → OverlapSphere miss → scene FOOT ~50ms.
- `EnsureDeadNpcCorpses` ran inside EntityState **RX** (poll path) every 2s with `GetAll().ToArray()`.

### Fix
- `WorldQueryHelper`: 3s **per-type FOOT cache** (at most one scan / type / 3s).
- Client lure: **interest cull** before lookup; host skips broadcasting far lures (death still sends).
- Corpse setup moved to `TickClientCorpseSetup` in Update (not poll); `CopyAll` not `ToArray`.
- Entity unmatched cleanup uses `CopyAll`.

## 0.9.2+ — Stutters still present: real hot-path (Steam-era peer + FOOT)

### Evidence after GameEvents fix
- Clean windows: `fps~100 poll~2.5 findOfType=0`
- Dirty windows: **same `pktRx~85`** but `poll~120 upd~115 maxMs 40–60`, `findOfType=4`, `[LureSync]` ~1/s
- Host entity path: `CharacterTracker.GetAll()` → **`ToArray()` every 10 Hz** (dual-box host hitch freezes both instances)
- Steam commit (`a62417a`) also made `ConnectedPlayerIds` allocate a **new List every entity send**

Steam P2P itself is idle on LAN (`PollSteamBackend` no-ops). The regression window matches the peer-abstraction + bulk FOOT paths that ship with that commit.

### Fix
- `WorldQueryHelper`: **OverlapSphere first**, scene FOOT only as fallback (never `FindObjectsOfTypeAll`)
- Lure outbox: store full pose — **no FindNearest on host flush**; apply absolute health; Trace log
- Pending lock/feeder/saw/constructible flushes: **1s throttle** (were every-frame FOOT if pending)
- Host join bulk locks/lights: scene `FindObjectsOfType` not `FindObjectsOfTypeAll`
- `CharacterTracker.CopyAll` buffer; entity broadcast uses it + `SendRawToReadyPeers` (no List alloc)

## 0.9.2+ — Stutter fix (log-driven, minimal)

### Evidence (dual-box logs)
- **Client** `[Perf]`: steady fps~100 / poll~2ms, then periodic **maxMs 350–390**, **poll 500–620ms** over 2s windows. `findOfType=0` / `fullRbScan=0` (probe never instrumented `WorldQueryHelper`).
- Correlated **`[LureSync] applied`** ~1/s (expected coalesce).
- **Host**: no ClientPerfProbe; flood of **`[GameEventsSync]`** for ambient `multipleFire` loops (`worldEvent_fireGroomText1_*`, `groomHitHeadSound_*`).

### Root cause
1. `WorldQueryHelper.FindNearest*` used **`Resources.FindObjectsOfTypeAll`** (assets + prefabs) on every lure/game-event/lock apply.
2. Host **rebroadcast every `multipleFire` tick** even though clients already run those loops locally → net spam + more FindNearest on client.

### Fix (only these)
- `WorldQueryHelper`: scene `FindObjectsOfType(true)` + valid scene filter; count toward ClientPerfProbe `findOfType`.
- `GameEventsFiredPatch`: do **not** broadcast `multipleFire` ambient events.
- Pending lure flush: **1s throttle** (same idea as pending GameEvents).

### Files
- `Sync/WorldQueryHelper.cs`, `Patches/GameEventsFiredPatch.cs`, `Networking/LanNetworkManager.Handlers.cs`

## 0.9.2+ — Revert session perf experiments; client cannot see host

### Revert
All uncommitted hitch "optimizations" from the dual-box perf session are **reverted** to last commit (`a62417a` / main). Rate limits, PlayerState dirty-gates, flush staggering, zero-copy RX rewrites, etc. are gone — they were not present when co-op was known-good.

### Critical: client cannot see host
**Symptom:** Host sees client proxy; client never gets host body. Log: `[Light] RX drop p1 proxy=null`, no `[Proxy] Created proxy for player 1`.

**Root cause:** Host `Broadcast(PlayerState, skipLoadingPeers: true)` while peer is in `_peersLoadingWorld`. Soft reconnect / world share marked loading → **no host PlayerState** → client never `EnsureRemoteProxy(1)`.

**Fix (minimal):**
- Always send **PlayerState** (never skip loading peers).
- Phase 3 `AlreadyInWorld`: do **not** `MarkPeerLoadingWorld`; mark gameplay-ready immediately.
- World share mute skips coop-reconnect peers.

### Files
- `LanNetworkManager.cs`, `LanNetworkManager.Handlers.cs`, `WorldSaveShareService.cs`

Unreleased Path B work after **0.9.2** tag lives under **0.9.2+** sections below (newest first). Protocol stays **19**; optional message IDs **112–126**. Keep this file updated whenever playtest/audit fixes land — do not leave them only in plans or COOP_COVERAGE.

**Agent rule:** every ship of playtest fixes / features / regressions must add a **0.9.2+** section here in the same change (see root `AGENTS.md` → Changelog discipline).

## 0.9.2+ — Steam P2P backend + loot-share cleanup + outside-location visibility

Shipped in `a62417a`. Protocol **19** unchanged. LAN LiteNetLib path fully retained.

### Steam connection (separate backend)
- **`ConnectionBackend`:** `Lan` | `Steam` per session — not mixed.
- **Steam:** friends-only lobby + classic `SteamNetworking` P2P (`SteamCoopTransport`); same Horde message framing as LAN.
- **UI:** MULTIPLAYER → `HOST LAN` / `JOIN LAN` / `HOST STEAM` / `JOIN STEAM`; SETTINGS lobby-id field + host copy/invite overlay.
- **Config:** `Network.SteamLobbyId` (host auto-fills); `HostPassword` also used as Steam lobby conn key.
- Host migration remains **LAN-only** (Steam host leave → clean disconnect).
- Peer send/receive shared via `SendRawToPlayer` / `ProcessInboundMessage`; entity broadcast no longer depends on `NetPeer` only.
- Bugfixes before ship: late-join/location/damage paths use backend-agnostic peer ids; `ConnectedPlayerIds` snapshot (no net471 KeyCollection cast crash); failed Steam lobby → full `StopNetwork`.

### Loot share
- Removed **`LootShareMode.Double`** (old config value falls through to `ScaleWithPlayers`).
- **No longer scaled:** regular dog meat (`meat`), wood, nails (`DefenseMatTypes` emptied).
- **Scaled (hideout fuels / furnace exp items):** odd mushrooms (incl. large/glowing variants), odd/mutated meats, red egg, embryo (`exp_piskle`), life potion, dead rat, fish, mutated cockroach — see `CoopBalance.UpgradeItemTypes`.

### Outside-location player visibility
- After bunker/village/etc. loading screens, remote proxies could lerp across the map or miss location geometry.
- **`RemotePlayerProxy`:** hard snap when displacement &gt; 150u (not only first state).
- **`OutsideLocationVisibilityPatches`:** settle after `transportToLocation` + return-to-world after `returningOnTeleportedPlayer`.
- **`LanNetworkManager`:** `OnLocalOutsideLocationSettled` / `OnLocalReturnedToWorld` / proxy re-place + LocationEnter rebroadcast; `PlayerPositionManager.TryGetRemote` for snap targets.

### Entity spawner
- Dual-target **BepInEx / MelonLoader 0.7.x** (`-p:Loader=MelonLoader` → `bin/Release/MelonLoader/YokWare.EntitySpawner.dll`).

### Files
- `Networking/ConnectionBackend.cs`, `Networking/Steam/SteamCoopTransport.cs`, `Networking/LanNetworkManager.Steam.cs`
- `LanNetworkManager*.cs`, `EntityStateBroadcastService`, `HostMigration`, `ModConfig`, multiplayer UI
- `CoopBalance`, `ItemDoublePickupPatch`, `RemotePlayerProxy`, `OutsideLocationVisibilityPatches`, `PlayerPositionManager`
- `DarkwoodMP.EntitySpawner/*`

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
