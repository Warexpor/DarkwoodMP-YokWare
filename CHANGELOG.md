# Changelog

## Versioning

**Current product line: `0.7.x`.** Plugin / DisplayVersion ship as **0.7.43** and continue from there.

Labels **`0.9.x` / `0.9.2+` in older sections below were too ambitious** — they implied near-1.0 maturity the campaign still does not have (dream sync and other domains still need soak). Those headings are **historical mislabels**; do not treat them as the live semver. New ship notes use **`## 0.7.x — …`**. Protocol is **21** (unchanged from 0.7.42).

---

## 0.7.43 — Client entity present: death SFX, floaty roam, corpse vanish, claim (2026-08-04)

Playtest after 0.7.42: rabbit runs in then freezes/vanishes on approach; roaming dogs float with no anim until aggro; client kill = silent death; looted dog corpse disappears in front of client. **Protocol 21** (no wire change — both boxes still need this DLL).

### Roots
- **EntitySound send cull used 3D distance** while interest is XZ-only — player Y ~−1984 vs NPC Y can exceed 1400 and drop Death/flee SFX. `IsNearAnyListener` now XZ.
- **Death SFX** also plays on Alive→dead snap (`NoteLocalDeathPresentation`); EntitySound Death shares that path (one play max). Fixes silent kill when host cull/order races.
- **Floaty roam:** after `SetActive` cycle, clip name stuck while `Playing=false` — restart clip when stopped.
- **Corpse vanish:** phantom stale cleanup `Destroy`’d dead GOs ~5s after host stopped streaming; far-target hide used host pos while local corpse still at feet. Never hide/destroy dead/Item corpses; hide only when **local** pos is outside interest.
- **`EnsureEntityAwake`** no longer force-revives `isActive` on corpses.
- **`ClaimClosestRadius` 250→60** (log: claimed Dog at d=214).

### Files
- `LocalAudioService.cs`, `ClientEntityInterpolationService.cs`, `LanNetworkManager.Handlers.cs`
- `PluginInfo.cs` / `AssemblyInfo.cs` (**0.7.43**, proto **21**)

## 0.7.42 — Entity audio / AI root-cause (flee SFX, hit react, far aggro, roam) (2026-08-04)

Playtest after 0.7.41: crow flee silent on client; wrong/double entity loops; beartrap death 2×; client melee no hit SFX/Hit roll; dogs aggro from too far; fled crows freeze mid-map; too many visible roamers; micro-event dog walks off to host waypoints. **Protocol 21** (EscapingStart/Start2 — both boxes must update).

### EntitySound = vanilla CharacterSounds surface
- Forward `escapingStart` / `escapingStart2`; suppress nested Idle while `playEscapingLoop` (single Escaping message).
- Resolved idle loop name (`idleLoopAggressive` when chasing).
- Client host-synced: `die2` soundless; suppress local `play`/`playSingleInstance`/foot (EntitySound + PlayerAudio own them) — fixes beartrap death triple.

### Client melee hit presentation
- On `PlayerAttack` send: local `playGetHitByAxe1` + `Hit1/2/3`; ignore host GetHit echo ~0.35s.

### Far aggro
- `HostCanSeeEnemyPatch` CASE 3: `attackCharacter` only within **nearViewDistance**; far = listen/sight flags.
- EotF / ProxyAggro: commit only at near (smell alone no longer far-instant attacks).

### Lifecycle / presentation
- `StopDriving` / far snaps: hide fleeing/phantom entities (no frozen crow statues).
- Outside interest: deactivate never-host-synced locals.
- `HostCheckStuffPatch`: temp keep-alive **1400** (not 3500).
- Event/temp dogs: clear host `bigLocation` waypoints after `spawnCharacterAround` / `attackPlayer` redirect.

### Files
- `EntitySoundSyncPatches.cs`, `SyncMessages.cs`, `LanNetworkManager.Handlers.cs`, `ClientEntityInterpolationService.cs`, `ClientCombatPatches.cs`, `HostAIPatches.cs`, `LanNetworkManager.cs`, `NightSpawnRedirectPatches.cs`
- `PluginInfo.cs` / `AssemblyInfo.cs` (**0.7.42**, proto **21**)

## 0.7.41 — Entity P1 audit pass (broadcast, interest, sleep/eat, corpse) (2026-08-04)

Composer audit vs vanilla `Character*`: no confirmed P0; ship residual P1s. **Protocol 20** (EntityState `Flags` byte — both boxes must update).

### Broadcast starvation
- Cap **192 → 256**; two-pass fill (near 1400 any player first, then 3500 band) so dense night packs do not starve combat NPCs at end of tracker list.

### Client interest vs destroy
- Keep drive/wake interest at **1400** (FPS; host still streams 3500).
- Unmatched/unmapped destroy only **inside interest**; grace **3s**; pending match **0.9s** — far save NPCs stay for claim when the player walks up.

### Sleep / eat on wire
- `EntitySnapshotNet.Flags` bit0=sleeping bit1=eating; client applies to `Character.sleeping` / `eating` so corpse-eating / sleep pose is not idle-only.

### ProxyAggro LOS
- `ProxyAggroCheck` now requires FOV+raycast (or smell) like `HostCanSeeEnemyPatch` — no through-wall predator acquire on nearView alone.

### Client corpse pipeline
- `CharacterDeathCorpsePatch` transfers death inventory only; Item + `isActive=false` deferred to `TickClientCorpseSetup` after death anim or **1.2s** (avoids freezing mid-death clip).

### Files
- `EntityStateBroadcastService.cs`, `WorldMessages.cs`, `ClientEntityInterpolationService.cs`, `LanNetworkManager.cs`, `LanNetworkManager.Handlers.cs`, `CharacterDeathCorpsePatch.cs`
- `PluginInfo.cs` / `AssemblyInfo.cs` (**0.7.41**, proto **20**)

## 0.7.40 — Flee from client, phantom dogs, client scrape 2×, workbench lock off (2026-08-04)

Playtest 0.7.39 entity pass: crows/rabbits ignored the client (stood on corpses); client saw extra stale dogs (no anim) next to live synced ones; client push/drag scrape felt doubled; workbench exclusive-open message unwanted. **Protocol 19 unchanged.**

### Flee fauna vs client (P0)
- 0.7.39 skipped *all* proxy flee to stop "chase feel" — client approach became a no-op (host AI never `runAway` from proxy).
- **Fix:** host `canSeeEnemy` / proxy collide restore `runAway(proxy)` for `flee` / `fleeAndDespawn` only (no `attackCharacter`). Still skip ProxyAggro 0.5s spam.

### Client phantom / stale dogs (P0)
- Pending match timed out at **0.2s** + MatchRadius **15** → `AddPrefab` phantom while save NPC sat elsewhere → duplicate frozen twin (AI off, no EntityState).
- Unmatched cleanup skipped characters **without** a stable id → ghosts never culled.
- **Fix:** claim closest same-name within **250** before phantom; pending timeout **0.6s**; destroy unmapped locals after grace.

### Client double scrape (P1)
- Host PhysicsState echo could arm MOS while native `ItemSounds` already played for the pusher.
- **Fix:** `NoteClientPhysicsSent` grace (2s) on outbound free-bodies; ApplySnapshot / MOS / PlayerAudio honor it.

### Workbench lock (parked)
- Exclusive open + "Someone is already using the workbench…" **disabled** (patches + handler no-op). Wire ID kept.

### Parked
- Crafting item desync if both use same bench at once (expected with lock off).

### Files
- `HostAIPatches.cs`, `ClientEntityInterpolationService.cs`, `ItemMovingSoundHelper.cs`, `MovingObjectSoundService.cs`, `WorldPhysicsSyncService.cs`, `WorkbenchLockPatches.cs`, `LanNetworkManager.Handlers.cs`
- `PluginInfo.cs` / `AssemblyInfo.cs` (**0.7.40**)

## 0.7.39 — Walkie hitch, flee-fauna chase, scrape/Save/door hygiene (2026-08-04)

Playtest 0.7.38: `[PerfSeg] hotSeg=walkie` ~50–64ms every ~5s both boxes; rabbits/ravens chased both players while dogs correctly fought the client; lamp/stool MOS start/stop thrash; death SaveSync storm; door open spam. **Protocol 19 unchanged.**

### Walkie hitch (P0)
- `WalkieItem.Tick` kept calling `InjectIcon` → `FindObjectsOfTypeAll&lt;tk2dSpriteCollectionData&gt;` every 5s after settle.
- **Fix:** `_iconSettled` — never scan again once icon injected (or texture failed).

### Flee-fauna chase (P0)
- `ProxyAggroCheck` + `HostCanSeeEnemy` / proxy collide forced `runAway`/`attackCharacter` toward proxies for flee animals → chase feel.
- **Fix:** predators only (`attacksFaction(player)`); skip `flee`/`fleeAndDespawn`; no proxy flee-on-collide; `attackCharacter(proxy)` blocked for non-predators. Dogs unchanged.

### MOS scrape thrash (P1)
- Body-push quiet threshold **0.1** + two quiet ticks before soft-stop (host→client and client→host paths).

