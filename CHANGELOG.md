# Changelog

## Versioning

The current product line is `0.8.x`. The plugin and display version are
**0.8.130**. The current Horde wire protocol is **30** (bumped in 0.8.130:
`PlayerAudio` drops its unused stop-signal and object-name fields, `LightState`
gains `Switched`; 29 held for 0.8.129 only).

This file is a public ship log. Code-only status and runtime status are called
out separately. A runtime item is not considered verified until it has been
tested in the game.

---

## 0.8.130 — Sound pass: one owner per sound

Branch `dev-entity-sync-remaster`, on top of 0.8.129. **Protocol 29 → 30.** Product
**0.8.129 → 0.8.130**. Built and unit-tested; **runtime is not playtested**. Found by
a code audit of the sound sync, not by a report.

### Doubled and endless world sounds on peers

- **Every synced object sound played twice on peers.** The host (and each client)
  forwarded every world-object sound as `PlayerAudio`, and the peer also re-ran the
  same vanilla method from synced state, which plays its own sound. Doubled: door
  open and close (`Door.openSound` is randomized per peer, so two different clips),
  barricade and door hits and breaks, lamp and switch clicks, light and generator
  start/stop, and game event sounds. Now a sound played inside a vanilla method that
  peers re-run (`Door.open/close/getHit/destroyBarricade/destroyDoor`, every
  `ItemSounds` play method, the `GameEvent.fire` body) is owned by that replay and
  never forwarded (`Audio/ReplayOwnedSound.cs`). Host-only spirit FX game events
  (`def_glow` / `def_shadow`, never fanned out) keep the forward.
- **Forwarded loops never stopped.** A forwarded object loop (fire, generator hum,
  radio, stove, a host's crate scrape) played on peers as a bare positional sound,
  and nothing forwards a stop. No looping sound is forwarded any more; each peer's own
  copy of the object plays and stops its loops.
- **Game event replays echoed back.** A replayed event's sound steps run after its
  delay, outside the apply guard, so the peer forwarded them back to the sender. The
  replay scope now covers those steps.
- **Barricade replay now plays vanilla's sounds.** A barricade break played the break
  sound and then a hit sound; a door break played its break sound twice; a metal door
  hit played the wood hit instead of the metal clang. The replay now plays exactly what
  `Door.getHit` played on the sender.
- **A silent forwarded play stopped the sound everywhere.** A world play at volume 0
  arrived as `AudioController.Stop(id)`, killing every instance of that id on the peer.
  Silent plays are no longer sent and are ignored on arrival.

### Forwarded world and creature sounds on clients

- **World sounds landed on the host's body.** Rules meant for a player's own sounds
  were applied to every forwarded sound: a door's `door_hit_metal` (also a player
  blocked-hit sound) and any id containing `activate` / `switch`+`light` were moved to
  the host's stand-in, and ids containing `_get` / `_hide` played 2D in the listener's
  head. They now apply only to the sender's own player sounds (`StickToSender`).
- **Per-player hear gate flipped by world sounds.** World sounds from the host shared
  the host body's sticky range gate; they now use the stateless range band.

- **Lamp replay clicked on every state change.** Peers played the switch click for
  every lamp toggle, including power restores, scripted toggles and the late-join bulk
  (a click from every lit lamp on joining), and added an end sound vanilla never plays
  for an unpowered lamp. `LightState` now carries `Switched` (set inside
  `Item.switchMe`); the click plays only then, and the rest is the item's own
  `turnOn` / `turnOff` sounds.

### Hearing range: each sound's own

- **Every sound past 690 was cut while connected.** The host keeps areas around
  remote players awake, so a cull is needed, but a fixed 690 (XZ) was shorter than
  many vanilla sounds carry (gunshots, explosions, screams), on the host's own world
  too. A sound is now culled only beyond its own range: a 3D sound as far as the game
  lets it carry (the item's override, else its AudioObject prefab's `maxDistance`),
  never less than the 650 peer range; a 2D sound at the peer range
  (`LocalAudioService.AudibleRange`). The same range gates peer sounds on arrival, peer
  gunshots, explosions and dream sounds, and sets the falloff of a forwarded world sound
  (it used the item override only, else 650).
- **A loop started out of range stayed silent.** The cull ran once at play, so a fire,
  generator or creature idle loop that started far away never became audible when the
  listener walked up. 3D loops are no longer culled; their own falloff silences them
  far away, as in vanilla.
- **Apply flag clobbered.** Several replays (peer sounds, creature sounds, lamps, blood,
  object spawn and destroy, trap and door state, generators, liquid fire) cleared the
  network-apply flag when done, even when an outer scope had set it. They now save and
  restore it.

### Scrape (drag / push) sounds on peers

- **Scrape loop rebuilt on the game's audio path.** `MovingObjectSoundService` played
  a raw `AudioSource`: always the first clip, outside the Sound volume slider and the
  game's fades, its own occlusion filter, and always `movingSound`. It now plays exactly
  vanilla `ItemSounds.Update`: `AudioController.Play(id, object, volumeModifier)`, the
  grass scrape off a Ground, stop with the 0.5 s fade.
- **Drag release stopped every scrape in the world.** The release stop killed every
  playing `AudioObject` with that scrape id, including other crates being pushed by
  other players. It now stops only loops on the released object.
- **Drag release could hit the wrong crate.** The stop looked the object up with
  `GameObject.Find(name)` (first same-named object anywhere, e.g. a dream-pad twin)
  and zeroed its velocity. It now resolves the object nearest the local player.
- Removed the `PlayerAudio` stop-signal and object-name fields and their receive
  branches; nothing has sent them since 0.7.76.

### Dream audio

- **Creature sounds doubled in dreams.** Host dream audio forwarding also sent
  `CharacterSounds` that `EntitySound` already plays, and the host's own player sounds
  that `PlayerAudio` already carries. Both are skipped now, as are replay-owned sounds
  and loops.
- **Dream sounds bypassed the Sound slider.** Clients played them on raw
  `AudioSource`s with a hand-rolled clip lookup (first clip only, many ids unresolved).
  They now play through `AudioController`. The clip lookup cache is gone.

### Voice

- **Proximity voice only worked when touching.** `VoiceRangeFull` / `VoiceRangeMax`
  defaulted to 8 / 28 labelled metres, but the game measures in units where a body is
  about 40 across and peer sounds carry 650. New keys `VoiceFullVolumeDistance` (150)
  and `VoiceMaxDistance` (650); the old keys are ignored (config key change). Voice is
  now heard from the listen position (the followed player while spectating).

### Smaller

- AudioController forward prefixes return before any component lookup when no
  session is live (they ran on every parented sound in single player).
- `ItemSounds.Update` suppression and drag stop use a cached field accessor instead
  of a `Traverse` per call.

---

## 0.8.129 — Entity sync remaster: the player being hit decides the hit

Branch `dev-entity-sync-remaster`. The host still runs every enemy. What changed is
who judges an enemy's hit on a client, and how clients show host enemies.
**Protocol 28 → 29.** Product **0.8.128 → 0.8.129**. Built and unit-tested;
**runtime is not playtested** (first checks in `PLAYTEST.md` section 0).

### Enemy hits on clients ("defender decides")

- **Dodges that looked clean still hit.** The host judged an enemy swing against its
  copy of the client, which trails the real client by about a round trip plus two
  snapshot steps. Now the host sends each attack frame (`EnemyAttack` 147: enemy id,
  sensor or projectile, final damage, host time, attack clip and frame). Each client
  re-creates the attack on the enemy as it sees it, and only that client's own player
  can be hit by the copy. The host original still hits the host player, other enemies,
  barricades and doors, and skips remote stand-ins. Covers melee (`Character.melee`),
  ranged `SensorType` shots (`rangedAttack`) and activity projectiles (`spawnProjectile`),
  both bullets and thrown items. A strike older than 0.6 s on arrival is dropped, not
  landed late.
- **Client copies of enemies fired their own attacks.** A client playing a host attack
  clip ran the clip's attack frame (event 997): a second, unsynced melee sensor that
  could hit the client on top of the host's `DamagePlayer`, damage local doors and
  enemies, and (banshee) spawn a scream that the client then fanned back to the host.
  Clients now skip event 997 on host-driven enemies, and any enemy-type melee sensor
  on a client that is not a re-created copy deals nothing.
- **Repeated swings were invisible.** The snapshot stream only replays a clip when its
  name changes, so a second `Attack1` right after the first never played on clients.
  The attack frame now replays the clip at the host's frame.
- **Enemy projectiles were not shown on clients at all.** They are now re-created as
  above. A thrown copy only flies and judges a direct hit on the local player, then
  goes away; the host original keeps the landed object, the blast and any spawn.
- **Blood and sound for other players.** The hit client reports it (`EnemyHitConfirm`
  148, damage already applied there); the host plays the hit sound and blood on that
  player's stand-in and fans the blood to the other clients.
- **Stays host-decided (not dodge-timed):** the `damagesAroundMe` aura, flier dive,
  `Shooter` turrets, explosions (radius damage), night shadows, traps and fire.

### Enemy presentation on clients

- **Host clock on snapshots.** Every `EntityState` batch carries the host time. Clients
  keep a short pose history per enemy and render it 75 ms (near) / 150 ms (far) behind
  the estimated host clock: smooth motion at the host's real speed, a 50 ms coast
  at most when a packet is late, then hold. Replaces "lerp 100 ms from arrival".
- **20 Hz near players.** Enemies within 800 of a remote player are sent every 50 ms,
  the rest at 10 Hz (was 10 Hz for all). A body that stops gets one more send so
  clients pin the stop pose instead of coasting past it.
- **Smaller snapshots.** Name and prefab path (most of each entry) travel only on an
  id's first three sends and on the 1 s resync; clients cache them per id. A joiner
  waits at most 1 s for an enemy's name.
- **Recycled ids kept the old name on the host.** The name/prefab cache was keyed by id
  only, so an id handed to a different body after a wrap sent the old body's name.
  It is now tied to the body that owns the id.

