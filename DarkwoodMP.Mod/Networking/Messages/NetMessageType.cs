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
        WorldSession = 3,
        /// <summary>Physics / door / trap / generator state snapshot (hot path).</summary>
        PhysicsState = 4,
        /// <summary>Item spawn event.</summary>
        ItemSpawn = 5,
        /// <summary>Light source on/off state.</summary>
        [Forwardable] LightState = 6,
        /// <summary>Entity snapshot for interpolation.</summary>
        EntityState = 7,
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
        TimeSync = 21,
        /// <summary>Host broadcasts an entity sound event to the client.</summary>
        /// <summary>Host→clients: AI CharacterSounds (growl/attack/death/idle). Broadcast from host.</summary>
        [Forwardable] EntitySound = 22,
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
        /// <summary>Client->Host: client's inventory/skills/state backup sent on save.</summary>
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
        [Forwardable] DreamStarted = 56,
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
        /// <summary>Either peer: an oxygentank_empty was acquired; stash a copy in the Workbench.</summary>
        [Forwardable] OxygenTankStash = 70,
        /// <summary>Either peer: the compressor converted an empty tank to a full one.</summary>
        [Forwardable] CompressorTankConvert = 71,
        /// <summary>Either peer: a player removed a map marker.</summary>
        [Forwardable] MapMarkerRemove = 72,
        /// <summary>Host->Client: bulk-sync all journal entries on connection.</summary>
        JournalBulkSync = 73,
        /// <summary>Host->Client: continuous state update for a shadow (position, distanceToPlayer, alive/dead).</summary>
        ShadowStateUpdate = 74,
        /// <summary>Client->Host: request the current state of a container inventory.</summary>
        ContainerStateRequest = 75,
        /// <summary>Host->Client: full container inventory state snapshot.</summary>
        ContainerStateSync = 76,
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
        RemotePlayerForward = 89,
        /// <summary>Either peer: player started/finished vaulting (Jumpable collision for that proxy only).</summary>
        [Forwardable] VaultState = 92,
        /// <summary>Host->Client: synchronise rain/fog/lightning state.</summary>
        WeatherSync = 93,
        /// <summary>Host->Client: bulk sync all game flags on connect.</summary>
        FlagBulkSync = 94,
        /// <summary>Host->Client: bulk sync all NPC reputations on connect.</summary>
        ReputationBulkSync = 96,
        /// <summary>Client->Host: request host to start a dream.</summary>
        DreamStartRequest = 97,
        /// <summary>Host->Client: current night scenario name.</summary>
        ScenarioStateSync = 98,
        /// <summary>Host->Client: hideout oven enable states.</summary>
        HideoutStateSync = 99,
        /// <summary>Host->Client: current workbench level.</summary>
        WorkbenchLevelSync = 100,
        /// <summary>Host->Client: map markers and discoveries.</summary>
        MapStateSync = 101,
        /// <summary>
        /// Reserved (protocol hole). Skills/XP are per-player and backed up via
        /// ClientStateBackup on save — this message is not sent; handler is a no-op.
        /// </summary>
        PlayerSkillsSync = 102,
        /// <summary>Host→Client: begin one-shot new-world save file transfer.</summary>
        WorldSaveBegin = 103,
        /// <summary>Host→Client: one chunk of a compressed save file.</summary>
        WorldSaveChunk = 104,
        /// <summary>Host→Client: world save transfer finished; client may apply.</summary>
        WorldSaveEnd = 105,
        /// <summary>Host→Client / host-authoritative: absolute trader shop stock (join bulk, restock, post-trade).</summary>
        [Forwardable] TradeInventorySync = 106,
        /// <summary>Host→all / peer request: load a Unity scene (credits / epilogue end). Protocol 14.</summary>
        [Forwardable] SceneLoad = 107,
        /// <summary>Host→all: cutscene start / end / skip (4.6). Protocol 15.</summary>
        [Forwardable] CutsceneSync = 108,
        /// <summary>Host→all: chapter scene transition (ch1/ch2). Protocol 16.</summary>
        [Forwardable] ChapterTransition = 109,
        /// <summary>Client→host request / host→all state: Examinable.examine (4.11). Protocol 17.</summary>
        [Forwardable] ExamineObject = 110,
        /// <summary>Either peer: chat / system line (Yokyy product port). Host rebroadcasts via Forwardable.</summary>
        [Forwardable] ChatMessage = 111,
        /// <summary>
        /// Client→host request / host→all grant|deny|release: one-speaker-per-NPC dialogue lock (0.9.2).
        /// Optional for older peers (ignored if unhandled); protocol version stays 19.
        /// </summary>
        DialogNpcLock = 112,
        /// <summary>
        /// Either peer → host/all: CharacterDialogue consumed-node snapshot (Yokyy DialogueSync port).
        /// Host fans out after apply (not auto-Forwardable — avoid double apply).
        /// Optional for older peers; protocol version stays 19.
        /// </summary>
        DialogTreeState = 113,
        /// <summary>
        /// Client→host: pull world save share (Yokyy RequestWorld equivalent).
        /// Optional for older peers; protocol version stays 19.
        /// </summary>
        WorldRequest = 114,
        /// <summary>
        /// Host→client: simultaneous loot rejected (slot empty / type mismatch). Client refunds.
        /// Optional for older peers; protocol version stays 19.
        /// </summary>
        ContainerTakeDenied = 115,
        /// <summary>
        /// Either peer: feeder used (absolute inactive state). Optional; protocol stays 19.
        /// </summary>
        [Forwardable] FeederState = 116,
        /// <summary>
        /// Either peer: lure bait absolute health. Optional; protocol stays 19.
        /// </summary>
        [Forwardable] LureState = 117,
        /// <summary>
        /// Client→host: post-sleep clock snapshot for host-authority adopt. Optional; protocol stays 19.
        /// </summary>
        SleepEndRequest = 118,
        /// <summary>
        /// Client→host request / host→all grant|deny|release: exclusive workbench open. Optional; protocol stays 19.
        /// </summary>
        WorkbenchLock = 119,
        /// <summary>
        /// Host→peer: dream session snapshot (completed presets + hadDreamAtLvl*). Late-join bulk. Optional; protocol 19.
        /// </summary>
        DreamSessionBulk = 120,
        /// <summary>
        /// Host→all: transferToDream next preset (chain). Optional; protocol 19.
        /// </summary>
        [Forwardable] DreamChainStart = 121,
        /// <summary>
        /// Client→host: left hideout / wants morning freeze cleared (host runs endAfterNight once).
        /// Optional; protocol stays 19.
        /// </summary>
        AfterNightEndRequest = 122,
        /// <summary>
        /// Host→all: peer LAN roster (playerId + address + session port) for host-crash migration.
        /// Optional; protocol stays 19.
        /// </summary>
        PeerRoster = 123,
        /// <summary>
        /// Host→all: graceful leave — elect player id becomes host. Optional; protocol stays 19.
        /// </summary>
        HostHandoff = 124,
        /// <summary>
        /// Host→all: thrown projectile/light expired (flare burnout). Optional; protocol 19.
        /// </summary>
        [Forwardable] ThrowableDespawn = 125,
        /// <summary>
        /// Host→peer: trap table bulk (id + triggered + occupant). Late-join. Optional; protocol 19.
        /// </summary>
        TrapBulk = 126,
        /// <summary>Highest used message type ID.</summary>
        _Highest = 126
    }
}