### Death SaveSync storm (P1)
- Day/night death arms suppress; first Save still fans out, then **6s** drop of further SaveSync requests/broadcasts.

### Door open spam (P1)
- `Door.open` Postfix skips broadcast if door was already open (Prefix `__state`).

### Late-join flush (P2)
- `TickHeavyLateJoinBulk`: **one peer × one phase per frame** (was all peers).

### Parked
- First-join mid-share disconnect; entityBroadcast baseline cost; entTick Character FOOT bursts.

### Files
- `Items/WalkieItem.cs`, `Networking/LanNetworkManager.cs`, `HostAIPatches.cs`, `WorldPhysicsSyncService.cs`, `SaveSyncPatches.cs`, `DeathStateTracker.cs`, `DreamDoorSyncPatches.cs`, `LanNetworkManager.Handlers.cs`, `HostDeathSendPatch.cs`
- `PluginInfo.cs` / `AssemblyInfo.cs` (**0.7.39**)

## 0.7.38 — Hitch gap probe (contiguous Update segs) (2026-08-04)

0.7.37 playtest: steady `upd≈55–60` still had `segMax=(none)` — stall was outside wrapped blocks. **No behavior fix claimed.** Protocol 19 unchanged.

### Probe
- Contiguous Update segments: walkie, flushPending, peerRoster, scenarioApply, gameEvents, meleeDebounce, flagSync, entityBroadcast, proxyAggro, timeShadow, physTimer.
- `[PerfSeg] updFrame=Xms hotSeg=name:ms` when a single Update slice ≥25ms (names the hottest sub-seg that frame).

### Files
- `Logging/CoopPerfProbe.cs`, `Networking/LanNetworkManager.cs`
- `PluginInfo.cs` / `AssemblyInfo.cs` (**0.7.38**)

## 0.7.37 — Lamp body-push scrape silent + hitch seg probes (2026-08-04)

Playtest after 0.7.36: host body-push of `Lamp_old_yellow_01` moved on client but scrape was silent; E-drag scrape was fine. Chairs / wardrobe push+drag scraped OK. Periodic `upd≈50–70` hitch still present (`footN=0`, ~4s cadence) despite LAN IP cache. **Protocol 19 unchanged.**

### Lamp push scrape (root cause)
- `IsSceneFixedLightItem` treated every `Item.isLight` as a wall lamp and **skipped PhysicsState free-body build**.
- Body-push scrape for observers is armed from PhysicsState→MOS; DragSync still ran for E-drag → drag heard, push silent.
- **Fix:** only treat lights as fixed when they are **not** `draggable` and have **no** ItemSounds moving scrape. Floor lamps stream pose like stools.

### Remaining hitch (instrumented, not claimed fixed)
- Logs: SaveSync dedup / DoorTracker / fullRbScan fixes hold; `upd` spike still ~every 4s on **both** host and client with `footN=0` (not PeerRoster packet correlation).
- **Added:** Update sub-segment probes (`[PerfSeg]` when ≥25ms; Perf line `segMax=name:ms`) for flushPending / peerRoster / gameEvents / flagSync / entityBroadcast / proxyAggro.

### Files
- `Sync/WorldPhysicsSyncService.cs` (`IsSceneFixedLightItem`)
- `Logging/CoopPerfProbe.cs`, `Networking/LanNetworkManager.cs`
- `PluginInfo.cs` / `AssemblyInfo.cs` (**0.7.37**)

## 0.7.36 — Periodic hitch cadence + post-dream Save spike (2026-08-04)

Playtest after 0.7.35: host (and client) felt small periodic frame hitches; after dream exit a larger hitch lined up with SaveSync. **Protocol 19 unchanged.**

### ~4s `upd≈60–80` hitch
- Host roster gossip every 4s called `NetworkInterface.GetAllNetworkInterfaces()` for LAN IPv4 — known Windows main-thread stall (~40–80ms), showed as `upd` with `footN=0`.
- **Fix:** cache LAN IPv4 for 60s; invalidate on `StartHost`.

### ~30s `objInterp≈45` hitch
- `DoorTracker.Cleanup` ran `FindObjectsOfType<Door>` from LateUpdate `UpdateObjectInterpolation` every 30s (uninstrumented FOOT).
- **Fix:** null-purge only; register doors on `OnEnable` as well as Awake (chunk re-enable).

### Post-dream SaveSync spike (`upd≈400+`)
- Host end-dream Save → broadcast; client independent vanilla Save → SaveSync request → after 3s cooldown host Saved **again** + re-broadcast.
- **Fix:** ignore client SaveSync requests while still inside the host broadcast cooldown.

### Files
- `Networking/HostMigration.cs`, `LanNetworkManager.cs`, `LanNetworkManager.Handlers.cs`
- `Sync/EntityTrackers.cs`
- `PluginInfo.cs` / `AssemblyInfo.cs` (**0.7.36**)

## 0.7.35 — Client dream bed "Lie down" no-op (2026-08-04)

Playtest after 0.7.34: client pressing the dream-exit **Lie down** (`CustomCursorAction` → `onActivate` → one-shot GE `item` → `endDream` / `dream_underground_bed`) did nothing; host pressing the same ended the dream for both. **Protocol 19 unchanged** (optional msg `ActivateCursorAction = 130`).

### Root cause
- Client one-shot `GameEvents.fire` is blocked by design (`GameEventsFiredPatch`).
- `CustomCursorAction.activate` / ItemMenu `onActivate` had no client→host request (unlike `Examinable` / `ExamineObject`), so the press never reached the host.

### Fix
- Client defers `Core.sendTriggerInfo(..., onActivate)` when the target has `CustomCursorAction` → host runs `activate()` (authoritative GE + `initiateEndDreaming` fan-out).
- Dream-pad resolve prefers pad instances (clone trap).

### Parked / still watching
- Host post-dream micro-hitches — addressed in **0.7.36**.

### Files
- `Patches/CustomCursorActionSyncPatches.cs` (new)
- `Networking/Messages/NetMessageType.cs`, `WorldMessages.cs`, `LanNetworkManager.cs`
- `PluginInfo.cs` / `AssemblyInfo.cs` (**0.7.35**)

## 0.7.34 — Host dialog text leak + dream-end GE stutter (2026-08-04)

Playtest after 0.7.33: host saw client dialogue lines on their DialogueWindow; dream end failed with periodic client stutters (`upd=700ms+`) until hostLostMidDream. **Protocol 19 unchanged.**

### Host sees peer dialogue text
- World-only `displayDialogue` still activated the dialogue text root (only blackscreen was suppressed).
- **Fix:** while `DialogHostApplyGuard` is active, hide dialogue/options/items/portrait and `WritingText.forceFinish` so boards drain without on-screen peer lines.

### Dream-end stutter / cannot finish
- Host forest-spirit `def_glow` / `def_shadow` one-shots fan out; client has no durable GE at chase coords → pending queue + `FindObjectsOfType` every 0.5s for up to 90s.
- **Fix:** do not broadcast those ephemeral FX; if a miss still arrives after pad ready, drop (do not queue); clear them on dream end with other pad pending.

### Files
- `Patches/DialogHostPresentationSuppressPatches.cs`, `Patches/GameEventsFiredPatch.cs`
- `Networking/LanNetworkManager.Handlers.cs`
- `PluginInfo.cs` / `AssemblyInfo.cs` (**0.7.34**)

## 0.7.33 — Dream karuzela rotate on client (2026-08-04)

Client walked into `area_karuzela_rotate_dream_underground` → host carousel spun, client stayed still. **Protocol 19 unchanged.**

### Root cause
- Host proxy enter fired the volume; `GameEvents` is `multipleFire` so `GameEventsFiredPatch` does not broadcast (by design — ambients run locally).
- Client `OnTriggerEnter` was fully suppressed, so the client never ran that local multipleFire path either.

### Fix
- Drop client EventTriggers enter/exit suppress (one-shots still blocked at `GameEvents.fire` Prefix).
- Run proxy area enter/exit on **all** peers so host walking into the volume also starts RotateIt on clients.

### Files
- `Patches/EventTriggersProxyPatches.cs`
- `PluginInfo.cs` / `AssemblyInfo.cs` (**0.7.33**)

## 0.7.32 — Undo audio crutches (flashlight 2D / footstep 280) (2026-08-04)

0.7.31 papered over remote SFX with a 2D flashlight path and a tighter 280 footstep gate. Both were symptom patches. **Protocol 19 unchanged.**

### Audio (root-cause)
- **Flashlight/torch:** stay spatial at the proxy (bunker reverb). Soft-tail cut was tiny `minDistance` burying the quiet end under attenuation — use Logarithmic + `DefaultMinSpatialDistance` / `DefaultMaxSpatialDistance`, not 2D.
- **Proxy footsteps:** gate + `maxDistance` match `DefaultMaxSpatialDistance` (same as `AudioSuppression`). No invented 280 ceiling.

### Files
- `Audio/LocalAudioService.cs`, `LanNetworkManager.Handlers.cs`
- `PluginInfo.cs` / `AssemblyInfo.cs` (**0.7.32**)

## 0.7.31 — Backup/save isolation + dream door lock + audio edges (2026-08-04)