### Shadow sensor fixes (host-decided path)

- **Shadow hits on clients took armor and interrupted.** Vanilla routes a shadow
  sensor through `getHitByShadow` (flat, no armor, no interrupt); the host relay sent a
  normal hit. `DamagePlayer` now carries `ShadowHit` and the client uses
  `getHitByShadow`.
- **An enemy sensor that hit a client could not reach the host player.** The host relay
  consumed the sensor on a stand-in hit; vanilla does not consume an enemy sensor on a
  player hit. Only player weapon sensors are consumed now.

### Key files

`Domains/Combat/Patches/DefenderAttackPatches.cs` (host send, capture, client 997
skip, client sensor / bullet / throw filters), `Domains/Combat/EnemyAttackNetHandlers.cs`,
`Core/EntityTimeline.cs` (host clock + pose history, unit-tested),
`EntityStateBroadcastService`, `ClientEntityInterpolationService.{cs,Snapshot,Tick}`,
`HostCombatPatches`, `ProxyDamagePatch`, `ExplosionFriendlyFirePatch` (blast depth),
`BulletFXSyncPatch` (enemy impacts not forwarded), messages in `CombatMessages` /
`WorldMessages` / `PlayerMessages`.

---

## 0.8.128 — Code-health audit: dupes, relay, saves, patching, structure

Six parallel audits (networking core, session/saves, Harmony patches, world/items,
combat/story, architecture/tests) followed by a fix pass over every finding.
**Protocol 27 → 28.** Product **0.8.127 → 0.8.128**. Built against stand-in
references only (no game DLLs in the build environment): every change was compile-
checked for new errors on both loaders and the test suite passes, but **runtime is not
playtested**. First things to run: two players looting the same container slot,
a saw convert while the other player takes wood, host migration and reconnect, a
manual-save load, and a Steam client that drops and rejoins (`PLAYTEST.md` section 0).

### Item duplication and loss

- **Container take/place validation never ran for clients.** Every host check was
  gated on `!IsApplyingRemoteState`, which is true for every inbound message, so two
  players looting the same slot both kept the item and place amounts were unbounded.
  Validation now keys on "came from a client"; denies refund exactly and stop the relay.
- The host no longer spawns items from a client's physics snapshot (a picked-up item
  could come back from a client's 5 s resync).
- Remote pickup destroy matches exactly (normalized name / item type, 2 m, dropped
  items only) instead of fuzzy substrings over 8–12 m that could delete a nearby
  wardrobe or pile; the host only grants a world-pickup claim it could actually remove.
- Saw stock: clients send convert/fuel deltas, the host validates against its own stock
  and broadcasts the absolute result (was last-writer-wins absolute stock).
- Pending-take bookkeeping is per container and timestamped; a deny refunds what was
  actually granted. Workbench level only goes up and is not double-sent.
- Loading a manual save no longer re-applies the pre-load character on top of the slot.

### Relay and transport

- A deferred saw update set the "do not relay" flag from the per-frame tick and it
  swallowed the next client message's relay. Per-message state (relay flag, sender id,
  delivery method) is now reset around every inbound message; the sender id is -1
  outside dispatch.
- The host drops traffic from refused peers, from peers that have not completed the
  handshake, from unknown peers, and `[HostOnly]` message types sent by a client
  (forwarded-player wrappers, bulk syncs, world share, roster, dream start/chain, …).
  Client-originated cutscene starts and dropped-item deny/remove are rejected and not
  relayed; every handler reject path suppresses the relay.
- Relays keep the inbound delivery method: 30 Hz drag/voice streams stay unreliable
  instead of queueing every reliable event behind them.
- Steam: a stale connection closing no longer tears down a player who already
  reconnected; the reliable send backlog is capped (a stalled peer is dropped); packets
  are copied once.

### Saves and sessions

- A promoted host (after migration) never auto-saves: not on exit, not on a client's
  SaveSync request.
- The exit checkpoint save skips chapter transitions, dreams, held night deaths and
  game quit.
- "Resend world" no longer permanently mutes clients already in the world.
- World-save swaps are journaled and recovered at startup after a crash; the inflate of
  a host package is size-capped; the campaign identity file is written atomically, never
  re-minted on a parse error, and the host's own campaign is no longer labelled a co-op
  copy. Manual save slots are per profile.
- Player-id rebind on reconnect is limited to ids the host reserved, and moves every
  per-player record. The host derives a client's backup identity from the connection,
  not the uploaded JSON.

### Patching

- The MelonLoader build applied every patch twice (MelonLoader's own PatchAll plus
  ours).
- Patches are applied class by class; a failed class is logged by name and, unless it is
  cosmetic, Host/Join refuse to start instead of running half-patched.
- AI targeting is vanilla while hosting alone; one `Core.AddPrefab` detour per overload
  replaces seven (no `object[]` boxing); one `displayNextBoard` patch replaces four;
  per-frame reflection and component lookups in hot patches are cached; the grid lookup
  tests cached bounds first; the client clock override is restored once on session end.

### Combat and story

- A night-dead host whose last living peer leaves now resolves the morning instead of
  spectating forever; a client death just after dawn is no longer recorded as a night
  death.
- Client attacks are range-checked, rate-limited per peer, and rejected from dead or
  dreaming attackers; BulletImpact only spawns allow-listed FX prefabs; shadow armor
  damage is applied by the host; NPC dialogue locks carry the world bit so a renewal
  never replays the enter-dialogue triggers; host-side queued flag deltas are flushed.
- Host migration repopulates the character tracker (NPCs no longer vanish for
  reconnecting clients); trap ids minted by a promoted host cannot collide.

### Performance

- Map discovery no longer rescans the whole scene per pending name per frame;
  pad resyncs on first enter are limited to the pad instead of the whole map;
  phantom re-matching is throttled; trap/door resend caches are pruned by age.
- Legacy logging builds no strings when it is off (interpolated-string handler); the
  perf probe is opt-in (`Debug.PerfProbe` or the Dev/Trace preset).

### Structure

- Inbound dispatch is one handler table (`On<T>`/`OnRaw`) instead of five switch
  partials and 134 copies of the deserialize call.
- Eight pass-through handler facades are gone; one network-manager locator
  (`ModRuntime.Network`); `NetGuard` replaces 70 copies of the session check; no
  underscore fields reached from outside `LanNetworkManager`; shadow tracking has its
  own registry.
- Wire messages read exactly what they write; the dead "older peer" trailer branches are
  removed (the handshake already refuses other protocols).
- Session statics found leaking are reset (pending light queue times, dream story-end
  deferral, unused final-dream death pass, client pad-slot owners, proxy registry, voice
  walkie/linger flags, among others), and a structural test over every runtime folder
  requires each static to be reset on network stop or marked process-scoped.
- Tests: a reflection round-trip over all 136 message types, carried-state round trips,
  whole-tree Harmony rules (Finalizer for Prefix/Postfix flags, coroutine prefixes,
  narrow exception swallowing) replace the source-text greps; the 500-line file cap is
  gone. CI now runs on `dev`. The product version has one source (PluginInfo).
- Transport seam: LAN and Steam peers sit behind one `IPeerTable` (count, route,
  rebind, drop, roster address); the fan-out and session code no longer branch on the
  backend, and a LAN receive resolves its sender in O(1) instead of a linear scan.
- Per-peer bookkeeping (handshake, refused, loading, late-join bulk, sequence
  high-water marks) is one `LinkState` replaced whenever the transport stops; it sits in
  a `SessionState` replaced on network stop. The heavy late-join retry counter leaked
  across sessions before this.
- `WorldPhysicsSyncService` keeps its ~40 session collections in one session object
  that a reset swaps out (thrown lights survive a host promotion on purpose).
- New `docs/ARCHITECTURE.md`: source layout and the rules a change has to follow.
- Comments no longer narrate version/batch history; 0.8.99 and earlier moved to
  `CHANGELOG-ARCHIVE.md`.

## 0.8.127 — Full-mod audit pass (transport, patches, dupes, lifecycle)

Five parallel code audits (0.8.126 diff, networking, Harmony patches vs the
decompile, gameplay lifecycle, product/docs) followed by a full fix pass.
Nothing was deferred. **Protocol 26 → 27** (new message 146, handshake
password, throwable land flag, long-string framing). Product
**0.8.126 → 0.8.127**. **Runtime: not playtested** — see
`DarkwoodMP.Mod/docs/PLAYTEST.md` for the 0.8.127 checklist; the first things
to run are a busy night on LAN (entity chunking), a client walking through a
container deny, and chapter 1→2.

### Blockers

- **LAN entity/physics sync died in busy scenes.** `EntityState` (up to 256
  entities) and `PhysicsState` went out as one `Unreliable` packet; LiteNetLib
  throws `TooBigPacketException` above the single-packet size (~1 KB) and
  nothing caught it, so `Update()` aborted every tick and clients stopped
  seeing enemies. Snapshots are now chunked per peer limit, each chunk with
  its own sequence; oversize sends on a non-fragmentable method are promoted
  to `ReliableOrdered`; `PollEvents` and per-peer `Send` are guarded with
  throttled logging (`EntityStateBroadcastService`, new
  `LanNetworkManager.PhysicsSend.cs`).
- **Stacked `[HarmonyPatch]` attributes only patched the last target.**
  HarmonyX merges class-level attributes. `ClientAIDisablePatches` left 19 of
  22 client AI methods unpatched (clients ran vanilla AI beside host
  snapshots); `NoWorldPausePatch` left Map/Journal/Padlock/Dialogue/skill
  menus pausing the world. All converted to `TargetMethods()` with a
  logging helper (`Harmony/PatchTargets.cs`); pause/unpause symmetry
  re-checked per UI (dialogue now hooks `onTweenClose`, where vanilla
  unpauses). **Playtest:** client enemy animation — the AI skip has never
  actually run before.
