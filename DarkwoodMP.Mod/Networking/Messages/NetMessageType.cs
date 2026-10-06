using System;

namespace DWMPHorde.Networking
{
    public enum NetMessageType : byte
    {
        /// <summary>Initial handshake for protocol version agreement.</summary>
        Handshake = 1,
        /// <summary>Player position and animation state snapshot.</summary>
        PlayerState = 2,
        /// <summary>Host publishes save/world identifiers for client to match.</summary>
        [HostOnly] WorldSession = 3,
        /// <summary>Physics / door / trap / generator state snapshot (hot path).</summary>
        PhysicsState = 4,
        /// <summary>Item spawn event.</summary>
        ItemSpawn = 5,
        /// <summary>Light source on/off state.</summary>
        [Forwardable] LightState = 6,
        /// <summary>Entity snapshot for interpolation.</summary>
        [HostOnly] EntityState = 7,
        /// <summary>Player attack event targeting a specific entity.</summary>
        PlayerAttack = 8,
        /// <summary>Damage applied to the local player by the remote.</summary>
        DamagePlayer = 9,
        /// <summary>Notification that a player has died.</summary>
        [ForwardablePlayer] PlayerDied = 10,
        /// <summary>Container inventory operation (take/place/remove).</summary>
        [Forwardable] ContainerItem = 11,
        /// <summary>Barricade built/destroyed/damaged event.</summary>
        [Forwardable] BarricadeEvent = 12,
        /// <summary>Workbench upgrade level sync.</summary>
        [Forwardable] WorkbenchLevel = 13,
        /// <summary>Journal/note/key item pickup sync.</summary>
        [Forwardable] JournalItem = 14,
        /// <summary>Friendly fire damage event.</summary>
        [Forwardable] FriendlyFire = 15,
        /// <summary>Player effect/status flags sync (shadow ward, invisibility, etc.).</summary>
        [ForwardablePlayer] PlayerEffectSync = 16,
        /// <summary>Player-made sound notification for AI alert.</summary>
        PlayerSound = 17,
        /// <summary>Player weapon aim scare notification for AI.</summary>
        PlayerScare = 18,
        /// <summary>Dragged object position/rotation sync.</summary>
        [Forwardable] DragSync = 19,
        /// <summary>Trigger a save on the remote peer.</summary>
        SaveSync = 20,
        /// <summary>Host broadcasts current game time to the client.</summary>
        [HostOnly] TimeSync = 21,
        /// <summary>Host→clients: a creature one-shot (voice, hit, death, footstep). Loops ride the entity snapshot.</summary>
        [HostOnly] EntitySound = 22,
        /// <summary>Client->Host: a world object was harvested/destroyed by clicking (e.g. mushroom).</summary>
        WorldObjectRemoved = 23,
        /// <summary>Either peer: player's active light (flashlight/torch/lantern) toggled.</summary>
        [ForwardablePlayer] PlayerLightState = 24,
        /// <summary>Client->Host: player threw a throwable item (molotov, etc.).</summary>
        [ForwardablePlayer] ThrowableSpawn = 25,
        /// <summary>Client->Host: a world object exploded (barrel, gas tank, etc.).</summary>
        [Forwardable] ExplosionTrigger = 26,
        /// <summary>Either peer: play an audio clip at the proxy position.</summary>
        [ForwardablePlayer] PlayerAudio = 27,
        /// <summary>Either peer: a gasoline trail was spawned at a world position.</summary>
        [Forwardable] GasTrailSpawn = 28,
        /// <summary>Either peer: gasoline ignited at a world position (non-explosion ignition).</summary>
        [Forwardable] GasIgnite = 29,
        /// <summary>Either peer: player's torso or legs animation clip changed (immediate event-based sync).</summary>
        [ForwardablePlayer] PlayerAnimation = 30,
        /// <summary>Either peer: player switched animation library (item equip changes weapon sprites).</summary>
        [ForwardablePlayer] PlayerAnimLibrary = 31,
        /// <summary>Host->Client: bullet impact FX (blood, wall hit, muzzle flash) at a world position.</summary>
        [Forwardable] BulletImpact = 32,
        /// <summary>Either peer: player fired a weapon (sync muzzle flash, projectile visuals).</summary>
        [ForwardablePlayer] PlayerFiredWeapon = 33,
        /// <summary>Either peer: an inventory item was dropped into the world.</summary>
        [Forwardable] DroppedItemSpawn = 34,
        /// <summary>Either peer: a networked dropped item was picked up.</summary>
        [Forwardable] DroppedItemPickup = 35,
        /// <summary>Either peer: saw fuel/inventory state changed (convert or add fuel).</summary>
        [Forwardable] SawState = 36,
        /// <summary>Host->Client: NightShadows skill activated; triggers client-side shadow spawns.</summary>
        ShadowEvent = 37,
        /// <summary>Host->Client: an individual shadow was spawned at a specific world position.</summary>
        ShadowSpawn = 38,
        /// <summary>Host->Client: active night scenario name for client-side custom events.</summary>
        ScenarioSync = 39,
        /// <summary>Host->Client: the chosen custom event index for frequency-based random picks.</summary>
        ScenarioEventFired = 40,
        /// <summary>Host->Client: an entity started or stopped burning (for visual sync).</summary>
        [Forwardable] EntityBurning = 41,
        /// <summary>Either peer: a Liquid (gasoline puddle) stopped burning.</summary>
        [Forwardable] LiquidStopBurning = 42,
        /// <summary>Host->Client: an object spawned by Explodes.spawnObjects() at a specific position.</summary>
        [Forwardable] ExplosionSpawnObject = 43,
        /// <summary>Either peer: the local player started or stopped burning (for proxy visual sync).</summary>
        [ForwardablePlayer] PlayerBurning = 44,
        /// <summary>
        /// Client↔Host: inventory/skills backup. Client→host on Save (host stores
        /// <c>client_backup_p{id}.json</c>); host→client on late-join bulk restore.
        /// Optional both ways; protocol stays 19.
        /// </summary>
        ClientStateBackup = 45,
        /// <summary>Either peer: spawn a death bag at a world position on the remote.</summary>
        [Forwardable] DeathBagSpawn = 46,
        /// <summary>Host->Client: synchronize night-death state (which players are dead).</summary>
        NightDeathState = 47,
        /// <summary>Host->Client: synchronize a world flag change.</summary>
        FlagSync = 48,
        /// <summary>Either peer: synchronize a completed trade (shared merchant assortment).</summary>
        [Forwardable] TradeSync = 49,
        /// <summary>Either peer: notify all peers that a death bag has been looted (emptied).</summary>
        [Forwardable] DeathBagLooted = 95,
        /// <summary>Either peer: a dream sequence started.</summary>
        [HostOnly] DreamStarted = 56,
        /// <summary>Either peer: a dream sequence ended.</summary>
        [ForwardablePlayer] DreamEnded = 57,
        /// <summary>Dreamer->Spectator: an item was picked up in a dream (visual sync only).</summary>
        [Forwardable] DreamItemPickup = 58,
        /// <summary>Dreamer->Spectator: an audio clip was played in the dream (forwarded for spectator to hear).</summary>
        [Forwardable] DreamAudio = 59,
        /// <summary>Either peer: a player died in the Final Dreamscene (epilogue).</summary>
        [ForwardablePlayer] FinalDreamsceneDeath = 60,
        /// <summary>Either peer: a Constructible object was built (construction menu).</summary>
        [Forwardable] ConstructibleConstruction = 61,
        /// <summary>Either peer: an InteractiveItem (lever/switch) was toggled.</summary>
        [Forwardable] InteractiveItemSwitch = 62,
        /// <summary>Either peer: a Padlock was unlocked (correct combination entered).</summary>
        [Forwardable] PadlockUnlock = 63,
        /// <summary>Either peer: a Locked component was unlocked (key/lockpick).</summary>
        [Forwardable] LockedUnlock = 64,
        /// <summary>Host->Client: a GameEvents component was fired (authoritative).</summary>
        GameEventsFired = 65,
        /// <summary>Either peer: an ExperienceMachine (hideout oven) was enabled.</summary>
        [Forwardable] HideoutUpgrade = 66,
        /// <summary>Either peer: a player placed a marker on the map at a world position.</summary>
        [Forwardable] MapMarker = 68,
        /// <summary>Either peer: a MapElement was discovered (isOnMap set to true).</summary>
        [Forwardable] MapElementDiscovered = 69,
        // 70, 71 retired (OxygenTankStash, CompressorTankConvert): replaced by OxygenTankTier (157).
        /// <summary>Either peer: a player removed a map marker.</summary>
        [Forwardable] MapMarkerRemove = 72,
        /// <summary>Host->Client: bulk-sync all journal entries on connection.</summary>
        [HostOnly] JournalBulkSync = 73,
        /// <summary>Host->Client: continuous state update for a shadow (position, distanceToPlayer, alive/dead).</summary>
        ShadowStateUpdate = 74,
        /// <summary>Client->Host: request the current state of a container inventory.</summary>
        ContainerStateRequest = 75,
        /// <summary>Host->Client: full container inventory state snapshot.</summary>
        [HostOnly] ContainerStateSync = 76,
        /// <summary>Shared NPC reputation (not isNightTrader). Forwardable for client→host→peers.</summary>
        [Forwardable] ReputationSync = 77,
        /// <summary>Either peer: the local player entered an OutsideLocation (basement, bunker, etc.).</summary>
        [Forwardable] LocationEnter = 78,
        // DreamPositionUpdate was 79 (removed)
        /// <summary>Dream door open (host Broadcast / client→host). Forwardable for 3+ fan-out.</summary>
        [Forwardable] DoorOpen = 80,
        /// <summary>Confirms dream scene has been loaded; receiver unfreezes proxy.</summary>
        DreamEntered = 81,
        /// <summary>Client->Host: local player melee-hit a door/window/item (host applies damage).</summary>
        [Forwardable] MeleeWorldHit = 82,
        /// <summary>Either peer: the local player left an OutsideLocation.</summary>
        [Forwardable] LocationExit = 84,
        /// <summary>Host->Client: spawn a physics-critical entity on the remote side.</summary>
        EntitySpawn = 86,
        /// <summary>Client->Host: a trap was triggered on the client.</summary>
        TrapTriggered = 88,
        /// <summary>Client->Host: forward a dialog decision outcome from the remote player.</summary>
        DialogOutcomeSync = 90,
        /// <summary>Host->Peer: wraps a player-specific message from another client.</summary>
        [HostOnly] RemotePlayerForward = 89,
        /// <summary>Either peer: player started/finished vaulting (Jumpable collision for that proxy only).</summary>
        [Forwardable] VaultState = 92,
        /// <summary>Host->Client: synchronise rain/fog/lightning state.</summary>
        [HostOnly] WeatherSync = 93,
        /// <summary>Host->Client: bulk sync all game flags on connect.</summary>
        [HostOnly] FlagBulkSync = 94,
        /// <summary>Host->Client: bulk sync all NPC reputations on connect.</summary>
        [HostOnly] ReputationBulkSync = 96,
        /// <summary>Client->Host: request host to start a dream.</summary>
        DreamStartRequest = 97,
        /// <summary>Host->Client: current night scenario name.</summary>
        ScenarioStateSync = 98,
        /// <summary>Host->Client: hideout oven enable states.</summary>
        [HostOnly] HideoutStateSync = 99,
        /// <summary>Host->Client: current workbench level.</summary>
        [HostOnly] WorkbenchLevelSync = 100,
        /// <summary>Host->Client: map markers and discoveries.</summary>
        [HostOnly] MapStateSync = 101,
        /// <summary>
        /// Reserved (protocol hole). Skills/XP are per-player and backed up via
        /// ClientStateBackup on save. This message is not sent; the handler is a no-op.
        /// </summary>
        PlayerSkillsSync = 102,
        /// <summary>Host→Client: begin one-shot new-world save file transfer.</summary>
        [HostOnly] WorldSaveBegin = 103,
        /// <summary>Host→Client: one chunk of a compressed save file.</summary>
        [HostOnly] WorldSaveChunk = 104,
        /// <summary>Host→Client: world save transfer finished; client may apply.</summary>
        [HostOnly] WorldSaveEnd = 105,
        /// <summary>Host→Client / host-authoritative: absolute trader shop stock (join bulk, restock, post-trade).</summary>
        [Forwardable] TradeInventorySync = 106,
        /// <summary>Host→all / peer request: load a Unity scene, such as credits.</summary>
        [Forwardable] SceneLoad = 107,
        /// <summary>Host→all: cutscene start, end, or skip.</summary>
        [Forwardable] CutsceneSync = 108,
        /// <summary>Host→all: chapter scene transition.</summary>
        [Forwardable] ChapterTransition = 109,
        /// <summary>Client→host request / host→all state for Examinable.examine.</summary>
        [Forwardable] ExamineObject = 110,
        /// <summary>Either peer: chat / system line (Yokyy product port). Host rebroadcasts via Forwardable.</summary>
        [Forwardable] ChatMessage = 111,
        /// <summary>
        /// Client→host request / host→all grant, deny, or release for an
        /// one-speaker-per-NPC dialogue lock.
        /// </summary>
        DialogNpcLock = 112,
        /// <summary>
        /// Either peer → host/all: CharacterDialogue consumed-node snapshot.
        /// The host fans out after applying it, rather than using automatic
        /// forwarding, to avoid applying it twice.
        /// </summary>
        DialogTreeState = 113,
        /// <summary>
        /// Client→host: request a world save share.
        /// </summary>
        WorldRequest = 114,
        /// <summary>
        /// Host→client: simultaneous loot rejected because the slot is empty
        /// or the item type does not match. The client refunds the local take.
        /// </summary>
        [HostOnly] ContainerTakeDenied = 115,
        /// <summary>
        /// Either peer: feeder used, with its absolute inactive state.
        /// </summary>
        [Forwardable] FeederState = 116,
        /// <summary>
        /// Either peer: lure bait absolute health.
        /// </summary>
        [Forwardable] LureState = 117,
        /// <summary>
        /// Client→host: post-sleep clock state for host-authority adoption.
        /// </summary>
        SleepEndRequest = 118,
        /// <summary>
        /// Client→host request / host→all grant, deny, or release for an
        /// exclusive workbench open. The feature is currently disabled.
        /// </summary>
        WorkbenchLock = 119,
        /// <summary>
        /// Host→peer: dream session snapshot for late-join state.
        /// </summary>
        [HostOnly] DreamSessionBulk = 120,
        /// <summary>
        /// Host→all: next preset in a dream chain.
        /// </summary>
        [HostOnly] DreamChainStart = 121,
        /// <summary>
        /// Client→host: left the hideout and wants the morning freeze cleared.
        /// </summary>
        AfterNightEndRequest = 122,
        /// <summary>
        /// Host→all: peer roster for host-crash migration.
        /// </summary>
        [HostOnly] PeerRoster = 123,
        /// <summary>
        /// Host→all: graceful leave; the elected player becomes host.
        /// </summary>
        [HostOnly] HostHandoff = 124,
        // 125 retired (ThrowableDespawn): flares burn out on each peer's own vanilla clock (FlareClock).
        /// <summary>
        /// Host→peer: trap table bulk for late join.
        /// </summary>
        [HostOnly] TrapBulk = 126,
        /// <summary>
        /// Client→host: NightShadows perk darkness wave. The host spawns
        /// shadows owned by the requesting peer.
        /// </summary>
        NightShadowSpawnRequest = 127,
        /// <summary>
        /// Host→client: dream item collider `isTrigger` flags.
        /// </summary>
        DreamPropCollider = 128,
        /// <summary>
        /// Either peer: compressed Steam Voice samples. The host fans out
        /// unreliable voice data without forwarding it again.
        /// </summary>
        [Forwardable] VoiceData = 129,
        /// <summary>
        /// Client→host: `CustomCursorAction.onActivate`, such as the dream-bed
        /// "Lie down" action.
        /// </summary>
        ActivateCursorAction = 130,
        /// <summary>
        /// Host→requesting client: transport into an OutsideLocation.
        /// </summary>
        LocationTransport = 131,
        /// <summary>
        /// Host→clients: `Character.removeMe` / despawn for fleeing or
        /// temporary wildlife.
        /// </summary>
        [Forwardable] EntityDespawn = 132,
        /// <summary>
        /// Client→host: compact bag item presence for EventTrigger `haveItem`.
        /// </summary>
        PeerHasItem = 133,
        /// <summary>
        /// ChainParent health / attached absolute state (pos-keyed). Forwardable for
        /// client→host→peers fan-out of hits and detach (timer / health-zero / onDie).
        /// </summary>
        [Forwardable] ChainState = 134,
        /// <summary>
        /// World Item / ShadowArmor absolute health (pos-keyed). Forwardable for
        /// mid-fight HP bar + die fan-out (melee / light damageMe).
        /// </summary>
        [Forwardable] ShadowArmorState = 135,
        /// <summary>
        /// Host→peer: late-join bulk of already-fired one-shot GameEvents
        /// (pos + name). Joiner applies via the live GameEventsFired path.
        /// </summary>
        [HostOnly] GameEventsBulk = 136,
        /// <summary>
        /// Door / Window / Item Burn absolute state (pos-keyed). Forwardable for
        /// client→host→peers fan-out when Flame (molotov) ignites world objects.
        /// Character/Player burn stays on EntityBurning / PlayerBurning.
        /// </summary>
        [Forwardable] WorldBurnState = 137,
        /// <summary>
        /// Host→peer late-join: night scenario name + non-firing event flags
        /// (CustomEvent.started, RandomEvent.startedToday/disabled, currentEvent).
        /// Does not replay ScenarioEventFired / RandomEvent.fire.
        /// </summary>
        [HostOnly] ScenarioStateBulk = 138,
        /// <summary>
        /// Host→clients: host is fully in-world (past load). Title clients may
        /// start waiting for WorldSaveBegin / should not treat mid-load as ready.
        /// Added in protocol 25; peers on the same protocol always know every id listed here.
        /// </summary>
        [HostOnly] HostWorldReady = 139,
        /// <summary>
        /// Client→host: chapter world share outcome (Committed / Failed / NotInWorld).
        /// Host waits for these before tearing the network for the chapter scene load.
        /// </summary>
        ChapterShareAck = 140,
        /// <summary>
        /// Host→client: proceed with the chapter load (Proceed=true) or leave the session
        /// with a reason (Proceed=false: share failed, or the client's world does not match).
        /// </summary>
        [HostOnly] ChapterLoadGo = 141,
        /// <summary>
        /// Host→all clients on the morning edge (Controller.startDay): every peer
        /// that is still night-dead leaves spectator and is sent home; all peers
        /// clear night-death bookkeeping. Idempotent.
        /// </summary>
        [HostOnly] NightDeathRelease = 144,
        /// <summary>
        /// Host→one surviving peer at Controller.startAfterNight: that peer's own
        /// morning survival reward (night-trader reputation + saturation).
        /// </summary>
        [HostOnly] MorningReward = 145,
        /// <summary>
        /// Client→host: this peer is about to spawn an outside-location pad the host has
        /// not assigned it a slot for yet. Host replies with <see cref="LocationPadSlotSync"/>.
        /// </summary>
        LocationPadSlotRequest = 142,
        /// <summary>
        /// Host→peers: host-owned outside-location pad slot + yaw assignments (request
        /// reply, new-allocation broadcast, late-join snapshot). Every peer spawns a
        /// location at the host's slot so absolute-position sync agrees across machines.
        /// </summary>
        [HostOnly] LocationPadSlotSync = 143,
        /// <summary>
        /// Host→client: gameplay settings every peer must agree on (friendly fire, loot-share
        /// mode, double items, party multiplier). Sent after the handshake, on roster changes
        /// and when the host changes them in the F2 menu.
        /// </summary>
        [HostOnly] SessionSettings = 146,
        /// <summary>
        /// Host→clients: a host enemy attack frame (melee sensor or projectile). Each client
        /// re-creates it on its own copy of the enemy; only that client's own player can be
        /// hit ("defender decides"). Added in protocol 29.
        /// </summary>
        [HostOnly] EnemyAttack = 147,
        /// <summary>
        /// Client→host: a re-created enemy attack hit this client's own player (damage already
        /// applied there). Host shows the hit on the stand-in for the other peers. Protocol 29.
        /// </summary>
        EnemyHitConfirm = 148,
        /// <summary>
        /// Host→clients: a banshee screams at a player or stops. Sight light for everyone, the
        /// scream, shake and overlay for the player it sees. Protocol 31.
        /// </summary>
        [HostOnly] BansheeAgitation = 149,
        /// <summary>
        /// Host→clients: the porter carried a hideout's stash to another hideout (vanilla
        /// Location.transportAllItemsToCurrentHideout, run on the host only). Peers empty the source
        /// containers and place the delivery package shell; its contents come from the host when
        /// opened. Protocol 33.
        /// </summary>
        [HostOnly] PorterTransport = 150,
        /// <summary>
        /// Host→one client: run a vanilla Player story function on that client's own body (the
        /// host saw it happen to that player, e.g. killing the night trader). Protocol 33.
        /// </summary>
        [HostOnly] PlayerSpecial = 151,
        /// <summary>
        /// Client→host: a finished trade (what this player bought and sold) for the host to check
        /// against its own stock. Host→client with <c>Denied</c>: the stock lacked it; undo the trade.
        /// Replaces the client's whole-stock snapshot. Protocol 33.
        /// </summary>
        TradeCommit = 152,
        /// <summary>
        /// Host→one client: the host's fingerprint of the state that client should share (desync
        /// check, <c>Diagnostics.DesyncCheck</c>). Protocol 33.
        /// </summary>
        [HostOnly] DesyncDigest = 153,
        /// <summary>Client→host: entries wanted for the digest sections that differed. Protocol 33.</summary>
        DesyncDetailRequest = 154,
        /// <summary>Host→client: one section's entries for a detail request. Protocol 33.</summary>
        [HostOnly] DesyncDetail = 155,
        /// <summary>Client→host: what the client's check found, for the host log. Protocol 33.</summary>
        DesyncReport = 156,
        /// <summary>
        /// Host→clients: the best oxygen tank anyone in the party has (1 empty, 2 full); each player
        /// is topped up to it once (<c>Sync.OxygenTankParty</c>). Protocol 35.
        /// </summary>
        [HostOnly] OxygenTankTier = 157,
        /// <summary>Highest used message type ID.</summary>
        _Highest = 157
    }
}