Playtest after 0.7.30: client inv/skills still bled across saves; dream `door_underground` double-open sound; host input fully locked after that dialogue; remote flashlight click cut near the end; host footsteps whispered/cut just outside hearing range. **Protocol 19 unchanged.**

### ClientBackup
- Solo host (`PeerCount==0`) used to restore `client_backup_self` over sav.dat (`IsConnected` gate was wrong).
- ManualSave slot load remints CampaignId.
- Host push refused when day-1 world + progressed backup fingerprint mismatch (stale CampaignId reuse).

### Dream door (`door_underground` / `onLeaveDoorDialogue_dream_underground`)
- Skip `HostEnsureDialogueDoorOpen` when leave-door GE already fired (was GE openSound + DoorOpen openSound).
- Client leave-door: deferred backup ForceOpen only if still closed after GE delay.
- Drop redundant second `BroadcastDoorOpened` after host `open()`.

### Host input lock
- World-only `displayDialogue` NRE / inactive DialogueWindow left `Core.forbidInputs` stuck after `changePortrait`.
- Clear forbidInputs on displayNextBoard Postfix (guard), SilentClose, drain finally, and catch path; re-activate DialogueWindow before world-only apply.

### Audio (superseded by 0.7.32)
- Had forced flashlight 2D + footstep 280 cap — reverted.

### Files
- `UI/ManualSaveGUI.cs`, `Networking/ClientStateBackup.cs`, `LanNetworkManager.Handlers.cs`
- `Patches/DreamDoorSyncPatches.cs`, `DialogHostSilentClosePatch.cs`, `DialogHostPresentationSuppressPatches.cs`
- `Audio/LocalAudioService.cs`
- `PluginInfo.cs` / `AssemblyInfo.cs` (**0.7.31**)

## 0.7.30 — New-world ClientBackup leak + dream prepare NRE (2026-08-04)

Host new world in a reused profile slot pushed a prior campaign’s ClientBackup (`inv=2` / dream-pad coords) onto day-1. Separately, skill dream entry froze peers: host `prepareDream` died inside `prepareLocation` → `closeInventory` NRE, so `DreamStarted` never fans out. **Protocol 19 unchanged.**

### ClientBackup
- **Root:** `MintNewCampaignId` only ran when already Host — offline “new game then host MP” reused leftover `dwmp_coop_meta` CampaignId and matched old `client_backup_p*_….json`.
- **Fix:** mint on any fresh worldgen (not only Host role).

### Dream
- **Root (host Player.log):** `NullReferenceException` in `Player.closeInventory` during `OutsideLocations.prepareLocation` (often dangling `talkedToNPC` without Inventory after world-only dialog). Session stuck Starting; client waited then `hostLostMidDream`.
- **Fix:** clear NPC-without-Inventory before close; swallow closeInventory NRE so prepare can finish; on prepareLocation failure abort session + DreamEnded reject to peers.
- **Watchdog:** client entry watchdog no longer treats early CutsceneSync as “dream active” (was exiting immediately and never clearing the void).

### Files
- `Patches/WorldGenSharePatch.cs`, `Patches/PlayerInventoryPatches.cs`
- `Sync/DreamSyncManager.cs`
- `PluginInfo.cs` / `AssemblyInfo.cs` (**0.7.30**)

## 0.7.29 — Steam SNS join tear + connect timeout (2026-08-03)

Client Steam join could stick on “joining lobby…” if SNS closed before peer map, or hang forever if `Connected` never arrived. **Protocol 19 unchanged.**

- Pre-handshake SNS close/problem → `OnSteamLobbyFailed` / `StopNetwork` (no silent no-op).
- Mid-join `OnSteamSessionFailed` without peer map → tear client session.
- 20s ConnectP2P timeout in `SteamCoopTransport.Poll` → `SNS timeout`.

### Files
- `Networking/Steam/SteamCoopTransport.cs`, `LanNetworkManager.Steam.cs`
- `PluginInfo.cs` / `AssemblyInfo.cs` (**0.7.29**)

## 0.7.28 — Voice/walkie + SteamNetworkingSockets (2026-08-03)

Ported Steam Voice proximity chat, craftable walkie radio, and Steam session transport from a friend's Melon/Yokyy DLL onto Path B Horde (protocol **19** unchanged). Full Yokyy sync dump (`ActionEvent`, SyncCheck, InteractionLock, …) stays deferred.

### Steam SNS (2B)
- Replaced classic `SteamNetworking` P2P with **SteamNetworkingSockets** (`CreateListenSocketP2P` / `ConnectP2P` / poll group) under `SteamCoopTransport`.
- Lobby keys still `yokware` / `proto` / `conn` / `name`; join now **rejects protocol mismatch**.
- Relay warm + send-buffer knobs (`SteamRelay`); lobby type config `friends|public|private`; `+connect_lobby` consume.
- LAN LiteNetLib path unchanged. Still **no** Steam host migration.

### Voice + Walkie (1A)
- `NetMessageType.VoiceData = 129` (optional; host fans out Unreliable).
- Steam `GetVoice` / `DecompressVoice` proximity mix + walkie radio LPF when both carry a walkie.
- Craftable `walkie_talkie` (2 scrap + 1 nail, workbench lvl 1) with embedded icon.
- Config: `VoiceEnabled`, `VoiceMode` (ptt/open), `VoicePttKey` (default V), ranges/gain, `WalkieItemName`.

### Parked
- Friend Melon sync stack / ActionEvent combat path
- Steam host migration / PeerRoster on Steam
- Text ChatHud re-enable (`Enabled = false`)

### Files
- `Networking/Steam/SteamCoopTransport.cs`, `SteamRelay.cs`, `LanNetworkManager.Steam.cs`
- `Audio/VoiceChatService.cs`, `Networking/Messages/VoiceDataMessage.cs`, `NetMessageType.cs`
- `Items/WalkieItem.cs`, `Resources/walkie_talkie.png`
- `PluginInfo.cs` / `AssemblyInfo.cs` (**0.7.28**)
- Friend DLL decompile stays local under `reference/` (gitignored)

## 0.7.27 — Dream black void + ClientBackup policy rewrite (2026-08-02)

Client skill/sleep dream → silent black void (no pad, no body). Host `prepareDream("")` after empty `DreamStartRequest` never reached `DreamStarted`; watchdog force-cleared host while client stayed black. Separately ClientBackup still applied wrong/empty inv+skills (ManualSave on host restored empty self; fingerprint gate was a no-op on legacy files and host/client hashes diverge anyway). **Protocol 19 unchanged.**

### Dream void
- **Root:** depleted `presetList` in the save → vanilla empty roll IndexOutOfRange (or hang after Save); no `DreamStarted` fan-out; client held opaque black.
- **Fix:** refill random pool from `allPresets` before host roll; reset `dreamPrepared` on prepare failure / host watchdog; client 25s entry watchdog clears black/`EnteringDream`.

### ClientBackup (holes closed)
- **Policy:** campaign-keyed only (dropped fingerprint match — host vs client package hashes diverge after share).
- **Refuse empty** collect/restore/push (no more lvl0/inv0 wipe).
- **Host never** overlays ClientBackup after ManualSave load (sav.dat is the host body).
- **Prefer local self** over host push when richer/newer; host push only fills a gap.
- **Pre-load snapshot** skips empty collects so title/load races cannot clobber a good self file.
- Exit mid-dream with omitted pose keeps prior overworld coords instead of writing `(0,0)`.

### Files
- `Patches/DreamSyncPatches.cs`, `DreamEntryClientPatch.cs`
- `Sync/DreamSyncManager.cs`, `Networking/LanNetworkManager.DreamHandlers.cs`
- `Networking/ClientStateBackup.cs`, `LanNetworkManager.Handlers.cs`, `UI/ManualSaveGUI.cs`
- `PluginInfo.cs` / `AssemblyInfo.cs` (**0.7.27**)

## 0.7.26 — Client oven dialogue softlock + backup-per-save (2026-08-02)

Client stuck looping oven `lookAtOven` ↔ `lookAtBottle` with no new options; host applied each choice world-only but story flags / dialogue tree never came back. Separately, host late-join pushed a mid-dream ClientBackup (`inv=2`, pad coords, Aug 1 stamp) onto today's save of the same `CampaignId`. **Protocol 19 unchanged.**

### Root cause (dialogue)
- Client defers `setFlag` during talk (`DialogClientWorldDefer`); host must FlagSync after DialogOutcome.
- Host DialogOutcome runs under `ProcessInboundMessage`'s `NetworkApplyGuard`, so `FlagSync` Postfix and `DialogTreeSync.TryBroadcast` early-outed — same class of bug as the dream-door GE miss.
- Extra: client `DialogueButton.onPress` is a no-op until `boardFinished`, but our Postfix still sent `DialogOutcomeSync` → host advanced while client UI stayed put (duplicate `lookAtOven→lookAtBottle` spam).

### Fixed
- **FlagSync** (bool + int): fan out when `DialogHostApplyGuard.Active` even under inbound apply.
- **DialogTree** host flush after world-only apply: `TryBroadcastFromNpc(..., force: true)`.
- **DialogOutcome client send:** only after vanilla actually switched to the target node.
- **ClientBackup:** embed `ContentFingerprint`; refuse load/restore/push on fingerprint mismatch within the same campaign (rewound/old host save). Prefer newer local self over stale host push.