- **Pickup completion never fired.** `PlayerPickupDroppedItemPatch` and
  `ThrownItemCombatDespawnSyncPatch` tested `__instance != null` after
  vanilla `Object.Destroy` (deferred, so always non-null). Success is now
  read from state (slot emptied / vanilla `DestroyMe` recorded), so peers no
  longer keep ghost drops and knives leaving the world send
  `WorldObjectRemoved`.

### Items / dupes

- Deny refunds scanned only `Inventory.slots`; vanilla also stacks into
  `Hotbar` → dupe. Both are scanned; removal goes through `removeAmount`.
- Denied drag-from-container left the item on the cursor
  (`Controller.pickedUpItem`) → cursor item is cancelled first.
- Lost GUID pickup race refunded twice (Remove + ClaimDeny fallback) →
  fallback removed; `recipeFor` still travels with the pending claim.
- `ContainerPlaceItemPatch` snapshotted `Player.currentItem` instead of
  `pickedUpItem` → phantom held weapon sent to peers. Both place patches now
  mirror vanilla's guard and check `__runOriginal`.
- Deny snapshot hid the denied slot (`_pendingContainerRemoves` not cleared).
- `_pendingHideoutUpgrades` never cleared across sessions.
- Culled drops unregistered on `OnDisable` → missing from late-join; now
  `OnDestroy`, registry prunes instead of wiping.
- Drag-proxy item leaked on the host when the dragger disconnected.
- Workbench pile sync depth had no finalizer → one throw disabled it.
- Embedded `PlayerId` trusted over the transport peer in DragSync, trade
  presence, vault state, map markers → host uses `CurrentReceivePlayerId`
  and rewrites forwarded DragSync bodies.

### 3+ players

- Forwarded client `PhysicsState` kept the client's sequence under the host's
  id slot → third player rejected doors/traps/generators as stale. Host
  re-stamps from its own counter; door/trap/generator events go
  `ReliableOrdered`.
- Chat relay forwarded raw client bytes (sender spoof) → host re-serialises.
- Host dropped a peer's `PlayerState` relay while it had no proxy for them
  (location load) → relay before proxy lookup.
- Late joiner never got other clients' light state / anim library → host
  caches and replays per client.

### Host migration / reconnect

- Survivors kept `_lastSnapshotSequence` → entities frozen for ~the old
  host's uptime. `StopTransportOnly` resets inbound sequence state.
- A failed in-world (re)connect (`ConnectionFailed`/`Rejected`) triggered
  migration → client promoted itself to a second host. Migration now requires
  a completed handshake and a real loss; link failures do a bounded
  reconnect (3 × 5 s) or stop with a visible status.
- Steam migrate-connect failure set `Offline` while retry needed `Client`.
- Trap `NetId` counter rewound to 1 while traps kept old ids → never rewinds.
- Promote cleared thrown-flare tracking (flares burned forever) →
  `ResetForPromote()` keeps it.
- Destroyed-on-fire GameEvent list recorded only on the host → clients record
  it too.
- `CaptureForResume` bailed when the link was already down → captures as
  client from config / last Steam lobby.

### Night / death / spectator

- Every spectator exit called `DeathStateTracker.Reset()`, wiping *remote*
  death bookkeeping (dead peer revived, no dawn release, all-dead never
  resolved). Split into `ResetLocal()` (spectator exits) and the full reset.
- `PreventSpectator` was set then cleared by that same reset.
- `NightDeathSavePatch` skipped the save but `SaveSyncPatch.Postfix` still
  sent `SaveSync` → peers persisted the first night death.
- `ForceExit` never unmuted local audio; F4 had no state guard (now mirrors
  save-menu guards + chat input lock).
- Shared permadeath: `PartyWipeDeclared` stuck when `Begin` bailed.
- Night participant counter → id set; clients drop a leaver's night-dead
  mark; late joiners get the night-dead snapshot.
- Client-owned night shadow froze when its owner left; a late `Unreliable`
  alive update revived a dead shadow.
- Remote day-death saves coalesced (one per 15 s).
- Last peer dropping no longer blocks the morning resolve.

### Dreams / dialogue

- `DreamWantToSwitchPatch` chained the preset twice → `MarkCompleted` → host
  stuck. Removed; `SetChainedPreset` ignores a repeat.
- `IsDeadInDream` never cleared for a peer who died → their story end in a
  later dream rejected. Cleared for all peers on dream start/end.
- Client dialogue outcome rebound `dw.npc` and silent-closed the host's own
  conversation with another NPC. Host-in-this-talk is decided by NPC instance
  before any rebind; outcomes arriving mid-talk are queued; drain-abort keyed
  by NPC+owner; apply guards scoped to the synchronous apply (no 8 s window).
- Stale `_preDreamPosition` teleported the client on any disconnect.
- Dream entry coroutines survived disconnect/reject → generation + session
  id checked after every yield.
- Last remote peer leaving ended the host's dream as a death; host now
  continues solo.
- `_hostOrderedDreamEnd` and `IsDreamActive` leaked across a chain.
- Pull-in closes dialogue/trade/container/journal; dialogue lock lease
  renews while open; `Starting` watchdog (60 s).
- Clone trap: NPC locks keyed by (name, dream/overworld); strict pad-only
  lookups; leave-door replay limited to the door NPC.

### World

- Explosions resolved barrels by bare `GameObject.Find` → position first,
  name within 8 u, same world. Rigidbody resolve capped at 50 u and scoped
  to the dream pad. Body-push sound likewise.
- Destructible match 25 m → 2 m; door same-name fallback 20 m → 2 m.
- Pending-barricade flush ran every frame with up to 64 overlap scans →
  1 s gate, 30 s expiry.
- `GameEventsBulk` aborted on the first throwing event → per-item.
- Pad-slot wait is capped at 30 s with vanilla fallback; host answers
  "unavailable" instead of silence.
- `LocationEnterExit` pending maps and `_outboundRemoveDebounce` reset on
  session end.

### Saves / chapter handshake

- `TryCommitPermanentSlot` overwrote savs/sav/savch file by file; a mid-way
  failure left a mixed-chapter slot. Now validate → stage `.dwmp_tmp` → move
  originals to `.dwmp_bak` → rename in → roll back on failure
  (`WorldSaveShareService.SlotCommit.cs`).
- Client committed chapter 2 to its slot at ack time, then sat in chapter 1
  for up to 300 s → commit moved to `ChapterLoadGo`; abort discards the
  buffered package.
- Chapter broadcast requested during a running share was silently skipped →
  re-run to everyone after the running share; mid-broadcast joiners get a
  per-peer share.
- Host deadline now per peer and moved by progress acks
  (`StatusReceiving`); explicit `ChapterLoadGo(Proceed=false)` on refusal;
  host cap 270 s < client 300 s.
- Stale host-share coroutine could clear the new share's flag → generation
  token.

### Singleplayer parity (mod loaded, no session)

Gated on `IsConnected`: personal flavor HUD hiding, NightShadows perk wave
(vanilla never calls `tryToSpawnShadow`), vault collider toggling, random
event gate, scenario-event null returns (now a `GameEvent.fire` skip),
`LureRemoveHealthPatch` global RNG reseed (state saved/restored), trap
harvest interrupt, cursor confine guard, offline campaign-id minting,
inventory open sound, auto-heal, unique-object keep, map marker patches,
swallowing finalizers (NRE-only, dream-prepare-only).

### Other patch fixes

- `HitscanImpactSyncPatch` postfixed global `Physics.Raycast`; the host's
  `ProxyAggroCheck` matched → predators "shot" proxies. Scoped to
  `Player.spawnBullet`.
- Client melee sensor sent a range-600 sound on every trigger and skipped
  vanilla gates/durability → mirrors vanilla.
- Melee weapon status effects (bleed/poison/stun…) now travel with
  `PlayerAttack`, `FriendlyFire` and `DamagePlayer` as a trailing
  `SensorEffectWire` (≤ 8 effects) and are applied through vanilla
  `CharacterEffects.activate` on the receiving side; friendly-fire effects
  follow the same gate as damage (`SensorEffectCodec.cs`).
- `FastProjectile` sweep state stale on pool reuse; despawn destroyed a
  pooled instance.
- Explosion friendly-fire stash was static (nested explosions) → `__state`;
  FF-off sets `affectsPlayer=false` for the call instead of rolling health.
- Host AI: banshee scream target validated; attacking-flee no longer
  permanently clears `runAwayAfterAttacking`; melee sensor and sniffer gates
  restored; flame keeps burn visuals on clients.
- Well heal no longer re-synced/duplicated; client `GasIgnite` stops
  `waitToBurnNeighbors`; gas trail queue flushes on a timer.
- `[HarmonyPriority]` on classes is ignored by HarmonyX → moved to methods
  (all 43). Static prefix→postfix stashes converted to `__state`.
- `DayDeathTransportHomeGridPatch` checks `__runOriginal`; `BirdAreaPresence`
  resettable; `SoundAreaVolumePatch` keeps loudest-wins.

### Networking misc

- `ThrowableSpawnMessage.HasLandTarget` now on the wire (grounded flares
  were spawned in flight).
- `TrapBulk` chunked at 512; reader throws instead of zeroing.
- Late-join heavy phases retry ×3 then log the phase.
- `ClientStateBackup` / `DialogTreeState` use int-prefixed long strings
  (65535 overflow).
- Steam: connection handle closed on peer loss; lobby password no longer
  published — sent in the handshake and verified by the host.
- `ItemSpawn` / `WorldObjectRemoved` `ReliableOrdered`.
- `AutoRecycle`; throttled logs with suppressed counts (`NetLogThrottle`,
  `ModLog.WarnRate`); handler exceptions no longer relay except for a
  sender-authoritative allow-list.
- `Slice()` always copies; `Reset()` dead guard fixed.

### Settings sync (new message 146 `SessionSettings`)

Friendly fire, loot-share mode, double items and the party multiplier were
per-peer config; a client with FF on saw hits the host dropped, and the
multiplier was ×2 on clients / ×3 on the host. The host now sends
`SessionSettings` after handshake and whenever the roster or its own settings
change (checked on the roster tick, so a host config edit reaches peers
within a few seconds); all gameplay readers go through
`Core/SessionSettings.cs`.