### Files
- `Patches/FlagSyncPatches.cs`, `DialogOutcomePatch.cs`
- `Sync/DialogTreeSync.cs`
- `Networking/LanNetworkManager.Handlers.cs`, `ClientStateBackup.cs`, `CoopWorldCopyMeta.cs`
- `PluginInfo.cs` / `AssemblyInfo.cs` (**0.7.26**)

## 0.7.25 — Dream prop collider parity (lamp solid / bell ghost) (2026-08-02)

Live soak: bunker `Lamp_dream_underground` still blocked the client (host walk-through); church bell was solid for host but walk-through for client. Host log showed `body-push Lamp_dream_underground` — client was streaming a **non-trigger** lamp via PhysicsState while host’s lamp is a **trigger** (skipped by scan). Same class as missed `GameEvent` `isColliderTrigger` mutations. **Protocol 19 unchanged** (optional msg **128** `DreamPropCollider`).

### Fixed
- **`IsSceneFixedLightItem`:** any `isLight` / `ItemLight` / dream `Lamp*` — never PhysicsState free-body; repair drops kinematic lock.
- **No dream pad ItemsDatabase spawns** from PhysicsState (duplicate wrong colliders).
- **`DreamPropCollider` (128):** host broadcasts Item `isTrigger` under the dream pad (after load, peer `DreamEntered`, and host GEs); client applies — lamp walk-through / bell solid parity.

### Files
- `Sync/WorldPhysicsSyncService.cs`, `DreamSyncManager.cs`
- `Networking/Messages/DreamMessages.cs`, `NetMessageType.cs`, `LanNetworkManager*.cs`
- `Patches/GameEventsFiredPatch.cs`
- `PluginInfo.cs` / `AssemblyInfo.cs` (**0.7.25**)

## 0.7.24 — Post-dream abyss + LocationExit NRE + dream GE flush (2026-08-02)

Log triage after 0.7.23: client still backed up pad coords after `DreamEnded`, `LocationExit` NRE during join, empty dream preset on end, and dream GEs (`def_glow`, `onEnterLocation_*`, karuzela/lamp) queued forever. **Protocol 19 unchanged.**

### Fixed
- **Remote dream entry `positionCopy`:** `LoadDreamSceneCoroutine` teleported onto the pad *before* `startDreaming()`, so vanilla `saveCurrentPlayerState()` overwrote overworld `positionCopy` with −75k. Restored after `startDreaming` (mirrors `timeCopy` fix); set `_localDreamPreset` on remote entry.
- **`endDreaming` safety snap:** if still on pad after end, teleport to `positionCopy` / pre-dream pose.
- **ClientBackup collect:** refuse pad coords even when dream flags already clear; fall back to pre-dream pose.
- **LocationExit:** defer while proxies cannot spawn; purge destroyed proxy dict entries before `GetComponent`.
- **Campaign backup migrate:** stamp empty `CampaignId` on legacy JSON → stop `file=(none)` skip.
- **DreamEnded preset:** `ResolveActivePresetName()` (local → session → Dreams.preset) for fan-out / complete logs.
- **Dream GE flush:** pad-coord + named FX (`def_glow`, `podmiana`, `karuzela`, `SWITCH_`, `dimLight`, …) wait for `finishedLoading`; flush uses soft Apply (not pre-find); max age 90s; load wait 15s.

### Dream event sync notes (audit)
- Full mutating beats are vanilla `GameEvents.fire()` — host fan-out already via `GameEventsFired`. Bunker carousel = `area_karuzela_*`; lamp/newborn chain = `SWITCH_*` / `podmiana_*` / lamp remove. Flush timing was the main client miss; portrait/newborn spawn still soak-test (parked if `trueTargets` fail on LoadDream path).

### Files
- `Sync/DreamSyncManager.cs`, `Patches/DreamSyncPatches.cs`
- `Networking/ClientStateBackup.cs`, `LanNetworkManager.Handlers.cs`
- `PluginInfo.cs` / `AssemblyInfo.cs` (**0.7.24**)

## 0.7.23 — Client backup no longer teleports into dream abyss (2026-08-02)

Rejoin restored `ClientStateBackup` position `(-74891, -80204)` — dream-pad coords from a mid-dream / exit snapshot — into the overworld (empty −75k space). Host had no campaign-keyed backup so local self fallback applied. **Protocol 19 unchanged.**

### Fixed
- **Collect:** while dreaming, store vanilla `Dreams.positionCopy` (overworld), never live pad pose.
- **Restore:** refuse pad-range coords (`|x|`/`|z|` ≥ 40k) when not currently dreaming; inv/skills still apply.

### Files
- `Networking/ClientStateBackup.cs`
- `PluginInfo.cs` / `AssemblyInfo.cs` (**0.7.23**)

## 0.7.22 — Client AI/AoE damage parity (normalHit) (2026-08-02)

0.7.21 forwarded `damagesAroundMe` to proxies, but client `HandleDamagePlayer` always applied `getHit` with **normalHit=true** (armor). Vanilla spirit/AoE uses **normalHit=false**, so the client still felt soft. **Protocol 19 unchanged** (optional `NormalHit`/`CanInterrupt` trailers on msg 9).

### Fixed
- **`DamagePlayerMessage`:** carries `NormalHit` + `CanInterrupt`; `ProxyDamagePatch` forwards caller flags (AoE keeps armor-bypass).
- Client apply uses those flags instead of hardcoding interrupt/armor-on.

### Files
- `Networking/Messages/PlayerMessages.cs`, `LanNetworkManager.Combat.cs`, `Patches/ProxyDamagePatch.cs` (+ other DamagePlayer construction sites)
- `PluginInfo.cs` / `AssemblyInfo.cs` (**0.7.22**)

## 0.7.21 — Dream door hitch + forest spirit / client melee damage (2026-08-02)

Dream bunker door open hitch (both peers). Dream forest spirit chased/damaged the wrong peer with hugely overscaled hits. **Protocol 19 unchanged.**

### Fixed
- **Client melee overdamage:** Host `MeleeSensor` hits on `RemotePlayerProxy` never destroyed the sensor (vanilla does after one hit) and had no per-proxy debounce — multi-collider proxies got `DamagePlayer` every FixedUpdate. Now consume sensor + 0.25s debounce + log `[ProxyMelee]`.
- **Forest spirit / AI aggro steal:** `HostCanSeeEnemy` no longer retargets mid-chase from host → closer client; `forceAttackClosestCharacter` only redirects when proxy is nearer than host; `attackPlayer` picks recent EventTriggers proxy or nearest player body.
- **Dream spirit spawn:** `special_spawnDreamForestSpirit` anchors near the peer who entered `area_forestSpirit_runaway_*` when that was a proxy.
- **`damagesAroundMe`:** AoE ticks can hit the chased proxy (via `CharBase.getHit` → `ProxyDamagePatch`), not only `Player.Instance`.
- **Door-open hitch:** Defer `enterAllNodes` one frame; cache `FindObjectsOfType<Door>`; stop host poll / client force-open once door is handled; fewer retries.

### Files
- `Patches/HostCombatPatches.cs`, `HostAIPatches.cs`, `HostDamageAroundMePatch.cs`, `DreamForestSpiritSpawnPatch.cs`, `ThreatTriggerContext.cs`, `EventTriggersProxyPatches.cs`, `DreamDoorSyncPatches.cs`, `ModRuntime.cs`
- `PluginInfo.cs` / `AssemblyInfo.cs` (**0.7.21**)

## 0.7.20 — Client backups keyed to campaign / save (2026-08-02)

Backups were last-write-wins per profile+playerId with no world attachment — wrong campaign could restore. **Protocol 19 unchanged** (optional `CampaignId` trailer on `WorldSaveBegin`).

### Changed
- **Stable `CampaignId`** in `dwmp_coop_meta.json` (GUID; survives Save fingerprint churn; reminted on brand-new worldgen).
- Host sends `CampaignId` in world-share Begin; client stores it on the permanent copy.
- Backup files: `client_backup_p{id}_{campaignId}.json` / `client_backup_self_{campaignId}.json`; JSON embeds `CampaignId`.
- Load/restore refused on campaign mismatch (legacy unscoped files only apply when current meta also has no id).

### Files
- `Networking/CoopWorldCopyMeta.cs`, `ClientStateBackup.cs`, `WorldSaveShareService.cs`, `Messages/WorldSaveShareMessages.cs`
- `Patches/WorldGenSharePatch.cs`
- `PluginInfo.cs` / `AssemblyInfo.cs` (**0.7.20**)

## 0.7.19 — Client backup restores exit position (2026-08-02)

PosX/Y/Z were already written into `ClientStateBackup` JSON on Save but never applied. Quit without Save left a stale exit spot. **Protocol 19 unchanged.**

### Fixed / Changed
- **Restore position** on backup apply (`teleportTo` + WorldGrid refresh + proxy teleport).
- **Exit snapshot:** client `StopNetwork` while in-world writes local self (+ host push if still linked) so disconnect captures current pos/inv.

### Note
Backups live under the **active profile folder** (`…/1_4Save/profN/`), last-write-wins per player id — not fingerprinted to a specific world save package.

### Files
- `Networking/ClientStateBackup.cs`, `LanNetworkManager.cs`, `LanNetworkManager.Handlers.cs`
- `PluginInfo.cs` / `AssemblyInfo.cs` (**0.7.19**)

## 0.7.18 — Client inv/skills survive multi-session rejoins (2026-08-02)

Same campaign across days: client loaded host `sav.dat` (host character) and never auto-restored. Backups were stored on host (`client_backup_p{id}.json`) but not pushed back; local `client_backup_self.json` was only written on ManualSave. **Protocol 19 unchanged** (reuse msg 45 both ways).

### Fixed
- **Client Save → local self backup:** every `SendClientStateBackup` also writes `client_backup_self.json`.
- **Host push on late-join bulk:** `SendStoredClientBackupTo` after settle; client applies + mirrors to local self.
- **Local self fallback:** if no host file within ~12s after phase-3 reconnect, restore local self once.

### Files
- `Networking/LanNetworkManager.Handlers.cs`, `LanNetworkManager.cs`, `Messages/NetMessageType.cs`
- `docs/COOP_COVERAGE.md` (2.10 deferred cleared)
- `PluginInfo.cs` / `AssemblyInfo.cs` (**0.7.18**)

## 0.7.17 — Dead mid-dream: no success rewards (2026-08-02)

Spectating peers who died mid-dream still saw the shared exit video (correct) but then ran the **success** outcome in `endDreaming`, granting createInvItem / journal rewards they should not get. Pre-dream inventory restore is unchanged. **Protocol 19 unchanged.**

### Fixed
- **Dead-in-dream success loot:** At story `endDreaming`, if `FinalDreamsceneManager.IsLocalDead`, swap `outcomePreset` to `playerDeath` (or none) before grants — inventory/hotbar restore still runs. Hard-cleanup personal `ApplyOutcomeEffects` also skips when local is dead.

### Files
- `Patches/DreamSyncPatches.cs`, `Sync/DreamSyncManager.cs`
- `PluginInfo.cs` / `AssemblyInfo.cs` (**0.7.17**)

## 0.7.16 — Far peer footstep audio hitch (2026-08-02)

Client heard periodic audio hitches while host walked outside hearing range. 0.7.14 exempted all `RemotePlayerProxy`-parented plays from distance cull so edge fade was smooth — but every far footstep still called `AudioController.Play` (silent Linear rolloff), which can duck/allocate voices on a cadence. Host proxy enter of `area_footsteps_*` also fired surface GEs uselessly. **Protocol 19 unchanged.**

### Fixed
- **Periodic out-of-range peer audio hitch:** Skip `PlayProxyFootstepSound` when peer is beyond hear range; proxy AudioSuppression exempt only while near listener.
- **Proxy `area_footsteps` / `soundarea` triggers:** Do not fire those EventTriggers for remote proxies (local body + proxy `checkGround` own surfaces).

### Files
- `Networking/LanNetworkManager.Handlers.cs`, `Patches/AudioSuppressionPatch.cs`, `Patches/EventTriggersProxyPatches.cs`
- `PluginInfo.cs` / `AssemblyInfo.cs` (**0.7.16**)

## 0.7.15 — Client dialogue door + flashlight click tail (2026-08-02)

Client opened the dream bunker door (dialogue) → host saw it open, client stayed closed. Host logged `[GameEventsSync] fired` for `onLeaveDoorDialogue_dream_underground` but never sent the packet: `SendGameEventsFired` early-returned on `IsApplyingRemoteState` while handling inbound `DialogNpcLock` Release (host-talk path was fine). Also host flashlight on/off click on client sounded truncated near the end (Linear rolloff + short AudioItem maxDistance). **Protocol 19 unchanged.**

### Fixed
- **Client-opened dream door stuck closed locally:** Allow `SendGameEventsFired` / `SendDoorState` / lock-unlock fan-out when `DialogHostApplyGuard` is active (same exception as DoorOpen postfix).
- **Remote flashlight click cuts off near the end:** Spatial tool SFX use Logarithmic rolloff and full `DefaultMaxSpatialDistance` instead of Linear + short item range.

### Files
- `Networking/LanNetworkManager.Handlers.cs`
- `PluginInfo.cs` / `AssemblyInfo.cs` (**0.7.15**)

## 0.7.14 — Shared dream exit video + lamp walk + audio range hitch (2026-08-01)

0.7.13 dream pad playthrough worked until host bed-exit: client hit `NullReferenceException: routine is null` on skipped `endDream` GameEvent (Harmony IEnumerator Prefix), then hard `ApplyRemoteDreamCleanup` with no exit video — game broken while host returned cleanly. Also client walk-blocked on `Lamp_dream_underground` (PhysicsState kinematic lock) and a one-step footstep hitch when host left hearing range (hard 650f AudioSuppression vs spatial rolloff). **Protocol 19 unchanged.**

### Fixed
- **Client broken on host dream finish:** Skip `endDream`/`startDream` GE with an empty coroutine (no `StartCoroutine(null)`); host broadcasts `DreamEnded` at `initiateEndDreaming` so peers play the same outcome transition video; client host-ordered story end runs vanilla `initiateEndDreaming` instead of hard cleanup.
- **Dream lamp blocks client walk:** Exclude non-draggable light Items from PhysicsState scan/apply (LightState owns them); unstick kinematic if already locked.
- **Out-of-range peer footstep hitch:** Do not hard-cull `AudioController.Play` parented under `RemotePlayerProxy` — let spatial `maxDistance` roll off.

### Parked
- **Lamp plot swap → newborn / intense calling:** Client got LightState on/off; full GE spawn/portrait parity for that beat not verified this pass — recheck after exit soak.

### Files
- `Patches/GameEventDreamAuthorityPatch.cs`, `Patches/DreamSyncPatches.cs`, `Sync/DreamSyncManager.cs`
- `Sync/WorldPhysicsSyncService.cs`, `Patches/AudioSuppressionPatch.cs`
- `PluginInfo.cs` / `AssemblyInfo.cs` (**0.7.14**)

## 0.7.13 — Dream pad missing props + door open delay (2026-08-01)

Door opened on both ends in 0.7.12, but after a long pause, and the client was missing some props in the room behind the door (host complete). Root cause: remote `LoadDreamScene` never set `OutsideLocations.loading`, so `CullableObject` registered onto the **World** grid at −75k and `WorldGrid.refresh` hid far nodes — host `prepareDream` sets `loading=true` and skips that. Door delay was `onCloseDialogue` waiting for lookKeyhole world-only board drain. **Protocol 19 unchanged.**

### Fixed
- **Client missing dream-pad objects behind dialogue door:** Set `OutsideLocations.loading` + `dreamPrepared` during remote pad spawn (mirror vanilla); `enterAllNodes` after dream `setGrid`; remap dream-pad `UniqueObject`s into `UniqueObjects` (overworld first-wins clone trap); PhysicsState interest exempt while dreaming; leave-door apply also `enterAllNodes`.
- **Long pause before door opens:** Abort world-only dialogue drain on `DialogNpcLock` Release so leave-door GE fires immediately.

### Files
- `Sync/DreamSyncManager.cs`, `Patches/UniqueObjectsDreamPatch.cs`, `Patches/DreamDoorSyncPatches.cs`
- `Networking/LanNetworkManager.Handlers.cs`, `Sync/WorldPhysicsSyncService.cs`
- `PluginInfo.cs` / `AssemblyInfo.cs` (**0.7.13**)

## 0.7.12 — Dream door force-open + client scrape echo (2026-08-01)

0.7.11 did replay `onCloseDialogue` (host log confirms) but still no leave-door GE / `[DoorSync]`: close arrived mid-`lookKeyhole` drain, `FindNpc` could hit the overworld bunker twin, and EventTrigger requirements often blocked the GE. Client push/drag also doubled scrape (native + MOS from host PhysicsState echo). **Protocol 19 unchanged.**

### Fixed
- **Dialogue door still shut after talk:** Defer `onCloseDialogue` until world-only drain finishes; prefer dream-pad `door_underground`; also force-fire `onLeaveDoor*` / `DoorDialogue` GameEvents under the pad and `HostEnsureDialogueDoorOpen` (unlock + open + DoorOpen fan-out).
- **Client double scrape on push/drag:** Longer local push authority; host PhysicsState echo never arms MOS while client recently owned the free-body; refresh authority while E-dragging.

### Files
- `Networking/LanNetworkManager.Handlers.cs`, `Patches/DreamDoorSyncPatches.cs`
- `Sync/WorldPhysicsSyncService.cs`, `Audio/ItemMovingSoundHelper.cs`, `Networking/LanNetworkManager.cs`
- `PluginInfo.cs` / `AssemblyInfo.cs` (**0.7.12**)

## 0.7.11 — Dream door onCloseDialogue + client entry Saving (2026-08-01)