### UI / config

- Defaults: `FreeCursorForDualBox` **false** (Linux dual-box testers set it
  true), `VerboseEntitySync` **false**, `LogRedactIPs` **true**. Port
  clamped 1..65535. Config save guarded.
- Chat is real: `ChatEnabled` (default true), Ctrl+C / Enter / Esc, with a
  proper input lock (`UI/UiInputLock.cs`, drives `Core.forbidInputs`) also
  used by F2, F3 and the slot picker. Walkie TX and PTT gated on it.
- F2 writes only changed fields on focus loss/close/apply (no more
  per-keystroke saves, lobby id no longer overwritten).
- HOST/JOIN failures show a 4 s label. Invite/launch-lobby joins use the
  join state machine; join statics clear on Offline.
- F3: client save refused; host success only if `sav.dat` changed; Load
  confirms and is refused in-session; restore waits for `!loadingGame`.
- Voice: Steam availability caches only success; leaver's speaker removed
  on disconnect; clip-miss negative cache.
- Bootstrap: resets registered before `PatchAll`; `Stop()` destroys the
  runtime object.

### Product / docs / tests

- `DisplayVersion` → "YokWare Branch 0.8.127 / Path B".
- New `docs/CONFIG.md` (every key), `PLAYTEST.md` rewritten for 0.8.127,
  `TODO.md` reduced to the parked list, `PATH_B_FEATURE_INVENTORY.md`
  deleted (historical), `COOP_COVERAGE.md` baseline updated, README paths
  for Linux and Windows, EntitySpawner (F5) mentioned.
- `pack-release.ps1` rewritten + `pack-release.sh`; CI pins .NET 8;
  `build-loaders.sh` uses `UNITY_DOTNET_SDK`.
- Removed `debug.log`, four unused `ref_*.png`, ~25 dead methods,
  `FreezeTracker` (inert).
- Tests: release consistency, message round-trips (140–146), dispatch
  coverage, config docs, pad-slot allocation logic; hubs split under 500
  lines.

### Post-pass review fixes

A full regression review of this pass found and fixed:

- Non-GUID world pickup still had the blind "claim deny fallback" refund
  (same double-refund as the GUID path) → removed.
- Chapter share re-run: a stale Committed ack from the superseded pass could
  satisfy the host wait, the client then got Go with no package. Begin and
  `ChapterShareAck` now carry a share pass number (trailing field) and old
  passes are ignored; the client drops its go-wait on supersede; the chapter
  callback runs after the re-run, not before; title clients' NotInWorld acks
  survive the re-run.
- Client shadows (now that `ShadowCreature.Start/die` really are skipped on
  clients) were never destroyed after dying → removed after the death clip.
- Steam password: unauthenticated lobby members got the world session, chat,
  roster and entity stream, and held a player slot. They now get only the
  handshake until the password verifies, and are dropped after 20 s.
- A night-dead client that soft-reconnected was never released at dawn → the
  host always broadcasts the (idempotent) release.
- Dialogue: drain now stops if the host opens its own conversation; deferred
  outcomes use the original sender, expire after 15 s or once the host talks
  to that NPC, and hold back that NPC's close; all dialogue apply state resets
  on session end; a drain that finished inside `StartCoroutine` no longer
  leaves a phantom "drain active".
- `UiInputLock`: overlays do not open while vanilla holds input (cutscene,
  dialogue, dream, loading), and release does not clear a lock vanilla raised
  meanwhile; asserted after the vanilla Update pass too.
- Connect-failure reasons only skip migration before the handshake; a
  `PeerNotFound` from a crashed host now migrates. Migration retry interval
  1 s → 6 s (was shorter than the connect timeout).
- Pad slots, inventory dream-prepare guards and the NPC lock lease use the
  network role instead of `IsConnected` (false during a reconnect blip / for a
  lonely host).
- Map marker and LocationEnter/Exit relays carry the host-stamped sender.
- Shift-click take with full bags (vanilla moves nothing) or a partial merge no
  longer tells the host the whole stack left; refunds remove from the bag
  before the hotbar.
- Steam soft-reconnect failure now fully stops the network; per-peer sequence
  marks cleared on leave; any socket exception in a send is contained;
  `PartyWipeDeclared` cleared if the outcome coroutine dies; bird area reset
  keeps the local player's presence; chat relay survives a HUD exception;
  `DialogHostSilentClose` no longer unlocks input under the host's own
  dialogue; multi-target patch groups skip instead of aborting `PatchAll`
  when every target is missing; per-frame `Object.name` allocation in the
  client AI gate cached.

### Parked (untouched, by prior user choice)

- Mid-dream host migration.
- Morning reward needs the host in its hideout (vanilla `startAfterNight`
  behaviour, not a sync gap).

### Notes

- A hard crash in the middle of the slot swap leaves `.dwmp_bak` files
  next to the slot for manual recovery; every software failure path rolls
  back automatically.
- The host validates peer-supplied sensor effects for well-formedness only;
  it cannot verify they match the attacker's weapon (trusted-LAN model, same
  as the existing FlagSync stance).

---

## 0.8.126 — Campaign-blocker pass (chapter 2, pads, night release)

Full-mod review against the decompile found four ways a real campaign could
stop. All four are fixed in code. **Protocol 25 → 26** (new messages 140–145,
new trailing fields). Both installs need the same DLL. Product
**0.8.125 → 0.8.126**. **Runtime: not playtested** — dual-box ch1→ch2 and a
night with one dead peer are the first things to run.

### Chapter 1 → 2 transition

- **Steam sessions could not resume after chapter 2 loaded.** Chapter resume
  always rehosted/reconnected over LAN. `CaptureForResume` now records the
  backend and lobby; a Steam host keeps its lobby through the scene load and
  re-listens on it, Steam clients rejoin the same lobby
  (`ChapterSessionResume`, `HostConnect`, `Steam/*`).
- **Host tore the network down 0.4 s after queueing the world share.** Slow
  peers could miss the chapter save, then fall back to their own stale local
  chapter after ~36 s. New handshake: client sends `ChapterShareAck` (140)
  after committing the shared world; host re-sends to failed clients (×2),
  waits up to 90 s, then sends `ChapterLoadGo` (141) to each client. The stale
  fallback is gone — a client that never got the world shows an error and
  leaves cleanly (`ChapterCommitHandshake.cs`, `ChapterProgressionPatches`,
  `WorldSaveShareService.ClientApply`).
- **"Already in world" trusted any loaded profile.** Handshake now carries
  campaign id + chapter id (LAN and Steam). Wrong campaign → refused as WRONG
  SAVE; same campaign, other chapter → host sends ChapterTransition and
  re-shares (`LanNetworkManager.WorldIdentity.cs`, `SessionHandlers`).
- **PlayerIds were reassigned on every chapter reconnect.** Host snapshots
  stable-key → PlayerId before teardown and rebinds each peer to its old id.
- **Chapter-jump GameEvent skipped when a client walked the trigger.**
  `transportPlayerToObject` + `activeModifier` (vanilla `generateChapter`) was
  classed as a personal teleport. It is now a world event: host always runs
  it, connected clients never self-fire it (`GameEventPersonalActorPatch`).
  Client no longer shows the vanilla fade before the host's transition
  arrives (presentation only).

### Outside-location pads

- **Same location at different coordinates on different peers.** Vanilla
  places a new pad at `locationPositions[actualSpawnedLocationsCount]` with a
  random yaw, so pads depended on each machine's first-enter order, while
  in-pad sync is keyed by absolute position. Host now owns a
  name → (slot, yaw) map; clients request a slot (`LocationPadSlotRequest`
  142) and wait before the marker spawns; host broadcasts assignments and
  sends the map on late join (`LocationPadSlotSync` 143). Pads that already
  diverged in an old session cannot be moved; they are logged
  (`OutsidePadSlots*.cs`, `OutsidePadSlotPatches.cs`).

### Night / death

- **Night-dead peer spectated forever if anyone survived to dawn.** Release
  only ran on the all-dead path. Host `startDay` now broadcasts
  `NightDeathRelease` (144); dead peers leave spectate and go home, host
  clears remote night-death state. Night-death window now mirrors vanilla.
- **Clients never got the morning survival reward.** Host sends
  `MorningReward` (145) per surviving peer in the hideout: their own
  night-trader reputation, saturation +10, trader help — once per morning.
- **Permadeath asymmetry (hard/nightmare).** Host now uses the same shared
  death rewrite as clients when peers are connected. A night party wipe ends
  the run for everyone only if every death was permadeath-eligible
  (`SharedPermadeathDeath.cs`, `PermadeathPolicy`).

### Robustness

- **Container take dupe.** Host rejected nothing when a take claimed more
  than the slot held. Now denied; refunds remove the granted copy (durability
  / ammo / recipe match) instead of the last slot of that type.
- **One handler exception broke 3+ player forwarding.** Dispatch caught only
  malformed packets; anything else escaped the poll loop and skipped the host
  relay, and the suppress-forward flag could stick. Catch-all with
  rate-limited log; flags reset in `finally`; a message whose local apply
  threw is still relayed.

### Parked (untouched)

- Denied `grabItem` take (item in cursor) still has nothing to refund.
- Morning reward needs the host in its hideout (vanilla `startAfterNight`).
- Failed chapter client must rejoin manually (host re-shares on mismatch).
- Denied dropped-recipe pickup when the pending claim is already gone (deny
  fallback has only the wire `ItemType`) — needs a wire field.

### Post-merge review fixes

- All-dead night granted clients the morning reward; dead peers are now
  marked before the respawn clears them.
- Client chapter go-timeout now outlasts the host's retry-extended ack wait.
- Morning release was dropped for everyone if **any** peer was dreaming at
  dawn. Now gated only on the local peer's own dream
  (`DeathStateTracker.Release.cs`).
- Shared permadeath body counter and party-wipe flag leaked when a death
  coroutine was killed by a scene load → next wipe hung. Reset on network
  reset and Single scene loads; wipe waits at most 30 s.