Client-led `door_underground` talk still never opened the armored door (no `[DoorSync]` / no leave-door GE). Root cause: door opens via `onCloseDialogue` → `onLeaveDoorDialogue_*` GameEvents; speaker close runs that only on the client, where one-shot `GameEvents.fire` is blocked, and host world-only silent-close intentionally skipped the trigger. Also aborted `lookKeyhole_dream` multi-board (`changePortrait`) before later boards ran. Client dream entry skipped the Saving indicator because SaveSync is suppressed for the whole dream window. **Protocol 19 unchanged.**

### Fixed
- **Dialogue door never opens (host or client):** On client `DialogNpcLock` Release, host replays `Core.sendTriggerInfo(onCloseDialogue)` under `DialogHostApplyGuard` and polls dream doors. Multi-board / `changePortrait` DialogOutcome applies drain boards before silent close instead of tearing down immediately.
- **Client missing Saving on dream entry:** Peer remote-dream path runs a local `Save(..., showSavingIndicator: true)` (SaveSync fan-out still suppressed during dream to avoid video hitch).

### Files
- `Networking/LanNetworkManager.Handlers.cs` (`HostFireNpcCloseDialogue`, `HostDrainWorldOnlyDialogue`)
- `Sync/DreamSyncManager.cs`
- `PluginInfo.cs` / `AssemblyInfo.cs` (**0.7.11**)

## 0.7.10 — Dream dialogue door + host keyhole black (2026-08-01)


Client-led `door_underground` talk: host went permanently black on `lookKeyhole_dream` (changePortrait fade), and dialogue door opens never reached peers — no `[DoorSync]` at all. Fallout from 0.7.8’s working `NetworkApplyGuard`: DialogOutcome apply sits under inbound apply-flag, which blocked DoorOpen / GameEventsFired fan-out; world-only `displayDialogue` still ran speaker presentation on the host. **Protocol 19 unchanged.**

### Fixed
- **Host black on keyhole / scene-shift dialogue:** Suppress fade-to-black `tweenBlackScreen*` while `DialogHostApplyGuard` is active; silent close clears forbidInputs + black layers; block stale `displayNextBoard` after `currentDialogue` was nulled.
- **Dialogue door not opening for peers (or syncing):** Allow DoorOpen/unlock/unblock + GameEventsFired broadcast when `DialogHostApplyGuard.Active` even under inbound apply; poll dream doors after every DialogOutcome world apply.

### Files
- `Patches/DialogHostPresentationSuppressPatches.cs` (new), `Patches/DialogHostSilentClosePatch.cs`
- `Patches/DreamDoorSyncPatches.cs`, `Patches/GameEventsFiredPatch.cs`
- `Networking/LanNetworkManager.Handlers.cs`
- `PluginInfo.cs` / `AssemblyInfo.cs` (**0.7.10**)

## 0.7.9 — Host dream entry video after NetworkApplyGuard fix (2026-08-01)

0.7.8 made `NetworkApplyGuard` actually work on every inbound packet. That exposed a latent bug: `HandleCutsceneSync` early-out on `IsApplyingRemoteState` for `ActionDreamEntryTransition` / `ActionSkipTransition`, so the host never played the peer-led entry video (client log showed transition begin → peers; host only got `CutsceneSync:1` in perf and jumped straight into the dream). **Protocol 19 unchanged.**

### Fixed
- **Peer-led dream entry video on host:** Remove the inbound-handler `IsApplyingRemoteState` gates on dream entry / skip CutsceneSync actions. Outer `ProcessInboundMessage` guard already suppresses rebroadcast via Harmony Prefixes.

### Files
- `Patches/CutsceneSyncPatches.cs`
- `PluginInfo.cs` / `AssemblyInfo.cs` (**0.7.9**)

## 0.7.8 — Dream client GE apply (NetworkApplyGuard) (2026-08-01)

Client entered `dream_home` with wrong clothes and could not see (room masks / START events dead). Logs: endless `[GameEventsSync] fire no-op` for `player_changeClothes_dreamHome`, `ROOM_mask_*`, `START_dreamHome`, `events_start_dreamHome` — never `applied`. Host fired the same GEs fine. **Protocol 19 unchanged.**

### Fixed
- **NetworkApplyGuard was a no-op:** `struct` + `using (new NetworkApplyGuard())` compiled to `initobj` (zero-init) under net471/C#10 — constructor never ran, `_entered` stayed false, `IsApplyingRemoteState` never set. `GameEventsFiredPatch` kept blocking client one-shots. Guard is now a **`sealed class`** so `new` always runs the ctor. Also: `IsApplyingRemoteState` ORs `NetworkApplyGuard.IsActive` (same pattern as `TraverseHack`), and the GE Prefix checks `IsActive` directly.

### Files
- `Networking/NetworkApplyGuard.cs`, `Networking/LanNetworkManager.cs`, `Patches/GameEventsFiredPatch.cs`
- `PluginInfo.cs` / `AssemblyInfo.cs` (**0.7.8**)
- `DarkwoodMP.PathB.Tests/AuditStructureTests.cs` (class-not-struct regression)

## 0.7.7 — Dream door fire guard + scrape/footsteps/transition (2026-07-28)

(Was briefly labeled 0.9.7.) Prior dream-enter flush fixed *when* to apply `onEnterLocation_dream_*`, but pending flush still called `GameEvents.fire()` **outside** `NetworkApplyGuard`. `GameEventsFiredPatch` silently blocked client one-shots → log said "applied" while `podmiana_1_dream_undeground` children never ran, so `door_underground` kept `welcome_opening` instead of `door_underground_act1` / `welcome_opening_dream`. Also fixes client scrape echo, dream peer footsteps sounding local, and the remaining entry black gap. **Protocol 19 unchanged.**

### Fixed
- **Dream bunker door dialogue (real root cause):** Wrap every `ApplyGameEventsFired` fire in `NetworkApplyGuard`; only dequeue pending GEs when fire actually succeeds (`firedNow=true`).
- **Client double push/drag scrape:** Host PhysicsState / DragSync echo armed MOS while native `ItemSounds` already played — local-owner via `_dragClaims` + push authority on client free-body send; ignore own DragSync echo.
- **Dream peer footsteps as own / hard cutoff:** Force `spatialBlend=1` + linear rolloff on proxy footsteps; drop hard `IsNearListener` cull; skip all dream `*footsteps*` / `soundarea` GEs on client (proxy path owns peer steps).
- **Client entry transition gap:** Opaque black (both layers) **before** `unpause`; snap video off (no 0.5s DOFade hole); keep `EnteringDream` until dream-load fade-in.

### Changed
- **Product version line:** `0.9.x` → **`0.7.x`** (0.9 was overstated; see Versioning above).

### Files
- `Networking/LanNetworkManager.Handlers.cs`, `Audio/ItemMovingSoundHelper.cs`, `Sync/WorldPhysicsSyncService.cs`
- `Patches/DreamEntryClientPatch.cs`, `Sync/DreamSyncManager.cs`
- `PluginInfo.cs` / `AssemblyInfo.cs` (**0.7.7**)

## 0.9.6 — Dream enter GE timing + black hold (2026-07-28)

0.9.5 queued the right pad GE but applied it **before** teleport/`startDreaming`/`finishedLoading`, so `door_underground_act1` stayed on `welcome_opening` (normal bunk) instead of `welcome_opening_dream`. Intercept also tore down the video with no black hold → overworld flash. **Protocol 19 unchanged.**

### Fixed
- **Dream door dialogue:** Flush `onEnterLocation_dream_*` only after `startDreaming` + `location.finishedLoading`; gate apply/pending flush until then.
- **Client overworld flash after entry video:** Stop entry audio only; hold opaque `blackScreen` + `EnteringDream` until remote load fades in.

### Files
- `Sync/DreamSyncManager.cs`, `Networking/LanNetworkManager.Handlers.cs`, `Patches/DreamEntryClientPatch.cs`
- `PluginInfo.cs` (0.9.6)

## 0.9.5 — Dream pad GE / black screen / entry audio (2026-07-28)

Root-cause fixes from 0.9.4 dual-box soak. **Protocol 19 unchanged.**

### Fixed
- **Wrong bunker door dialogue (real cause):** Client one-shot `GameEvents.fire` is blocked; host `onEnterLocation_dream_underground` arrives before the dream pad exists. Soft name fallback had **no distance cap** and picked the overworld bunker's homonym at `(-6342,…)` instead of the pad at `(-75000,…)`, wiring `door_underground` with the normal bunk dialogue. Now: queue dream GEs until `dreamLocation` exists, cap soft fallback, prefer pad instances, flush after pad `enter()`.
- **Host permanent black screen (client-led dream):** Peer `StartRemoteDreamTransition` sets base `UI.blackScreen` opaque; host then `startDreaming` only clears `blackScreenTop`. `LocalEntryFadeoutCoroutine` now calls `FadeInDreamBlackScreen`.
- **Client loud entry audio:** Intercepting `onFinishedVideo` skipped vanilla `onLoaded`, so entry stingers never `AudioController.Stop` and stacked under dream music. Stop stingers + tear down video on intercept.
- **Host-proximate dream ambients:** Skip far `*footsteps*` / soundarea GEs on client so host walking into volumes does not blast the client at the host's feet.

### Files
- `Networking/LanNetworkManager.Handlers.cs`, `Sync/DreamSyncManager.cs`
- `Patches/DreamEntryClientPatch.cs`, `Patches/DreamDoorSyncPatches.cs`
- `PluginInfo.cs` (0.9.5)