- `_localNightDeathDay` survived world reload → host skipped one morning
  reward after start-over. New `DeathStateTracker.ResetSession()`.
- Denied pickup of a dropped recipe was not refunded (dupe); `recipeFor`
  now travels with the pending claim.
- Host dying on nightmare/last-life while a peer was still loading could play
  the permadeath video twice; one shared-death decision now drives both
  paths.

---

## 0.8.125 — Save-safety + story-softlock pass closed

Code-proven save-brick and story-softlock holes for the whole class. Protocol
**25** unchanged. Product **0.8.124 → 0.8.125**. Not 1.0.

### Save brick — fixed this pass

- **Connected client world Save.** Vanilla `SaveManager.Save` writes
  `Flags.SaveState` (story flags, NPC dialogue/rep/dead) into DynamicSave. A
  connected client's local Save could persist client-only mutations over the
  host-shared copy. Clients now block disk Save except during host-coordinated
  SaveSync (`_isRemoteSaveInProgress`). Personal bag/skills still go through
  ClientStateBackup; Postfix still requests host SaveSync.
- **Permadeath difficulty restore.** Shared-death rewrite arms the real
  difficulty and restores it before Save (0.8.80). The yield loop now restores
  in `finally` so a suppressed Save or mid-death throw cannot leave nightmare
  forced to normal for a later Save. Host never rewrites difficulty.

### Save brick — already covered

| Row | Status |
|-----|--------|
| Client nightmare / last-life → shared death; Save sees real difficulty | covered (+ finally harden) |
| Client `generateChapter` only reloads host's current chapter | covered |
| Dream failure AbortStarting — no MarkCompleted / no default reward | covered (+ End/OnRemoteDreamEnded harden) |
| UniqueObjects dream remap stash + removeObject keep overworld twin | covered |
| Host-coordinated SaveSync client checkpoint still allowed | covered (exception to block) |
| NPC portrait/anim/reputation/dead — host Flags only, no second format | covered |

### Story softlock — fixed this pass

- **Dream End MarkCompleted on failure names.** `DreamSession.End` and remote
  `MarkDreamCompleted` skip failure cleanup outcomes (`rejected:*`, disconnect,
  prepare fail, storyEndTimeout) so a stray path cannot party-lock the preset.

### Story softlock — already covered

| Row | Status |
|-----|--------|
| GameEventsFired one-shot host-auth; ActorPlayerId personal vs world | covered |
| Padlock/Locked: failed open AttemptOnly; unlock clears both sides + first-enter | covered |
| Dream bunker leave-door GE under pad; UniqueObjects pad prefer | covered |
| Skill party-once + second peer still picks skills | covered |
| All-dead grace 25s; chain keeps death roster | covered |
| Epilogue pan/credits only for peers in ending | covered |
| Dialogue lock release on speaker disconnect | covered |
| World/GUID pickup claim deny refund | covered |

Parked (untouched): multipleFire local; mid-dream host migration disconnect;
workbench exclusive lock off; N-peer handoff; waitToSpawnShadow; night-trader
rep per-player.

---

## 0.8.124 — Night + combat pipelines closed

Code-proven holes across the whole night and the whole combat pipeline.
Protocol **25** unchanged. Product **0.8.123 → 0.8.124**. Not 1.0.

### Night — fixed this pass

- **Client aura double-hit.** Presentation characters still start
  `waitToDamageAroundMe` on clients; that stacked with host `DamagePlayer`
  when both bodies shared an around-me aura. Connected clients now skip the
  local aura; host still hits every living body in vanilla falloff.
- **Proxy night shadows vs host death.** Vanilla `ShadowCreature.Start` hooks
  `Player.Instance.onPlayerDeathDelegate`, so a client-owned perk wave died
  when the host died. Proxy shadows unhook host death, die when their owner
  is dead / night-dead, and still die on day / ward (immortal).
- **Hard-night worms skip night-dead bodies.** Party worm pick already skipped
  ward / `!alive`; it now also skips `LocalNightDeath` / `IsRemoteNightDead`.

### Combat — fixed this pass

- **Client Flame CharBase contact.** Host `ExplosionSpawnObject` Flame
  secondaries on clients were still burning / damaging local bodies on top of
  host proxy relay. Client Flame skips CharBase; world Item/Door/Window burn
  visuals stay.
- **Client Shooter.shoot belt.** `Shooter.Update` is already suppressed on
  clients; `shoot()` now Prefix-skips on connected clients too so an animation
  path cannot stack with host damage.

### Night checklist (covered vs fixed)

| Row | Status |
|-----|--------|
| Clock / TimeSync absolute / no client day-chain | covered |
| useTimeSkip host-adopted; lie-down no time advance | covered |
| Hunger `tryToActivateHunger` live-step; no false-hunger on jumps; `fedToday` clear | covered |
| Hard-night worms 1/5s random living unwarded body; no `waitToSpawnShadow` | covered (+ night-dead skip fixed) |
| Client shadows move (cruise before zero); immortal+ward dies; proxy targets | covered (+ host-death unhook fixed) |
| Fliers dive actual target / damage that body | covered |
| Area damage every body in falloff | covered (+ client double-hit fixed) |
| Perception remotes (vision, hearing, furniture scrape, barricade hammer) | covered |
| Morning: host `endAfterNight` / trader despawn mirrored; no client `startDay` | covered |
| Both dead → morning; one dead does not end night | covered |
| Client nightmare / last-life → shared respawn (0.8.80) | covered |
| Night trader reputation per-player; stock shared | covered (parked: keep per-player) |

### Combat checklist (covered vs fixed)

| Row | Status |
|-----|--------|
| Melee / shooter / thrown (stick despawn no peer FX loot) | covered (+ shooter client belt fixed) |
| Explosions (proxies + FF-off host rollback) | covered |
| Traps / flame contact / ground DoT | covered (+ Flame CharBase mute fixed) |
| Client smash destructible → BarricadeEvent (0.8.117) | covered |
| Gas pour / ignite host (0.8.103–104); fire spread peers | covered |
| Hit story trigger fan (0.8.88); examine with world-only guard (0.8.118) | covered |

Parked (untouched): multipleFire local; mid-dream host migration; workbench
exclusive lock off; N-peer handoff; waitToSpawnShadow; night-trader rep
per-player; hunger method is `tryToActivateHunger`.

---

## 0.8.123 — Dream + ending systems closed (grace, chain, epilogue gate)

Code-proven holes across the whole dream session and the ending crawl/credits.
Protocol **25** unchanged. Product **0.8.122 → 0.8.123**. Not 1.0.

### Dreams

- **All-dead grace.** Host `UnfreezeProxiesAfterDelay` (10s) was calling
  `ClearRemoteInDream`, which dropped peers still inside their 25s entry grace.
  All-dead could end the pad while a joiner or slow loader was still entering.
  Unfreeze now only clears proxy freeze; `IsRemoteInDream` owns the deadline.
- **Chain death roster.** Host `NotifyPeersStoryEndBeginning` called
  `DreamSession.End` before `transferToDream` / `wantToSwitchDream`, so the
  follow-up `SetChainedPreset` hit Idle→`TryBegin` and wiped the death roster
  via `OnDreamStarted`. Chain outcomes keep the session Active; clients skip
  `End` the same way. Latch clears on `SetChainedPreset`.
- **`allDead` default reward.** Remote cleanup for unrecognized outcomes fell
  through to the preset `default` reward. Non-reward sentinels (`allDead`,
  `playerDeath`, rejects, disconnect) no longer grant default; `allDead` maps
  to `playerDeath` effects when present.
- **Live preset name.** `UpdateActivePreset` now also notes `_localDreamPreset`
  so `ResolveActivePresetName` stays on the live pocket after a random roll /
  chain swap.
- Already covered this pass (no code change): enter host/client/second/late-join,
  skill party-once + second peer still picks skills, dream bunker leave-door GE
  under pad (not overworld twin / UniqueObjects first-wins), failure cleanup
  abort without MarkCompleted, exit clears `dreaming` before UniqueObject
  restore, personal GE rewards via ActorPlayerId (0.8.99), client dream
  one-shots via host proxy EventTriggers + GameEventsFired apply.

### Ending

- **Epilogue death fan-out.** Host/client `onDeath` in epilogue still broadcast
  `PlayerDied` and ran `DeathStateTracker`, which could pull living peers into
  day/night death paths. Epilogue deaths are vanilla crawl/cam only.
- **Living peer gate.** Credits `SceneLoad` and `epilogue_cameraPanOverBurningForest`
  apply only when the local peer is in the ending (`inEpilogue` / epilogue pad /
  epilog session). A peer still in the forest is not dragged because someone
  else finished the crawl. Client crawl still asks the host to fire the pan;
  client/host `goToCredits` still pulls the party when they are in.
- Permadeath / nightmare client death shared respawn path verified unchanged
  (0.8.80).

### World-grid houses (checked after dreams+ending)

Vanilla `WorldGrid.Cullable.hide` / `show` use `SetActive` (and `Location.leave`
/`enter` for large location cullables). World houses are **not destroyed** on
cull — same class as OutsideLocations. No virgin-prefab replay invented.

### Parked (unchanged)

- multipleFire local; mid-dream host migration; workbench exclusive lock stub;
  N-peer handoff; no waitToSpawnShadow; night-trader rep per-player; hunger is
  tryToActivateHunger.

---

## 0.8.122 — First-enter pad resync covers remaining virgin-prefab state

0.8.119–0.8.121 replayed barricades, opened doors, destroyed items, NPC
visuals, padlocks, fired one-shot GEs (`ActorPlayerId=0`), and key Locked
unlocks when a client first-spawned an outside pad. Sticky lamp/generator
state already re-pushed via `ResyncWorldLightsForPeer` on the same trigger.
Still missing (late-join pending FIFO/age-out before virgin `createLocation`):
InteractiveItem `isOn` (msg 62 — GE `switchItemOnOff` toggles `Item` only, not
levers), constructed props, traps (30s pending age), world Burn, chains,
ShadowArmor on pad props, and saw/feeder/lure stations on the pad. Protocol
**25** unchanged. Product **0.8.121 → 0.8.122**.