## 0.9.4 — Dream sync playtest harden (2026-07-28)

Fixes from dual-box dream soak logs + two pinpointed bugs (wrong bunker door, doubled entry sound). **Protocol 19 unchanged.**

### Fixed
- **SessionId desync:** Client `BeginFromHost` adopts host SessionId (no local mint via `TryBegin`+`Adopt`).
- **Stale random preset:** `prepareDream("")` no longer `TryBegin`s from leftover `Dreams.preset`; host roll calls `UpdateActivePreset` so session/bulk match the real dream.
- **Client exit clock:** Remote `startDreaming` was overwriting `timeCopy` with dream TimeSync (900); restore freeze snapshot on exit.
- **LocationEnter flood:** Dreams send LocationEnter once on enter/rename only (~180/dream → 1).
- **Wrong bunker door:** Host no longer fans out overworld opened doors mid-dream; client force-open targets only the dialogue-NPC door; `HandleDoorOpen` drops non-dream poses / distant same-name matches.
- **Doubled/prolonged entry sound:** Mute video track when Audio stinger plays; single stinger; `WaitForSecondsRealtime`; stop audio on fade; block double `StartRemoteDreamTransition`; no DreamAudio forward during `EnteringDream`.
- **Post-end dream GEs:** Host and client drop `*dream_*` GameEvents after session teardown (e.g. `fov_trigger_*`).
- **Already-fired GE reapply:** Client skips when `fired && !multipleFire`.
- **DreamAudio aimReturn:** Stop forwarding unresolved `aimReturn*` clips.
- **Follow-up (explore map):** `DreamSessionBulk` carries `SessionId`; chain adopts SessionId; flush pending dream GEs on end; item-action sounds excluded from DreamAudio.

### Files
- `Sync/DreamSession.cs`, `Sync/DreamSyncManager.cs`, `Sync/FinalDreamsceneManager.cs`
- `Patches/DreamSyncPatches.cs`, `DreamDoorSyncPatches.cs`, `DreamAudioPatches.cs`, `GameEventsFiredPatch.cs`, `CutsceneSync` path via DreamSyncManager
- `Networking/LanNetworkManager.cs`, `LanNetworkManager.DreamHandlers.cs`, `LanNetworkManager.Handlers.cs`, `Messages/DreamMessages.cs`
- `PluginInfo.cs` (0.9.4)

## 0.9.3 — Dream sync full harden (2026-07-28)

Pre-1.0 ship mood for dream sync. **Protocol 19 unchanged** (same DLL both boxes). Nothing deferred from the 2026-07-28 dream review.

### Fixed
- **C1 GE dual-fire:** Client `GameEvent.fire` skips `startDream`/`endDream` under `NetworkApplyGuard`; host Dream* messages stay authority; other GE effects still apply.
- **C2 solo death stuck:** Empty peer set allows vanilla `initiateEndDreaming`; spectate Prefix never blocks then no-ops.
- **C3 chain inventory wipe:** Remote `ProcessChainCoroutine` sets `switchingDream` before `startDreaming` (preserves inventory/time copies).
- **C4 story-end reject recovery:** Host nacks with `DreamEnded` outcome `rejected:*`; client 15s defer watchdog force-cleans if no accept/nack.
- **H1/H2 chain authority:** Single `DreamChainStart` from `DreamPrepareChainPatch`; `wantToSwitchDream` tracks next pocket; client validates `SessionId`.
- **H3 all-dead transition:** `EndDreamForBoth` uses `initiateEndDreaming(playerDeath)` + `AllowDeathEndPass` (no hard-cut `endDreaming`).
- **H4 completions policy:** `_completedPresets` drives `MirrorPoolRemove` only — named dreams may re-enter like SP.
- **H5 epilog 1a:** Remote load destroys `outside_roadToHome_01` + `forceSaveStatic` save.
- **H6 getPreset null:** Client `prepareDream("")` aborted without host pick; `getPreset` no longer returns null into live prepare.
- **Peer drop mid-dream:** Last remote gone ends shared session (living or dead local).
- **Mid-dream HOST GRANT:** Migration refused while dream active; cleanup + disconnect (no half-session promote).
- **Stale SessionId:** Drop mismatched `DreamStarted` / `DreamEnded` / `DreamChainStart` when a newer session is active.

### Parked (dream)
- None.

### Files
- `Patches/GameEventDreamAuthorityPatch.cs`, `Patches/DreamSyncPatches.cs`
- `Sync/DreamSyncManager.cs`, `Sync/DreamSession.cs`, `Sync/FinalDreamsceneManager.cs`
- `Networking/LanNetworkManager.DreamHandlers.cs`, `Networking/HostMigration.cs`
- `PluginInfo.cs` (0.9.3), PathB audit tests, `docs/DREAM_SYNC_REVIEW_2026-07-28.md`, `docs/PLAYTEST.md`, `docs/COOP_COVERAGE.md`

## 0.9.2+ — Deep-review SaveSync / workbench / world-share block (2026-07-28)

Static audit follow-up (protocol 19 unchanged). Host-authoritative save fan-out, workbench level, join hard-block on share failure.

### Playtest (unsigned)
- Checklist **docs/PLAYTEST.md** §5 (night alive-leaver, dream exit, trade 2p, SceneLoad, SaveSync storm, workbench, shotgun FF).

### Parked (still open)
- Handlers.cs split, Ironbark delete, mid-dream host migration, FlagSync allowlist, dual/triple campaign soak — see **docs/TODO.md**.

### Fixed
- **SaveSync hitch storms:** Clients request host fan-out only; host debounces (3s) then broadcasts. Clients apply only host-originated SaveSync. `_isRemoteSaveInProgress` loop guard kept.
- **Workbench level authority:** Host-only apply + `WorkbenchLevelSync` broadcast; clients mirror via sync handler only.
- **Workbench host upgrade (R4):** `WorkbenchUpgradePatch` on host now calls `SendWorkbenchLevelSync()` instead of `Broadcast(WorkbenchLevel)` (host never receives its own broadcast; clients ignore `WorkbenchLevel`).
- **World share fail-loud:** `WorldSharePolicy.IsShareFailureMessage`; ENTER WORLD blocked on terminal failure; client worldgen blocked when share failed on title.
- **Steam disconnect night parity (R5):** `OnSteamPeerDisconnected` gates `TryResolveNightMorning` on `OnRemoteDisconnected` return (matches LAN path).
- **DreamEnded snapshot trust (R5):** Host story-end path applies `DreamSession.ApplySnapshot` only after session/preset/handshake validation; rejected packets no longer merge `CompletedPresets`/`LvlFlags`.

### Files
- `Patches/SaveSyncPatches.cs`, `Patches/WorldGenSharePatch.cs`, `Patches/JournalSyncPatches.cs`
- `Networking/LanNetworkManager.Handlers.cs`, `LanNetworkManager.cs`, `LanNetworkManager.Steam.cs`, `LanNetworkManager.DreamHandlers.cs`
- `Networking/WorldSaveShareService.cs`, `CoopPolicy.cs`
- `UI/MainMenuMultiplayerInject.cs`, `UI/JoinWorldSlotPicker.cs`
- `PathB.Tests/CoopPolicyTests.cs`

## 0.9.2+ — Deep-review authority + night disconnect (2026-07-28)

Static audit follow-up (protocol 19 unchanged). Trusted-LAN anti-grief + night death semantics.

### Fixed
- **SceneLoad / credits grief:** Host ignores inbound `SceneLoad`; clients apply only from host. Peer-originated packets are not forwarded.
- **NightDeathState grief:** Host ignores peer `AllDeadTrigger`; clients apply only from host.
- **DreamEnded story end:** Host runs `initiateEndDreaming` only for living handshaked dream participants with matching session/preset.
- **Alive partner rage-quit morning:** Disconnect no longer treats remotes==0 as all-dead when the leaver was alive; `NightDeathPolicy.ShouldResolveMorningOnDisconnect` gates `skipDay`.

### Files
- `Patches/EpilogueSyncPatches.cs`, `Networking/LanNetworkManager.Combat.cs`, `DreamHandlers.cs`, `LanNetworkManager.cs`
- `CoopPolicy.cs`, `DeathStateTracker.cs`, `PathB.Tests/CoopPolicyTests.cs`

### Parked (same audit)
- Handlers.cs split, Ironbark delete, mid-dream host migration rehydrate, FlagSync allowlist, campaign soak (human).

## 0.9.2+ — Deep-review dream teardown + entry cull (2026-07-28)

### Fixed
- **Dream exit clock:** `UnfreezeWorld(restoreTime: false)` after applying `timeCopy` so remote cleanup no longer snaps to pre-dream freeze time.
- **Stuck spectator after dream end:** `ApplyRemoteDreamCleanup` exits spectate without position restore.
- **OnDreamEnded order:** deferred until after cleanup succeeds.
- **Pre-pad entity phantoms:** reject entity spawns while dream active and pad transform null.
- **Client getPreset race:** no local random roll without host PendingHostPreset.
- **Host prepareDream:** abort when TryBegin fails (except duplicate Starting same preset).

### Files
- `Sync/DreamSyncManager.cs`, `Networking/ClientEntityInterpolationService.cs`, `Patches/DreamSyncPatches.cs`

## 0.9.2+ — Deep-review trade auth + combat fail-closed (2026-07-28)

### Fixed
- **Trade stock grief:** Client sends inventory to host only; host sole broadcaster; suppress Forwardable echo of client payloads; apply client snapshot only with NPC dialog lock then rebroadcast host-built stock.
- **Client damage redirect fail-open:** catch now returns false (no local getHit).
- **FF double-relay:** shared `ProxyCombatRelay` per-frame debounce across ProxyDamage / hitscan / collision.
- **Host melee FF-off:** explicit return false on proxy when FF disabled.

### Files
- `Patches/TradeSyncPatches.cs`, `Networking/LanNetworkManager.Handlers.cs` (trade)
- `Patches/ClientHitscanDamageRedirectPatch.cs`, `ProxyDamagePatch.cs`, `HitscanImpactSyncPatch.cs`, `HostCombatPatches.cs`
- `Players/RemotePlayerProxy.cs`, `Players/ProxyCombatRelay.cs`

## 0.9.2+ — Deep-review identity, SaveSync, worldgen, loot (2026-07-28)

### Fixed
- **PlayerId spoof:** Host LocationEnter/Exit ignore payload PlayerId; use wire sender. Chat SenderId overwritten from wire on host fan-out. PlayerState rebroadcast stamps sender id.
- **SaveSync storms:** Client requests only; host 3s debounce then SendToAll; clients apply host-originated only.
- **Workbench level:** Host-only apply + WorkbenchLevelSync mirror on clients.
- **World share fail:** Terminal share failure hard-blocks ENTER WORLD / slot pick / client worldgen finish.
- **Loot bonus race:** ItemDoublePickup pending share keyed per InvSlot.

### Files
- `LanNetworkManager.Handlers.cs`, `LanNetworkManager.cs`, `SaveSyncPatches.cs`, `WorldGenSharePatch.cs`, `WorldSaveShareService.cs`, `MainMenuMultiplayerInject.cs`, `JoinWorldSlotPicker.cs`, `ItemDoublePickupPatch.cs`, `CoopPolicy.cs`

## 0.9.2+ — Dream dialogue door + flashlight spatial (2026-07-19)

Bunker dual-box: host dialogue door open left client blocked; flashlight click
was 2D-local with no reverb.

### Fixed
- **Dialogue door not open / room blocked on client:** Host never logged
  `Door.open` after `onLeaveDoorDialogue` (open runs in delayed GameEvent
  coroutines; targets often miss on client dream load). Now: explicit
  `Door.open` patch + DoorState dual-path; host polls opened doors 0–3.5s after
  dialogue-door GameEvents and re-broadcasts; client force unlock/unblock/open
  near `door_underground` after apply; unlock/unblock also sync; DoorOpen
  search widened in dreams.
- **Flashlight click from host:** Was Prefer2d parentless (local ears, no
  bunker reverb). Flashlight/torch activate/deactivate now spatial at remote
  proxy; keep AudioController reverb/lowpass; equip get/hide stay 2D.

### Files
- `Patches/DreamDoorSyncPatches.cs`, `GameEventsFiredPatch.cs`
- `Networking/LanNetworkManager.Handlers.cs`
- `Audio/LocalAudioService.cs`
- `Sync/DoorSyncPatches.cs`, `ModRuntime.cs`

## 0.9.2+ — Drag claim stuck + client double scrape (2026-07-19)

Client push/drag: 2× scrape; host could not re-grab after client released lamp.

### Fixed
- **Host blocked after client drag:** Late Unreliable `DragSync` IsDragging=true after
  reliable STOP re-claimed `_dragClaims` / `_remoteDragItemNames` (host log: STOP then
  still DragSync:15). Ignore IsDragging for 1s after stop; always clear claim on stop;
  `ReleaseClientPushHoldByName` drops host kinematic hold so free body is grabable.
- **Client double scrape (push/drag):** MOS could arm on the local pusher while native
  ItemSounds also ran. `NoteMoving`/`EnsurePlaying` refuse if local owner; SoftStop no
  longer kills native AO for local owner; startDragging clears residual MOS.

### Files
- `Networking/LanNetworkManager.cs`, `LanNetworkManager.Handlers.cs`
- `Sync/WorldPhysicsSyncService.cs`
- `Audio/MovingObjectSoundService.cs`, `ItemMovingSoundHelper.cs`
- `Patches/DragClaimPatch.cs`

## 0.9.2+ — Unity 2021.3.30f1 fact + inactive FOOT (2026-07-19)

Pinned engine version from Steam install; no architecture change required.

### Changed
- **AGENTS.md:** Darkwood = **Unity 2021.3.30f1** (`b4360d7cdac4`); `net471` +
  `FindObjectsOfType(true)` are correct — not Unity 5.
- **Dialog tree / door lookups:** DialogTreeSync + recent Handlers FOOT use
  `includeInactive: true` (dialogue door NPCs deactivate after talk). Dropped
  needless try/catch around that API.

## 0.9.2+ — Dream pickup ghost, dialogue door, fade parity (2026-07-19)

Dual-box bunker: shiny stone, dialogue door, client fade polish.

### Fixed
- **Pickup half-sync (shiny stone):** Peer emptied `itemInv` slot so take failed,
  but mesh stayed. `DestroyObjectByPos` only matched harvest keywords / `GameObject.Find`
  (miss on "Shiny stone" / `shiny_rock`). Now: nearest Item/itemInv by name or
  invItem.type; destroy empty itemInv after Container RemoveItem.
- **Dialogue door not open on peer:** Host `onLeaveDoorDialogue` GameEvents +
  `Door.open` did not reliably reach client. DoorState now also sent during dreams;
  DoorOpen always broadcasts; wider GameEvents name search + apply log; FindNpc
  includes inactive + dialogue name (host DialogOutcome was "NPC not found").
- **Client fade-in polish:** Match vanilla startDreaming — 1 frame wait, 0.5s
  blackScreenTop (and base blackScreen if still opaque).

### Files
- `Sync/WorldPhysicsSyncService.cs`, `DoorSyncPatches.cs`, `DreamSyncManager.cs`
- `Patches/DreamDoorSyncPatches.cs`
- `Networking/LanNetworkManager.Handlers.cs`

## 0.9.2+ — Dream bunker: host invisible, fade, loud audio, bright lights (2026-07-19)

`dream_bunker_underground_01` dual-box. Client log: host proxy placed 47× at
`Y=-12857` while pad is Y≈0; `createLocation` raced `LoadDreamScene` (2× pad).

### Fixed
- **Client cannot see host:** Proxy FixedUpdate locked Y to first bad place;
  LocationEnter ~1 Hz re-snapped to playerSpawn using 3D distance (Y mismatch
  forced spawn). Now: network Y applied, XZ-only in-location test, first-enter
  place only in dreams, `ResyncDreamProxiesAfterLocalLoad` after pad ready.
- **No client fade-in:** Remote path left black screen; `FadeInDreamBlackScreen`
  after load + hold black through video teardown.
- **Loud ambient/SFX:** Skip `createLocation` for dream_* (LoadDream owns pad) —
  double bunker = 2× SoundAreas/music.
- **Bright bunker lights:** Force `preset.time` + `updateAmbientLight` after load;
  re-apply ambient on dream TimeSync; light interest cull disabled while dreaming
  so pad lamps apply; flush pending lights after load.
- **Camera cleanup NRE:** Guard CamMain null on dream exit.

### Files
- `Players/RemotePlayerProxy.cs`
- `Networking/LanNetworkManager.Handlers.cs`
- `Sync/DreamSyncManager.cs`, `WorldPhysicsSyncService.cs`

## 0.9.2+ — Log-audit fixes: dream entry noise + light pending (2026-07-19)

Post dual-box log review of `dream_grave_meadow` session.

### Fixed
- **DreamAudio `Get_01`:** Host no longer forwards equip get/hide Prefer2d one-shots
  (was "Could not resolve clip for: Get_01" on client).
- **TimeSync log garble:** ASCII `->` instead of Unicode arrows so day/time no longer
  glue into `day 11` / `time 417800`; dream-time jumps tagged `[TimeSync/dream]`.
- **False LocationExit on dream settle:** Vanilla dreamPrepared transport never sets
  `playerInOutsideLocation`; settle now forces flag + name; PlayerState path holds
  dream pad instead of broadcasting LocationExit.
- **SaveSync mid dream entry:** Local prepareDream Save kept; peer SaveSync fanout
  suppressed while EnteringDream / dreamPrepared / dreaming / wantToDream / switching.
- **PlayerScare spam:** Client aimScare only while actively aiming + 1.25s rate limit
  (vanilla loops every 1s with aimFinished sticky).
- **Light RX drop proxy=null:** Queue PlayerLightState until proxy create, then apply.

### Files
- `Patches/DreamAudioPatches.cs`, `SaveSyncPatches.cs`, `ClientSoundPropagationPatches.cs`
- `Networking/LanNetworkManager.cs`, `LanNetworkManager.Handlers.cs`

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