- Host `ResyncOutsideLocationPadForPeer`: also pad-scoped InteractiveItemSwitch
  (isOn set-state, not toggle), ConstructibleConstruction, TrapBulk,
  WorldBurnState, ChainState, ShadowArmorState, Saw/Feeder/Lure — dream path
  still skipped. Client settle invalidates matching scene scans and flushes
  pending for those types.
- Containers stay on-open `ContainerStateRequest` (not first-enter bulk). Gas
  trails are network-spawned at coords (not virgin prefab fields).

### Parked (unchanged)

- multipleFire local; mid-dream host migration; workbench exclusive lock stub;
  N-peer handoff; no waitToSpawnShadow; night-trader rep per-player; hunger is
  tryToActivateHunger.

---

## 0.8.121 — First-enter pad also replays key Locked unlocks

0.8.120 replayed unlocked Padlocks on host first-enter pad resync. Key
`Locked` (not Padlock) had the same hole: late-join `LockedUnlock` shares the
FIFO-capped pending list (64 with padlocks/interactives), and DoorState opened
replay calls `Door.open` without clearing `Locked.locked` (DoorOpen apply does;
first-enter uses DoorState). Unlocked-but-closed doors and non-door Locked
(chests) were never covered by door-open replay. Protocol **25** unchanged.
Product **0.8.120 → 0.8.121**.

- Host `ResyncOutsideLocationPadForPeer`: also send unlocked `LockedUnlock`
  near the pad (host `!locked` only; pad-scoped like padlocks; dreams still
  skip this path). Client apply stays idempotent (`wasLocked` gates host
  `onActivate` synth — directed SendToPlayer, client Role does not re-fire).

### Parked (unchanged)

- multipleFire local; mid-dream host migration; workbench exclusive lock stub;
  N-peer handoff; no waitToSpawnShadow; night-trader rep per-player; hunger is
  tryToActivateHunger.

---

## 0.8.120 — First-enter pad also replays padlocks and fired GEs

0.8.119 replayed barricades, opened doors, destroyed items, and NPC visuals
when a client first-spawned an outside pad the host had already changed.
Padlocks and fired GameEvents that mutated pad geometry (setActive / remove /
moves) were still missing: late-join `PadlockUnlock` pending is FIFO-capped
at 64, and `GameEventsBulk` pending ages out at 60s — often long before the
client's first `spawnLocation`. DoorState opened replay does not clear
`Padlock.locked`. Cache invalidate alone does not re-send expired events.
Protocol **25** unchanged. Product **0.8.119 → 0.8.120**.

- Host `ResyncOutsideLocationPadForPeer`: also send unlocked `PadlockUnlock`
  and pad-scoped `GameEventsBulk` (fired `!multipleFire`, `ActorPlayerId=0`
  world geometry only; destroyOnFire latch near the pad; dream-named skipped
  unless dreaming; dream SoftMatch still pad-root filtered). Dreams still
  skip this path (dream load owns the pad).
- Client outside-location settle: invalidate Padlock / Locked / GameEvents
  scene scans and flush pending locks/GEs so SoftMatch sees virgin-pad
  children when the resync arrives.

### Parked (unchanged)

- multipleFire local; mid-dream host migration; workbench exclusive lock stub;
  N-peer handoff; no waitToSpawnShadow; night-trader rep per-player; hunger is
  tryToActivateHunger.

---

## 0.8.119 — Fresh outside-pad spawn gets host world state

A client's first `OutsideLocations.prepareLocation` /
`createLocation` for a pad not yet in `spawnedLocations` instantiates a
virgin prefab. The host may already have opened doors, torn boards,
smashed crates, and changed NPC portraits/anims. Late-join bulk often
ran while that pad did not exist (find-miss / pending cap). Lights
already re-pushed on first remote enter (`ResyncWorldLightsForPeer`);
doors / barricades / destroyed items / NPC visuals did not. Protocol
**25** unchanged. Product **0.8.118 → 0.8.119**.

- Host `HandleLocationEnter` (first enter / soft-reconnect place): after
  light resync, `ResyncOutsideLocationPadForPeer` re-sends existing
  barricade door/window/item snapshots, opened `DoorState` near the pad,
  and `ReputationBulkSync` (portrait/anim trailers). Idempotent apply —
  already-destroyed crates stay destroyed; no container bulk (open still
  uses `ContainerStateRequest`). Dreams skipped (pad owned by dream load).
- Client barricade pending queue now covers destructible `Item` (IsWindow=2)
  the same as doors/windows, so late-join Destroyed crates can flush after
  the pad wakes even before enter resync.

### Parked (unchanged)

- multipleFire local; mid-dream host migration; workbench exclusive lock stub;
  N-peer handoff; no waitToSpawnShadow; night-trader rep per-player; hunger is
  tryToActivateHunger.

---

## 0.8.118 — Client examine fans examined flags

Host applied a client `ExamineObject` ActionRequest via
`DialogHostApplyGuard.RunHostWorldFanout(examine)`, so triggers and story GE
ran, but `ExaminableExaminePatch` Postfix returned early on
`IsApplyingRemoteState` and never Broadcast `ActionState`. Peers kept
`examined` / `displayedDescriptionPool` unset (re-examine / pool one-shots
desync). Same class as pre-0.8.117 ItemGetHit swallow. Protocol **25**
unchanged. Product **0.8.117 → 0.8.118**.

- `ExaminableExaminePatch.Postfix`: allow fan when `DialogHostApplyGuard.Active`
  (match DoorOpen / GameEventsFired / Journal). Prefix still blocks Request
  re-send under apply. ActionState apply sets fields only; host ignores
  inbound ActionState.
- Sweep of other `IsApplyingRemoteState` / `NetworkApplyGuard` Prefix/Postfix
  send gates: Door/Window getHit already fan; ItemGetHit stays on
  `_processingBarricadeEvent` only (0.8.117); remaining matches are loop
  stops, client host-sim blocks, or visual-only.

### Parked (unchanged)

- multipleFire local; mid-dream host migration; workbench exclusive lock stub;
  N-peer handoff; no waitToSpawnShadow; night-trader rep per-player; hunger is
  tryToActivateHunger.

---

## 0.8.117 — Client smash fans destructible Item mesh

Host applied a client `MeleeWorldHit` on a destructible `Item` (wardrobe /
furniture) inside the inbound receive guard, so `getHit` / `die` ran locally
but `ItemGetHitPatch` returned early on `IsApplyingRemoteState` and never
sent `BarricadeEvent`. Peers kept the intact mesh. Door and Window getHit
patches already fan under that guard; item was the odd one out. Protocol
**25** unchanged. Product **0.8.116 → 0.8.117**.

- `ItemGetHitPatch`: drop the `IsApplyingRemoteState` swallow. Loop stop stays
  `_processingBarricadeEvent` (BarricadeEvent apply). Host local smash still
  fans once (`die` has no second send). Position in the existing message
  still names the pad twin when relevant.
- Workbench pile snapshot depth (0.8.116) and window/radio paths untouched.

---

## 0.8.116 — Workbench pile choke no longer double-sends

0.8.114 added a choke on `removeItemAmountFromPlayer` /
`removeItemDurabilityFromPlayer` so GameEvent pile drains sync. Craft / repair /
upgrade / construct / HammerWork already wrap the same pile with
`WorkbenchSharedPileSync`, so a real pile consume nested the choke and
`SendFullDiff` ran twice with the same before-snapshot — duplicate
`ContainerItem` RemoveItem. On a client craft that second remove hits an empty
slot on the host → deny + take-refund / "Already taken…". Protocol **25**
unchanged. Product **0.8.115 → 0.8.116**. Late-join portrait/anim bulk (0.8.115)
untouched.

- `WorkbenchSharedPileSync`: outermost snapshot depth — nested choke Prefix is
  a no-op; only the outer Postfix diffs once (still covers GE-only drains with
  no outer wrap).
- Bag-only / workbench-closed / `includeAdditionalInventory: false` still send
  nothing (`SendFullDiff` is a true slot diff; Prefix gates unchanged).

---

## 0.8.115 — Late join gets NPC portrait and body sprites

Live `ReputationSync` already carried GameEvent portrait (0.8.96) and
anim-library (0.8.97) trailers for peers already in the session. Late-join
`ReputationBulkSync` still stopped at `attackedID` / `deadID`, so a joiner (or
a peer whose chunk woke after the live packet) kept the vanilla face/body
while the host had the story result — especially when GameEventsBulk soft-
match missed the local copy. Protocol **25** unchanged. Product
**0.8.114 → 0.8.115**.

- `ReputationBulkSync` AvailableBytes trailers: sparse portrait + anim-library
  (same fields as live), filled from host NPC bodies on send.
- Clients apply via existing `ApplyPortrait` / `ApplyAnimLibrary`; queue until
  `NPC.OnEnable` when the body is not loaded yet (late join / other room).
- No new `NetMessageType`. Night-trader standing stay per-player.

### Candidates checked (already in bulk — no ship)

- Generators on/off + fuel: `SyncExistingGeneratorsTo`
- Barricades / constructed sites / dropped items / map / journal / world burn:
  late-join light + heavy phases already cover them
- Reputation standing + dead/attackedID: ReputationBulk since 0.8.93–0.8.95
- Doors: physics door snapshot scan

---

## 0.8.114 — GameEvent pile drain reaches the host

0.8.110–0.8.113 covered craft / repair / upgrade / construct / HammerWork
finish. Vanilla `GameEvent.addOrRemoveInvItem` (and durability drains) still
call `removeItemAmountFromPlayer` /
`removeItemDurabilityFromPlayer(..., includeAdditionalInventory: true)` —
the open workbench pile can be consumed. On the 0.8.99 actor path the remove
runs after `WaitForSeconds` (NetworkApplyGuard already gone) while the
workbench can still be open (e.g. host-replayed container story triggers), so
the client drained a local pile view with no `ContainerItem` diff. Protocol
**25** unchanged. Product **0.8.113 → 0.8.114**.

- Choke-point snapshot/diff on
  `Inventory.removeItemAmountFromPlayer` /
  `removeItemDurabilityFromPlayer` when `includeAdditionalInventory` is true,
  reusing `WorkbenchSharedPileSync` / `SendFullDiff`. Existing entry patches
  kept (no regression).
- No-op when the workbench pile is not open (bag-only). Workbench exclusive
  lock stays off. Hammer alert (0.8.109) untouched.

### Sibling check (rejected — no ship)

- `ExperienceMachine.tryToCook` / LevelingMenu — opens the personal leveling
  UI against the player's own bag only; no shared oven inventory in the
  decompile (generator fuel stays FuelDelta).

---

## 0.8.113 — Barricade finish drains the shared pile on the host

0.8.112 synced construction-place pile drains. Vanilla
`Player.checkFrameTrigger("HammerWork")` when `doneBuilding` loops
`currentConstruction.requirements` through
`removeItemAmountFromPlayer(..., includeAdditionalInventory: true)` against
`openedItemInventory2` — same hole, not covered by construct or
grab/transfer/place patches. Barricade plank world state already fans via
`BarricadeEvent`; 0.8.109 remote-hammer AI alert stays on that path (this
patch does not alert). Protocol **25** unchanged. Product **0.8.112 → 0.8.113**.

- Snapshot/diff the workbench pile on HammerWork finish only
  (`doneBuilding`), reusing `WorkbenchSharedPileSync` /
  `ContainerSnapshotHelper.SendFullDiff` (no second diff path; mid-swing
  hammers send nothing).
- Personal-only plank spend still sends nothing. Host local path fans like
  craft (Broadcast; no self-apply double). Workbench exclusive lock stays off.

### Sibling check (rejected — no ship)

- GameEvent `addOrRemoveInvItem` → `removeItemAmountFromPlayer(...,
  includeAdditionalInventory: true)` — can touch the pile if a workbench is
  open, but unproven that any GE drains the pile (not the bag) with no
  container diff; 0.8.99 actor routing for bag items stays. Left named.

---

## 0.8.112 — Construction place drains the shared pile on the host

0.8.110–0.8.111 synced craft/repair/upgrade pile drains.
`Constructible.construct(manual: true)` still calls
`ConstructionRequirement.removeIngredients`, which uses
`removeItemAmountFromPlayer` / durability drains with
`includeAdditionalInventory: true` against `openedItemInventory2` — invisible
to grab/transfer/place patches. Protocol **25** unchanged. Product
**0.8.111 → 0.8.112**.

- Snapshot/diff the workbench pile on `Constructible.construct`, reusing
  `WorkbenchSharedPileSync` / `ContainerSnapshotHelper.SendFullDiff` (no second
  diff path; one snapshot for the whole requirement loop).
- Placed prop was already synced via `ConstructibleConstructPatch` →
  `ConstructibleConstruction` (remote apply uses `manual: false`, no local
  drain). Only the ingredient consume was missing.
- Personal-only material spend still sends nothing. Host local path fans like
  craft (Broadcast; no self-apply double). Workbench exclusive lock stays off.

### Sibling check (rejected — no ship)

- `Player` HammerWork barricade finish drain — shipped in **0.8.113**.
- GameEvent `removeItemAmountFromPlayer(..., includeAdditionalInventory:
  true)` — personal GE path is 0.8.99 actor-routed; unproven that a GE drains
  the workbench pile (not the bag) with no container diff. Left named.

---

## 0.8.111 — Workbench repair/upgrade drain the shared pile on the host

0.8.110 synced craft pile drains via `CraftSharedPileSyncPatch`. Repair and
upgrade use other entry points that still call
`removeItemAmountFromPlayer` / durability drains with
`includeAdditionalInventory: true` against `openedItemInventory2`, invisible
to grab/transfer/place patches. Protocol **25** unchanged. Product
**0.8.110 → 0.8.111**.

- Snapshot/diff the workbench pile on `InvItemClass.repair` and
  `ItemUpgrade.removeIngredients`, reusing the same
  `ContainerSnapshotHelper.SendFullDiff` helper as craft (no second diff
  implementation).
- Repaired/upgraded item stays on the crafter's personal inventory (vanilla).
  Personal-only material spend still sends nothing. Host local path fans like
  craft (Broadcast to peers; no self-apply double). Workbench exclusive lock
  stays off.

### Sibling check (rejected — no ship)

- ConstructionRequirement / Constructible place drain — shipped in **0.8.112**.
- GameEvent `removeItemAmountFromPlayer(..., includeAdditionalInventory:
  true)` — GE fan-out path, not workbench UI (still parked; see 0.8.112).
- Repair-kit world-object `InputScript` → `repair()` without an open workbench
  pile — gate skips (no `openedItemInventory2` container).

---

## 0.8.110 — Workbench craft consumes the shared pile on the host

Vanilla `CraftingRecipes.doCraft` pulls ingredients via
`removeItemAmountFromPlayer(..., includeAdditionalInventory: true)`, which
can drain the open workbench storage pile (`openedItemInventory2`). That path
uses `Inventory.removeItemAmount` / `InvItemClass.removeAmount`, not the
grab/transfer/place hooks container sync already patches — so a client craft
could keep materials on the host pile (or leave a peer's pile stale). Protocol
**25** unchanged. Product **0.8.109 → 0.8.110**.

- On `doCraft`, snapshot the workbench pile when `openedItemInventory.isWorkbench`.
- After craft, fan existing `ContainerItem` Remove/Place diffs so the host
  applies the consume; product still goes to the crafter's personal inventory
  unless vanilla stacked it into the pile (then PlaceItem carries it).
- Personal-only craft (no pile touch) sends nothing. Workbench exclusive lock
  stays a stub.

### Sibling check (rejected — no ship)

- Rideable bicycle: vanilla has only `porterWhistle` / bike-bell audio (0.8.102);
  no mount/ride path in the decompile.
- Repair/upgrade ingredient drain from the same pile — shipped in **0.8.111**.

---

## 0.8.109 — Client barricade hammer alerts host AI

Vanilla `Player.checkFrameTrigger("HammerWork")` calls
`Character.alertInArea(playerPos, 500f)` each swing while boarding or
dismantling. Co-op `BarricadeEvent` apply only sets plank state on the host, so
remote hammering was silent to host enemies. Protocol **25** unchanged. Product
**0.8.108 → 0.8.109**.

- Host: on remote `Built` (`PlayerBarricade`) or dismantle `Destroyed`
  (`DamageAmount < 0`), fire the same `alertInArea` at 500f — prefer the sender
  proxy position (vanilla uses the player), else the door/window.
- Host local hammer still plays the anim; host does not re-apply its own
  `BarricadeEvent`, so no double alert.
- Dream: no dream-specific barricade block found; if construction runs on a pad,
  alert uses the live object/proxy position (not a stale overworld twin).

### Sibling check (rejected — no ship)

- Other `checkFrameTrigger` noise: footsteps and gunshots already forwarded;
  no repair/chop/shovel `alertInArea` in vanilla anim events.
- Furniture scrape host alert shipped in **0.8.108** — left alone.
- Melee barricade hit `MeleeSensor` 600f is a separate combat path — not this
  pass.

---

## 0.8.108 — Client furniture scrape alerts host AI

Vanilla `ItemSounds.alertCharactersInArea` only fires when `Player.Instance` is
touching or dragging the object. Co-op remote scrape uses MOS and suppresses
`ItemSounds.Update`, so `checkIfMoving` never runs — client push/drag noise was
silent to host enemies. Protocol **25** unchanged. Product **0.8.107 → 0.8.108**.

- Host: while a remote scrape is active, fire `Character.alertInArea` on the
  vanilla 0.5s cadence using `movingAlertDistance` / `movingAlertVolume`.
- Host: widen `ItemSounds.alertCharactersInArea` so remote scrape / remote drag
  names count like a local player body.

### Checked this pass (already covered / no ship)

- Sight acquire: `HostCanSeeEnemyPatch`, `HostCheckForCloserEnemyPatch`,
  `HostSnifferUpdatePatch`, `HostAttackPlayerNearestPatch`.
- Hearing already forwarded: footsteps (`HandleProxyFootstep`), gunshots
  (`ClientFireWeaponSoundPatch`), melee hit noise (`ClientCombatPatches`),
  aim-scare (`ClientAimScarePatch`).
- Banshee / InSightOfPlayer / Shooter retarget / growl / shadow ward / flee
  (`HostDetectionGapPatches`, `HostAIPatches.Targeting`).
- `constantlyAttackPlayer`: field only in DLL; no prefab/asset hit — left alone.
- `AIPath.rotateTowardsPlayerWhenPlayingCustomAni`: faces host during custom
  ani only — not acquisition.

---

## 0.8.107 — Enemy around-me aura hits every body in range

Vanilla `Character.waitToDamageAroundMe` only calls `Player.Instance.getHit`.
The co-op patch retargeted to the *nearest* living body, so when host and a
client were both inside the falloff radius only one took damage. Protocol
**25** unchanged. Product **0.8.106 → 0.8.107**.

- Host applies the vanilla falloff formula to the host body (plus shake/noise)
  and to every living remote proxy in range; proxy `CharBase.getHit` still
  relays via `DamagePlayer` / `ProxyDamagePatch`.

### Checked this pass (no ship)

- `Player.Instance.getHit` sites: `Flier.Update` (`HostFlierDivePatch`),
  `Shooter.shoot` (`HostShooterShootPatch`), `InputScript` cheat kill (skip).
- `Character.attackPlayer` → `HostAttackPlayerNearestPatch` (BirdArea,
  CharacterSpawner, GameEvent, Sniffer, ShadowArmor, self-calls).
- `Explodes.explode` → `ExplosionFriendlyFirePatch` (area + FF-off host restore).
- Traps (`Trigger.checkCollision`): damages the entering `Player` on that peer;
  client stomp already `TrapTriggered` + local getHit.
- `MeleeSensor` → `HostMeleeSensorPatch` / `ProxyDamagePatch`; shadows via
  `ProxyShadowController` / owner skip.
- `Flame` contact uses `CharBase.getHit` → proxy relay; ground DoT is local
  `Player` Update per body.
- Hard-night worm aim / sniffer retarget already shipped.

---

## 0.8.106 — Host combat throw despawn clears peer FX loot

Thrown knives / rocks / flares already fan `ThrowableSpawn` (host combat copy,
client/peer `MuteThrownCombat` FX). When the host combat copy sticks into a
character (or otherwise `DestroyMe`s on land), peer FX copies stayed pickable
while the real item lived in the NPC inventory — `DestroyObjectByPos` cannot
find it, so a world-pickup claim still granted a second copy. Protocol **25**
unchanged. Product **0.8.105 → 0.8.106**.

- `MuteThrownCombat` marks FX copies and clears `stickOnCollide` / land-spawn
  prefab so FX never invents a peer-only stick-into-char grant.
- Host combat `ThrownItem.onCollide` that leaves the world fans
  `WorldObjectRemoved` (consume + ModeRemove) so peer FX ghosts clear or refund
  pending claims. Ground land still uses the existing FX→claim path.

### Checked this pass (no ship)

- Shared world / GUID pickup claim-grant-deny — host-auth solid (optimistic +
  deny/remove refund; Prefix blocks already-consumed). No dual-keep path.
- Window/door barricade place-remove — already `BarricadeEvent`.
- Prologue cutscene catch-up — already `PrologueSync` / `CutsceneSync`.

---

## 0.8.105 — Client burning infection clears it on the host world

A client swinging a flaming torch / melee into an infection splat runs vanilla
`Infection.disappear` locally (`MeleeSensor` non-Character hits are not
redirected), but the disappear Postfix only broadcast when Role was Host — so
the client's splat faded and the host world kept it. Protocol **25** unchanged
(reuses `WorldObjectRemoved`). Product **0.8.104 → 0.8.105**.

- Either peer's `Infection.disappear` sends `WorldObjectRemoved` (`infection_splat`);
  host destroy + N-peer fan-out already existed on that message.

---

## 0.8.104 — Client lighting gasoline reaches the host world

After 0.8.103 pour sync, a client swinging a flaming torch / melee into a puddle
(or a Burn trigger touching Liquid) still called `Liquid.startBurning` only
locally — and that path was Prefix-dropped so dual fire sims would not fight.
Nothing told the host, so the puddles never burned for anyone. Protocol **25**
unchanged (reuses `GasIgnite`). Product **0.8.103 → 0.8.104**.

- Client ignite sends `GasIgnite` and lights a local visual; host validates near
  the actor's proxy, ignites on the host world, Forwardable fans peers.
- Host torch / molotov / neighbor-spread ignite Postfix path unchanged.

### Checked this pass (no ship)

- Dropped items / barricade place-remove / throwable spawn / map discovery /
  journal notes / prologue — already wired (coverage rows stale).
- Digging — no Darkwood dig mechanic in decompile.
- Skill-tree / personal heal/feed/recipes — personal by design.
- **`oxygentank_full` world-pick fan — rejected as silent-skip hole.** Vanilla
  world pick is `Item.getDroppedItem` → already host-auth
  `FinishWorldPickupClaim` / `WorldObjectRemoved` (and GUID
  `DroppedItemPickup`). Empty-tank peer copy stays `OxygenTankStash`;
  `haveItem` softlock is `PeerItemPresence`, not a missing destroy fan.
- **Silent client Prefix sweep (ClientMustNotMutateWorld / Role.Client return
  false / "client skipped"):** remaining NOSEND skips are host-owned worldgen
  spawners, night/shadow AI, bird volumes (host sees proxy), cutscene init
  (host `CutsceneSync` Begin), trader randomize, infection spread, GameEvents
  one-shots (player paths already defer via ActivateCursorAction / Examine /
  DoorOpen / DialogOutcome), gas Object-path molotov scatter (host owns). No
  proven player-action vanish sibling left for 0.8.105.

---

## 0.8.103 — Client gasoline pour reaches the host world

Client ground pour (`Player.waitToSpillLiquid` → `Items/GasolineTrail`) was
Prefix-skipped so clients never invented wild dual scatter, but nothing told
the host to place trails either — the can drained and no puddles appeared for
anyone. Protocol **25** unchanged (reuses `GasTrailSpawn`). Product
**0.8.102 → 0.8.103**.

- Client pour sends `GasTrailSpawn` and places a local visual; host validates
  near the pourer's proxy, spawns on the host world, Forwardable fans peers.
- Generator / saw pour via `FuelDelta` unchanged. Molotov / Explodes trail
  scatter stays host-owned (Object AddPrefab Prefix).

### Checked this pass (no ship)

- Lie-down / `onEndSleep` — vanity wake anim only; does not set `CurrentTime`
  or call `startDay` / `endAfterNight` / `skipDay`. Host and client both end
  sleep without advancing the shared clock. Night→morning is natural time /
  death `skipDay` / `useTimeSkip` (0.8.91). `SleepEndRequest` only
  forward-adopts a clock the sleep anim never changed — not a morning-chain
  hole; no speculative sleep change.
- Window/door barricade place/destroy — already `BarricadeEvent`.
- Hitscan destroying doors/items — already `MeleeWorldHit` redirect.
- Map item `markLocationsOnMap` → `showElement` — already `MapElementDiscovered`.
- Recipe learn / personal heals — intentional local.

---

## 0.8.102 — Client bike bell summons Porter on the host world

Using the bike bell (`porterWhistle`) calls `Location.spawnPorter`, which places
`Events/porterSpawner`. Clients already Prefix-skip `PorterSpawner.Start` /
`waitToSpawn` (host owns the NPC), so a client ring consumed the item and never
placed a spawner the host would run — no Porter for anyone. Protocol **25**
unchanged (reuses `ItemSpawn` with type sentinel `porterWhistle`). Product
**0.8.101 → 0.8.102**.

- Client `Location.spawnPorter` defers to host via existing `ItemSpawn` (not a
  trap prefab path); local “something happened” toast kept.
- Host applies `Events/porterSpawner` at the matching hideout pad (day /
  `porter_inTransit` / `porter_killed` / already-present gates). Peers see
  Porter via entity snapshots — no ItemSpawn fan-out.

### Checked this pass (no ship)

- Eat/drink/medicine/bandage/`InvItemClass.use` personal effects (health,
  `fedToday`, upgrades, recipes, maps) — personal; intentional local.
- `placeOnUse` — code path exists; no shipped asset with the flag enabled.
- Trap `canBePlaced` — already `ItemSpawn` via `TrapPlacementPatch`.
- Host ringing the bell — already host-local `spawnPorter`; no hole.

---

## 0.8.101 — Client padlock / key unlock story events reach every player

A client who cracked a padlock or unlocked a keyed lock already synced the
unlocked state, but the host’s story-trigger replay ran inside the inbound
`NetworkApplyGuard`. That swallowed `GameEventsFired`, so peers never got the
one-shot, and any personal grant hit host `Player.Instance`. Protocol **25**
unchanged. Product **0.8.100 → 0.8.101**.

- Host synth of `onTryToOpenLocked` / `onUnlockPadlock` (padlock) and
  `onActivate` (Locked) now uses `RunHostWorldFanout` (fan-out + actor stamp).
- Does not open the padlock UI on the host (`unlock(false)` unchanged).

### Checked this pass (no ship)

- `onSelectObject` — `Player.selectObject*` only fires it when the target has
  an `OnSelected` marker; that component is registered in the assembly but not
  attached to any shipped scene/prefab/resources asset. Dead path; no message.
- Examine-object story triggers — already host-apply via `ExamineObject` +
  `RunHostWorldFanout` (0.8.x).
- Barricade place/remove — no `EventTrigger` on barricade build/destroy.
- Eat/consume (`InvItemClass.use`) — personal effects only; no `sendTriggerInfo`.
- Projectile impact on world objects — `Bullet`/`ThrownItem` hit `CharBase`
  only; door/item melee already fans via `MeleeWorldHit` + `RunHostWorldFanout`.

---

## 0.8.100 — Client lever / switch story events reach every player

Vanilla `InteractiveItem.switchOn` / `switchOff` force-fire their `EventTriggers`
(area) into `GameEvents`. A client flipping a lever ran that only locally, where
one-shot GameEvents are blocked. The host applied the toggle inside the network
receive, which swallowed the broadcast, so the player who flipped it never got
the story result. Protocol **25** unchanged. Product **0.8.99 → 0.8.100**.

- Host apply of a remote InteractiveItem toggle now fans the GameEvent out the
  same way door / trap / activate fixes do (`RunHostWorldFanout`).
- Does not open the InteractiveItem UI on the host (`switchOn`/`switchOff` only).

### Checked this pass (no ship)

- `onPlayAttackAnimation` — NPC `chooseAttack` on host sim only; client does not cause it.
- `onWakeup` — enum only; `Character.wakeup()` never calls `sendTriggerInfo`.
- `onSpawned` / `onGameObjectActivated` — `EventTriggers` Start/OnEnable lifecycle, not a client-action receive path.
- `onDeath` (Character/Item) — killing blow from client hits already inside getHit `RunHostWorldFanout` (0.8.88).
- `onBurn` — `Burn.Start` schedules a delayed Invoke; `onBurn` runs after `NetworkApplyGuard` ends, so host fire already broadcasts. Client one-shots stay blocked until that fan-out.
- `onSelectObject` — marker + select path exists, but no existing message to host-apply without a new NetMessageType; skipped.
- Barricade place/remove, map discovery, skills confirm, hunger — no matching EventTrigger swallow hole proven this pass (barricade has no triggers; map/skills already have their own sync).

---

---

Older releases (0.8.99 and earlier): [CHANGELOG-ARCHIVE.md](CHANGELOG-ARCHIVE.md).
