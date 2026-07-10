using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using DarkwoodMP.Network;
using DarkwoodMP.Packets;
using DarkwoodMP.GameLogic;
using DarkwoodMP.DependencyInjection;
using DarkwoodMP.Patches;

namespace DarkwoodMP.Network;

/// <summary>
/// Main network manager - coordinates the network layer, packet handlers
/// and all game-state sync modules.
/// </summary>
public partial class NetworkManager : MonoBehaviour
{
    public static NetworkManager Instance { get; private set; } = null!;

    public bool IsConnected { get; private set; }
    public bool IsHost { get; private set; }
    public int LocalPlayerId { get; set; } = -1;

    private NetworkLayer _network = null!;
    private PacketReceiver _packetReceiver = null!;
    private PatchRegistry _patchRegistry = null!;
    private PlayerSync _playerSync = null!;
    private EnemySync _enemySync = null!;
    private DoorSync _doorSync = null!;
    private ItemSync _itemSync = null!;
    private EventSync _eventSync = null!;
    private DamageSync _damageSync = null!;
    private InteractiveSync _interactiveSync = null!;
    private WorldSync _worldSync = null!;
    private MovableSync _movableSync = null!;
    private PlayerAnimSync _playerAnimSync = null!;
    private StorySync _storySync = null!;
    private DialogueSync _dialogueSync = null!;
    private InteractionLock _interactionLock = null!;
    private ContainerSync _containerSync = null!;
    private HeldLightSync _heldLightSync = null!;
    private BuildSync _buildSync = null!;
    private BarricadeSync _barricadeSync = null!;
    private EventStateSync _eventStateSync = null!;
    private LockSync _lockSync = null!;
    private StationSync _stationSync = null!;
    private RangedSync _rangedSync = null!;
    private ShadowSync _shadowSync = null!;
    private WeatherSync _weatherSync = null!;
    private WorldTransfer _worldTransfer = null!;
    private SyncCheck _syncCheck = null!;
    private ChatManager _chatManager = null!;
    private LocationSync _locationSync = null!;

    // Join bulk: paced packets targeted at ONE joining player (not broadcast).
    private struct SnapshotItem
    {
        public int TargetPlayerId;
        public Packet Packet;
    }
    private readonly Queue<SnapshotItem> _snapshotQueue = new Queue<SnapshotItem>();
    private const int SnapshotPacketsPerFrame = 15;
    private bool _sessionRegistriesWired;

    private readonly Dictionary<int, GameObject> _remotePlayers = new();
    private readonly Dictionary<int, string> _playerNames = new();
    private readonly List<int> _connectedPlayers = new();
    private bool _initialized = false;

    public IReadOnlyList<int> ConnectedPlayers => _connectedPlayers;
    public IReadOnlyDictionary<int, GameObject> RemotePlayers => _remotePlayers;

    // Set once the server's player list arrives - avoids a join-window race
    // where a client briefly thinks it is the authority before it knows the
    // other players (and whether an in-game host with id 0 is present).
    private bool _playerListReceived;

    /// <summary>
    /// True on the machine that owns global game time and host-authoritative
    /// rolls (day/night, dreams, scheduled random events). An in-game host is
    /// always the authority. On a DEDICATED server nobody is IsHost, so the
    /// lowest-id connected player is elected - deterministic total order, and
    /// re-election is automatic when that player leaves. Provably a no-op for
    /// the in-game-host path: a host client always sees id 0 in the list, so
    /// no client can out-rank the host.
    /// </summary>
    public bool IsTimeAuthority
    {
        get
        {
            if (IsHost) return true;
            if (!IsConnected || LocalPlayerId < 0 || !_playerListReceived) return false;
            foreach (var id in _connectedPlayers)
                if (id >= 0 && id < LocalPlayerId) return false; // someone (incl. host 0) out-ranks us
            return true;
        }
    }

    public string GetPlayerName(int playerId)
    {
        if (playerId == LocalPlayerId)
            return ModConfig.Load().PlayerName + " (you)";
        return _playerNames.TryGetValue(playerId, out var name) ? name : $"Player_{playerId}";
    }
    public int DayNumber { get; internal set; } = 1;
    public bool IsNight { get; internal set; }
    public float TimeOfDay { get; private set; }

    public void SendChat(string message)
    {
        _chatManager.SendMessage(message);
    }

    public void Initialize()
    {
        if (_initialized) return;
        _initialized = true;

        if (Instance != null && Instance != this)
        {
            Destroy(Instance);
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);

        // Initialize DI
        ServiceLocator.Initialize();

        // Register and resolve services
        _network = new NetworkLayer();
        ServiceLocator.Register(typeof(NetworkLayer), _network);

        _packetReceiver = new PacketReceiver();
        ServiceLocator.Register(typeof(PacketReceiver), _packetReceiver);

        _patchRegistry = new PatchRegistry();
        ServiceLocator.Register(typeof(PatchRegistry), _patchRegistry);

        _playerSync = new PlayerSync(_network);
        ServiceLocator.Register(typeof(PlayerSync), _playerSync);

        _enemySync = new EnemySync(_network);
        ServiceLocator.Register(typeof(EnemySync), _enemySync);

        _doorSync = new DoorSync(_network);
        ServiceLocator.Register(typeof(DoorSync), _doorSync);

        _itemSync = new ItemSync(_network);
        ServiceLocator.Register(typeof(ItemSync), _itemSync);

        _eventSync = new EventSync(_network);
        ServiceLocator.Register(typeof(EventSync), _eventSync);

        _damageSync = new DamageSync(_network);
        ServiceLocator.Register(typeof(DamageSync), _damageSync);

        _interactiveSync = new InteractiveSync(_network);
        ServiceLocator.Register(typeof(InteractiveSync), _interactiveSync);

        _worldSync = new WorldSync(_network);
        ServiceLocator.Register(typeof(WorldSync), _worldSync);

        _movableSync = new MovableSync(_network);
        ServiceLocator.Register(typeof(MovableSync), _movableSync);

        _playerAnimSync = new PlayerAnimSync(_network);
        ServiceLocator.Register(typeof(PlayerAnimSync), _playerAnimSync);

        _storySync = new StorySync(_network);
        ServiceLocator.Register(typeof(StorySync), _storySync);

        _dialogueSync = new DialogueSync(_network);
        ServiceLocator.Register(typeof(DialogueSync), _dialogueSync);

        _interactionLock = new InteractionLock(_network);
        ServiceLocator.Register(typeof(InteractionLock), _interactionLock);

        _containerSync = new ContainerSync(_network);
        ServiceLocator.Register(typeof(ContainerSync), _containerSync);

        _heldLightSync = new HeldLightSync();
        ServiceLocator.Register(typeof(HeldLightSync), _heldLightSync);

        _buildSync = new BuildSync(_network);
        ServiceLocator.Register(typeof(BuildSync), _buildSync);

        _barricadeSync = new BarricadeSync(_network);
        ServiceLocator.Register(typeof(BarricadeSync), _barricadeSync);

        _eventStateSync = new EventStateSync(_network);
        ServiceLocator.Register(typeof(EventStateSync), _eventStateSync);

        _lockSync = new LockSync(_network);
        ServiceLocator.Register(typeof(LockSync), _lockSync);

        _stationSync = new StationSync(_network);
        ServiceLocator.Register(typeof(StationSync), _stationSync);

        _rangedSync = new RangedSync();
        ServiceLocator.Register(typeof(RangedSync), _rangedSync);

        _shadowSync = new ShadowSync(_network);
        ServiceLocator.Register(typeof(ShadowSync), _shadowSync);

        _weatherSync = new WeatherSync(_network);
        ServiceLocator.Register(typeof(WeatherSync), _weatherSync);

        _worldTransfer = new WorldTransfer(_network);
        _worldTransfer.WorldProvider = BuildWorldPayload;
        _worldTransfer.OnWorldReady = LoadDownloadedWorld;
        ServiceLocator.Register(typeof(WorldTransfer), _worldTransfer);

        _syncCheck = new SyncCheck(_network);
        ServiceLocator.Register(typeof(SyncCheck), _syncCheck);

        _locationSync = new LocationSync(_network);
        ServiceLocator.Register(typeof(LocationSync), _locationSync);

        _chatManager = ChatManager.Instance;
        ServiceLocator.Register(typeof(ChatManager), _chatManager);

        // Resolve services
        _network = ServiceLocator.Resolve<NetworkLayer>();
        _packetReceiver = ServiceLocator.Resolve<PacketReceiver>();
        _patchRegistry = ServiceLocator.Resolve<PatchRegistry>();
        _playerSync = ServiceLocator.Resolve<PlayerSync>();
        _enemySync = ServiceLocator.Resolve<EnemySync>();
        _doorSync = ServiceLocator.Resolve<DoorSync>();
        _itemSync = ServiceLocator.Resolve<ItemSync>();
        _eventSync = ServiceLocator.Resolve<EventSync>();
        _chatManager = ServiceLocator.Resolve<ChatManager>();

        // Register packet handlers
        _packetReceiver.RegisterHandler(PacketType.PositionUpdate, HandlePositionUpdate);
        _packetReceiver.RegisterHandler(PacketType.HealthUpdate, HandleHealthUpdate);
        _packetReceiver.RegisterHandler(PacketType.PlayerJoined, HandlePlayerJoined);
        _packetReceiver.RegisterHandler(PacketType.PlayerLeft, HandlePlayerLeft);
        _packetReceiver.RegisterHandler(PacketType.EntityState, HandleEntityState);
        _packetReceiver.RegisterHandler(PacketType.DoorState, HandleDoorState);
        _packetReceiver.RegisterHandler(PacketType.PickupState, HandlePickupState);
        _packetReceiver.RegisterHandler(PacketType.GameStateSync, HandleGameStateSync);
        _packetReceiver.RegisterHandler(PacketType.DayNightUpdate, HandleDayNightUpdate);
        _packetReceiver.RegisterHandler(PacketType.EventTrigger, HandleEventTrigger);
        _packetReceiver.RegisterHandler(PacketType.ChatMessage, HandleChatMessage);
        _packetReceiver.RegisterHandler(PacketType.SystemMessage, HandleSystemMessage);
        _packetReceiver.RegisterHandler(PacketType.PlayerList, HandlePlayerList);
        // ActionEvent removed in Ironbark v2 — all domains are typed MessageIds
        _packetReceiver.RegisterHandler(PacketType.TradeInventory, HandleTradeInventory);
        _packetReceiver.RegisterHandler(PacketType.ClientStateBackupChunk, HandleClientStateBackupChunk);
        _packetReceiver.RegisterHandler(PacketType.SaveBeat, HandleSaveBeat);
        _packetReceiver.RegisterHandler(PacketType.LocationEnter, HandleLocationEnter);
        _packetReceiver.RegisterHandler(PacketType.LocationExit, HandleLocationExit);
        _packetReceiver.RegisterHandler(PacketType.MapMarker, HandleMapMarker);
        _packetReceiver.RegisterHandler(PacketType.MapDiscover, HandleMapDiscover);
        _packetReceiver.RegisterHandler(PacketType.Reputation, HandleReputation);
        _packetReceiver.RegisterHandler(PacketType.ReputationBulk, HandleReputationBulk);
        _packetReceiver.RegisterHandler(PacketType.HideoutState, HandleHideoutState);
        _packetReceiver.RegisterHandler(PacketType.JournalSync, HandleJournalSync);
        _packetReceiver.RegisterHandler(PacketType.DreamPrepare, HandleDreamPrepare);
        _packetReceiver.RegisterHandler(PacketType.DreamStart, HandleDreamStart);
        _packetReceiver.RegisterHandler(PacketType.DreamEntered, HandleDreamEntered);
        _packetReceiver.RegisterHandler(PacketType.DreamEnd, HandleDreamEnd);
        _packetReceiver.RegisterHandler(PacketType.DreamAudio, HandleDreamAudio);
        _packetReceiver.RegisterHandler(PacketType.DreamDoor, HandleDreamDoor);
        _packetReceiver.RegisterHandler(PacketType.FinalDreamDeath, HandleFinalDreamDeath);
        _packetReceiver.RegisterHandler(PacketType.CutsceneSync, HandleCutsceneSync);
        _packetReceiver.RegisterHandler(PacketType.ChapterTransition, HandleChapterTransition);
        _packetReceiver.RegisterHandler(PacketType.ChapterNotify, HandleChapterNotify);
        _packetReceiver.RegisterHandler(PacketType.SceneLoad, HandleSceneLoad);
        _packetReceiver.RegisterHandler(PacketType.ExamineRequest, HandleExamineRequest);
        _packetReceiver.RegisterHandler(PacketType.ExamineState, HandleExamineState);
        _packetReceiver.RegisterHandler(PacketType.ContainerState, HandleContainerState);
        _packetReceiver.RegisterHandler(PacketType.ContainerRequest, HandleContainerRequest);
        _packetReceiver.RegisterHandler(PacketType.DeathDropSpawn, HandleDeathDropSpawn);
        _packetReceiver.RegisterHandler(PacketType.BarricadeState, HandleBarricadeState);
        _packetReceiver.RegisterHandler(PacketType.BuildPlaced, HandleBuildPlaced);
        _packetReceiver.RegisterHandler(PacketType.BuildConstruct, HandleBuildConstruct);
        _packetReceiver.RegisterHandler(PacketType.VaultState, HandleVaultState);
        _packetReceiver.RegisterHandler(PacketType.WorldObjectGone, HandleWorldObjectGone);
        _packetReceiver.RegisterHandler(PacketType.InteractionLockSync, HandleInteractionLockSync);
        RegisterIronbarkFHandlers();
        _packetReceiver.RegisterHandler(PacketType.PhysicsStateBatch, HandlePhysicsStateBatch);
        _packetReceiver.RegisterHandler(PacketType.InventoryUpdate, HandleInventoryUpdate);
        _packetReceiver.RegisterHandler(PacketType.DamageUpdate, HandleDamageUpdate);
        _packetReceiver.RegisterHandler(PacketType.InteractiveState, HandleInteractiveState);
        _packetReceiver.RegisterHandler(PacketType.ObjectMove, HandleObjectMove);
        _packetReceiver.RegisterHandler(PacketType.PlayerAnim, HandlePlayerAnim);
        _packetReceiver.RegisterHandler(PacketType.WorldRequest, HandleWorldRequest);
        _packetReceiver.RegisterHandler(PacketType.WorldOffer, HandleWorldOffer);
        _packetReceiver.RegisterHandler(PacketType.WorldChunk, HandleWorldChunk);
        _packetReceiver.RegisterHandler(PacketType.WorldEnd, HandleWorldEnd);

        // Connect network to packet receiver
        _network.OnDataReceived += OnDataReceived;
        _network.OnConnectResponse += OnConnectResponse;
        _network.OnPlayerJoined += (id, name) => TriggerPlayerJoined(id, name);
        _network.OnPlayerLeft += (id, name) => TriggerPlayerLeft(id, name);
        _network.OnPlayerList += HandlePlayerListEvent;
        _network.OnHeartbeat += HandleHeartbeat;
        _network.OnHeartbeatAck += pkt => { };
        _network.OnDisconnected += Disconnect;

        WireSessionRegistries();

        ModLogger.Msg("Session", "NetworkManager initialized (join bulk + sync modules)");
    }

    /// <summary>
    /// Horde-style session hygiene: join collectors + reset actions once per process.
    /// </summary>
    private void WireSessionRegistries()
    {
        if (_sessionRegistriesWired) return;
        _sessionRegistriesWired = true;

        // Join bulk collectors (authority fills list for one target peer)
        JoinSnapshotRegistry.Register((packets, _) => _movableSync?.CollectSnapshot(packets));
        JoinSnapshotRegistry.Register((packets, _) => _doorSync?.CollectSnapshot(packets));
        JoinSnapshotRegistry.Register((packets, _) => _interactiveSync?.CollectSnapshot(packets));
        JoinSnapshotRegistry.Register((packets, _) => _itemSync?.CollectThrowSnapshot(packets));
        JoinSnapshotRegistry.Register((packets, _) => _itemSync?.CollectDroppedSnapshot(packets));
        JoinSnapshotRegistry.Register((packets, _) => _buildSync?.CollectSnapshot(packets));
        JoinSnapshotRegistry.Register((packets, _) => _barricadeSync?.CollectSnapshot(packets));
        JoinSnapshotRegistry.Register((packets, _) => _lockSync?.CollectSnapshot(packets));
        JoinSnapshotRegistry.Register((packets, _) => _weatherSync?.CollectSnapshot(packets));
        JoinSnapshotRegistry.Register((packets, _) => _dialogueSync?.CollectSnapshot(packets));
        JoinSnapshotRegistry.Register((packets, targetId) => _storySync?.CollectSnapshot(packets, targetId));
        JoinSnapshotRegistry.Register((packets, _) => _containerSync?.CollectContainerSnapshot(packets));
        JoinSnapshotRegistry.Register((packets, _) => _containerSync?.CollectDeathDropSnapshot(packets));
        JoinSnapshotRegistry.Register((packets, _) => _stationSync?.CollectSnapshot(packets));
        JoinSnapshotRegistry.Register((packets, _) => _worldSync?.CollectSnapshot(packets));
        JoinSnapshotRegistry.Register((packets, _) => _shadowSync?.CollectSnapshot(packets));
        JoinSnapshotRegistry.Register((packets, _) => _eventStateSync?.CollectSnapshot(packets));
        JoinSnapshotRegistry.Register((packets, _) => _locationSync?.CollectSnapshot(packets));
        JoinSnapshotRegistry.Register((packets, _) => _buildSync?.CollectGasSnapshot(packets));
        JoinSnapshotRegistry.Register((packets, _) => Patches.Reputation_Patch.CollectSnapshot(packets));
        JoinSnapshotRegistry.Register((packets, _) => Patches.Hideout_Patch.CollectSnapshot(packets));
        JoinSnapshotRegistry.Register((packets, _) => Patches.MapSync_Patch.CollectSnapshot(packets));
        JoinSnapshotRegistry.Register((packets, _) => TradeInventorySync.CollectSnapshot(packets));
        JoinSnapshotRegistry.Register((packets, _) => _interactionLock?.CollectSnapshot(packets));

        // Session reset (StopNetwork / Disconnect)
        NetworkResetRegistry.Register(NetworkApplyGuard.ResetDepth);
        NetworkResetRegistry.Register(RemoteApply.ClearRemoteSpawned);
        NetworkResetRegistry.Register(PlayerPositionRegistry.Clear);
        NetworkResetRegistry.Register(() => _playerSync?.OnDisconnected());
        NetworkResetRegistry.Register(() => _enemySync?.Reset());
        NetworkResetRegistry.Register(() => _movableSync?.Reset());
        NetworkResetRegistry.Register(() => _playerAnimSync?.Reset());
        NetworkResetRegistry.Register(() => _storySync?.Reset());
        NetworkResetRegistry.Register(() => _itemSync?.Reset());
        NetworkResetRegistry.Register(() => _containerSync?.Reset());
        NetworkResetRegistry.Register(() => _interactiveSync?.Reset());
        NetworkResetRegistry.Register(() => _heldLightSync?.Reset());
        NetworkResetRegistry.Register(() => _buildSync?.Reset());
        NetworkResetRegistry.Register(() => _barricadeSync?.Reset());
        NetworkResetRegistry.Register(() => _eventStateSync?.Reset());
        NetworkResetRegistry.Register(() => _lockSync?.Reset());
        NetworkResetRegistry.Register(() => _stationSync?.Reset());
        NetworkResetRegistry.Register(() => _shadowSync?.Reset());
        NetworkResetRegistry.Register(() => _doorSync?.Reset());
        NetworkResetRegistry.Register(() => _damageSync?.Reset());
        NetworkResetRegistry.Register(DeathStateTracker.Reset);
        NetworkResetRegistry.Register(DreamSession.ResetIncludingCompletions);
        NetworkResetRegistry.Register(FreezeTracker.Reset);
        NetworkResetRegistry.Register(FinalDreamsceneManager.Reset);
        NetworkResetRegistry.Register(PauseSuppression.Reset);
        NetworkResetRegistry.Register(NightSpectator.Reset);
        NetworkResetRegistry.Register(CoopBalance.InvalidateAllowlistCache);
        NetworkResetRegistry.Register(Patches.Epilogue_Patch.Reset);
        NetworkResetRegistry.Register(Patches.Chapter_Patch.Reset);
        NetworkResetRegistry.Register(Patches.Cutscene_Patch.Reset);
        NetworkResetRegistry.Register(Patches.Reputation_Patch.Reset);
        NetworkResetRegistry.Register(Patches.Hideout_Patch.Reset);
        NetworkResetRegistry.Register(Patches.MapSync_Patch.Reset);
        NetworkResetRegistry.Register(TradeInventorySync.Reset);
        NetworkResetRegistry.Register(ClientStateBackup.ResetSession);
        NetworkResetRegistry.Register(() => _locationSync?.Reset());
        NetworkResetRegistry.Register(() => _worldTransfer?.Reset());
        NetworkResetRegistry.Register(() => _syncCheck?.Reset());
        NetworkResetRegistry.Register(() => _interactionLock?.Reset());
        NetworkResetRegistry.Register(() => _snapshotQueue.Clear());

        ModLogger.Msg("Session", $"Join bulk collectors={JoinSnapshotRegistry.CollectorCount}");
    }

    public void StartHost(int port, string password = "", string playerName = "Host")
    {
        ModLogger.Msg("Session", $"Hosting on port {port}…");
        _network.OnHostStartedCallback = (id, name) => TriggerPlayerJoined(id, name);
        _network.GameStateProvider = () => new GameStateSyncPacket
        {
            DayNumber = DayNumber,
            IsNight = IsNight,
            TimeOfDay = TimeOfDay,
            GameTime = TimeOfDay * 24f
        };

        // Set identity before StartHost so the host-started callback
        // doesn't spawn a remote visual for ourselves
        IsHost = true;
        LocalPlayerId = 0;
        _network.StartHost(port, password, playerName);

        if (!_network.IsConnected)
        {
            // Port bind failed - roll back
            IsHost = false;
            LocalPlayerId = -1;
            ModLogger.Error("Session", "Hosting failed (port in use / bind error)");
            return;
        }

        _connectedPlayers.Add(0);
        IsConnected = true;
        _patchRegistry.ApplyPatches(isHost: true);
    }

    public void StartClient(string ip, int port, string password = "", string playerName = "Player")
    {
        ModLogger.Msg("Session", $"Connecting to {ip}:{port}…");
        _network.ConnectToServer(ip, port, password, playerName);
    }

    private void OnDataReceived(byte[] data)
    {
        // Nested apply scope for the entire receive path (Horde NetworkApplyGuard).
        using (new NetworkApplyGuard())
        {
            _packetReceiver.HandleData(data);
        }
    }

    private void OnConnectResponse(int clientId, string name, bool accepted, string message)
    {
        if (accepted)
        {
            LocalPlayerId = clientId;
            IsConnected = true;
            ModLogger.Msg("Session", $"Connected — local player id={clientId}");
            _patchRegistry.ApplyPatches(isHost: false);

            // Request initial game state from server
            ModLogger.Msg("Join", "Requesting initial game state…");
            var req = new GameStateRequestPacket();
            _network.Send(req);

            SendWorldSeed();

            // World-download: if we connected from the main menu (no game loaded),
            // pull the host's/server's exact world instead of relying on the
            // deterministic-worldgen fallback. If we're already in a game, keep
            // the old behaviour (determinism + live sync).
            if (Player.Instance == null)
            {
                ModLogger.Msg("Join", "Not in a game — requesting world download");
                _chatManager?.AddSystemMessage("Downloading world from host...");
                _worldTransfer.RequestWorld();
            }
        }
        else
        {
            ModLogger.Error("Session", $"Connection rejected: {message}");
            Disconnect();
        }
    }

    public void Update()
    {
        _network.Update();
        if (!IsConnected) return;
        _playerSync?.OnUpdate();
        _playerAnimSync?.OnUpdate();
        _enemySync?.OnMirrorUpdate();
        _enemySync?.OnOwnerUpdate();
        _movableSync?.OnUpdate();
        _itemSync?.OnUpdate();
        _containerSync?.OnUpdate();
        _interactiveSync?.OnUpdate();
        _heldLightSync?.OnUpdate();
        _buildSync?.OnUpdate();
        _barricadeSync?.OnUpdate();
        _eventStateSync?.OnUpdate();
        _lockSync?.OnUpdate();
        _stationSync?.OnUpdate();
        _shadowSync?.OnUpdate();
        _doorSync?.OnUpdate();
        _storySync?.OnUpdate();
        _interactionLock?.OnUpdate();
        _worldTransfer?.OnUpdate();
        TradeInventorySync.FlushPending();
        ClientStateBackup.MaybeRestoreAfterSpawn();
        // Host-from-menu: a joiner may have asked for the world before we
        // loaded one - serve the queued request once we are fully in-game.
        // worldGenFinished gate: BuildWorldPayload force-flushes a save, which
        // must not run mid-worldgen.
        if (IsHost && _worldTransfer != null && _worldTransfer.HasPendingRequest
            && Player.Instance != null && Core.worldGenFinished()
            && Time.frameCount % 120 == 0)
            _worldTransfer.TryServePending();
        _worldSync?.OnUpdate();
        _syncCheck?.OnUpdate();

        MaybeUpgradeRemotePlayers();

        // Drain join bulk: targeted reliable to the joining player only.
        for (var i = 0; i < SnapshotPacketsPerFrame && _snapshotQueue.Count > 0; i++)
        {
            var item = _snapshotQueue.Dequeue();
            _network.SendToPlayer(item.TargetPlayerId, item.Packet, reliable: true);
        }
    }

    private void HandlePositionUpdate(Packet packet)
    {
        if (packet is PositionUpdatePacket pos)
        {
            _playerSync.OnPositionReceived(pos);
        }
    }

    private void HandleHealthUpdate(Packet packet)
    {
        if (packet is HealthUpdatePacket health)
        {
            // Update remote player health bar
            if (_remotePlayers.TryGetValue(health.PlayerId, out var playerObj))
            {
                var visual = playerObj.GetComponent<PlayerVisual>();
                visual?.UpdateHealth(health.Health);
            }

        }
    }

    private void HandlePlayerJoined(Packet packet)
    {
        if (packet is PlayerJoinedPacket joined)
        {
            // Use spawn position from packet if available
            var spawnPos = (joined.SpawnX != 0 || joined.SpawnY != 0 || joined.SpawnZ != 0)
                ? new Vector3(joined.SpawnX, joined.SpawnY, joined.SpawnZ)
                : Vector3.zero;
            var spawnRot = new Quaternion(joined.SpawnRx, joined.SpawnRy, joined.SpawnRz, joined.SpawnRw);
            TriggerPlayerJoinedWithPos(joined.PlayerId, joined.PlayerName, spawnPos, spawnRot);
        }
    }

    private void HandlePlayerLeft(Packet packet)
    {
        if (packet is PlayerLeftPacket left)
        {
            TriggerPlayerLeft(left.PlayerId, left.PlayerName);
        }
    }

    private void HandleEntityState(Packet packet)
    {
        if (packet is EntityStatePacket es)
            _enemySync.OnEntityState(es);
    }

    private void HandleDoorState(Packet packet)
    {
        if (packet is DoorStatePacket door)
            _doorSync.OnDoorState(door);
    }

    private void HandlePickupState(Packet packet)
    {
        if (packet is PickupStatePacket pickup)
            _itemSync.OnPickupState(pickup);
    }

    private void HandleGameStateSync(Packet packet)
    {
        if (packet is GameStateSyncPacket gs)
        {
            TimeOfDay = gs.TimeOfDay;
            ModLogger.Msg($"[GameStateSync] Day {gs.DayNumber}, Night: {gs.IsNight}, Time: {gs.TimeOfDay}");
            _worldSync?.OnGameStateSync(gs);
        }
    }

    private void HandleDayNightUpdate(Packet packet)
    {
        if (packet is DayNightUpdatePacket dn)
        {
            _worldSync?.OnDayNightUpdate(dn);
        }
    }

    private void HandleEventTrigger(Packet packet)
    {
        if (packet is EventTriggerPacket evt)
            _eventSync.OnEventTrigger(evt);
    }

    private void HandleChatMessage(Packet packet)
    {
        if (packet is ChatMessagePacket chat)
            _chatManager.OnChatReceived(chat);
    }

    private void HandleSystemMessage(Packet packet)
    {
        if (packet is SystemMessagePacket sys)
            _chatManager.OnSystemMessage(sys);
    }

    /// <summary>Parse "x,y,z" (invariant floats) from an action-event field.</summary>
    private static bool TryParseVec(string s, System.Globalization.CultureInfo inv, out Vector3 v)
    {
        v = Vector3.zero;
        var c = s.Split(',');
        if (c.Length != 3
            || !float.TryParse(c[0], System.Globalization.NumberStyles.Float, inv, out var x)
            || !float.TryParse(c[1], System.Globalization.NumberStyles.Float, inv, out var y)
            || !float.TryParse(c[2], System.Globalization.NumberStyles.Float, inv, out var z))
            return false;
        v = new Vector3(x, y, z);
        return true;
    }

    private void HandleTradeInventory(Packet packet)
    {
        if (packet is not TradeInventoryPacket p) return;
        if (p.PlayerId == LocalPlayerId) return;
        TradeInventorySync.Handle(p.NpcName, p.StockCsv);
    }

    private void HandleClientStateBackupChunk(Packet packet)
    {
        if (packet is not ClientStateBackupChunkPacket p) return;
        ClientStateBackup.HandleChunk(p.PlayerId, p.Index, p.Total, p.Part);
    }

    private void HandleSaveBeat(Packet packet)
    {
        if (packet is not SaveBeatPacket p) return;
        if (p.PlayerId == LocalPlayerId) return;
        Patches.Save_Patch.ApplyRemoteBeat();
    }

    private void HandleLocationEnter(Packet packet)
    {
        if (packet is not LocationEnterPacket p) return;
        if (p.TargetPlayerId == LocalPlayerId || p.PlayerId == LocalPlayerId) return;
        _locationSync?.OnRemoteEnter(p.TargetPlayerId, p.LocName, new Vector3(p.X, p.Y, p.Z));
    }

    private void HandleLocationExit(Packet packet)
    {
        if (packet is not LocationExitPacket p) return;
        if (p.TargetPlayerId == LocalPlayerId || p.PlayerId == LocalPlayerId) return;
        _locationSync?.OnRemoteExit(p.TargetPlayerId, new Vector3(p.X, p.Y, p.Z));
    }

    private void HandleMapMarker(Packet packet)
    {
        if (packet is not MapMarkerPacket p) return;
        if (p.PlayerId == LocalPlayerId) return;
        Patches.MapSync_Patch.ApplyMarker(p.PlayerId, p.Remove, new Vector3(p.X, p.Y, p.Z));
    }

    private void HandleMapDiscover(Packet packet)
    {
        if (packet is not MapDiscoverPacket p) return;
        if (p.PlayerId == LocalPlayerId) return;
        Patches.MapSync_Patch.ApplyDiscover(p.ElementName);
    }

    private void HandleReputation(Packet packet)
    {
        if (packet is not ReputationPacket p) return;
        if (p.PlayerId == LocalPlayerId) return;
        Patches.Reputation_Patch.ApplyRemote(p.NpcName, p.Value);
    }

    private void HandleReputationBulk(Packet packet)
    {
        if (packet is not ReputationBulkPacket p) return;
        if (p.PlayerId == LocalPlayerId) return;
        Patches.Reputation_Patch.ApplyBulk(p.Payload);
    }

    private void HandleHideoutState(Packet packet)
    {
        if (packet is not HideoutStatePacket p) return;
        if (p.PlayerId == LocalPlayerId) return;
        Patches.Hideout_Patch.ApplyRemote(p.ComponentId, p.Enabled);
    }

    private void HandleJournalSync(Packet packet)
    {
        if (packet is not JournalSyncPacket p) return;
        if (p.PlayerId == LocalPlayerId) return;
        switch (p.Kind)
        {
            case JournalSyncPacket.KindItem:
                _storySync?.ApplyRemoteJournalItem(p.Payload);
                break;
            case JournalSyncPacket.KindRef:
                _storySync?.ApplyRemoteJournalRef(p.RefClass, p.Payload);
                break;
            default:
                _storySync?.ApplyRemoteJournal(p.Payload);
                break;
        }
    }

    private void HandleDreamPrepare(Packet packet)
    {
        if (packet is not DreamPreparePacket p) return;
        if (p.PlayerId == LocalPlayerId) return;
        _storySync?.OnRemoteDream(p.PresetName, p.DreamId);
    }

    private void HandleDreamStart(Packet packet)
    {
        if (packet is not DreamStartPacket p) return;
        if (p.PlayerId == LocalPlayerId) return;
        DreamSession.TryBegin(p.PresetName);
        DreamSession.MarkActive();
        DreamSession.FreezeAllRemotes();
        var authProxy = GetRemotePlayer(p.PlayerId)?.GetComponent<RemotePlayerProxy>();
        if (authProxy != null) authProxy.FreezePosition = true;
        _storySync?.OnRemoteDream(p.PresetName, 0);
        _chatManager?.AddSystemMessage($"{GetPlayerName(p.PlayerId)} entered a dream...");
    }

    private void HandleDreamEntered(Packet packet)
    {
        if (packet is not DreamEnteredPacket p) return;
        if (p.PlayerId == LocalPlayerId) return;
        DreamSession.OnPlayerEntered(p.PlayerId);
    }

    private void HandleDreamEnd(Packet packet)
    {
        if (packet is not DreamEndPacket p) return;
        if (p.PlayerId == LocalPlayerId) return;
        DreamSession.End(p.PresetName);
    }

    private void HandleDreamAudio(Packet packet)
    {
        if (packet is not DreamAudioPacket p) return;
        if (p.PlayerId == LocalPlayerId) return;
        Patches.DreamAudio_Patch.ApplyRemote(p.AudioId, new Vector3(p.X, p.Y, p.Z));
    }

    private void HandleDreamDoor(Packet packet)
    {
        if (packet is not DreamDoorPacket p) return;
        if (p.PlayerId == LocalPlayerId) return;
        Patches.DreamDoor_Patch.ApplyRemote(p.DoorName, new Vector3(p.X, p.Y, p.Z));
    }

    private void HandleFinalDreamDeath(Packet packet)
    {
        if (packet is not FinalDreamDeathPacket p) return;
        if (p.PlayerId == LocalPlayerId) return;
        FinalDreamsceneManager.OnRemoteDeathInDream(p.PlayerId);
    }

    private void HandleCutsceneSync(Packet packet)
    {
        if (packet is not CutsceneSyncPacket p) return;
        if (p.PlayerId == LocalPlayerId) return;
        Patches.Cutscene_Patch.ApplyRemote(p.Begin ? "begin" : "end");
    }

    private void HandleChapterTransition(Packet packet)
    {
        if (packet is not ChapterTransitionPacket p) return;
        if (p.PlayerId == LocalPlayerId) return;
        _chatManager?.AddSystemMessage($"{GetPlayerName(p.PlayerId)} advanced the chapter...");
        Patches.Chapter_Patch.ApplyChapterLoad(p.ChapterId, p.LoadChapterSave, stopNetwork: true);
    }

    private void HandleChapterNotify(Packet packet)
    {
        if (packet is not ChapterNotifyPacket p) return;
        if (p.PlayerId == LocalPlayerId) return;
        _chatManager?.AddSystemMessage(
            $"{GetPlayerName(p.PlayerId)} entered chapter {p.ChapterId} - follow them when you are ready!");
    }

    private void HandleSceneLoad(Packet packet)
    {
        if (packet is not SceneLoadPacket p) return;
        if (p.PlayerId == LocalPlayerId) return;
        Patches.Epilogue_Patch.ApplySceneLoad(p.SceneName, p.DelaySeconds > 0 ? p.DelaySeconds : 8f);
    }

    private void HandleExamineRequest(Packet packet)
    {
        if (packet is not ExamineRequestPacket p) return;
        if (p.PlayerId == LocalPlayerId) return;
        if (!IsTimeAuthority) return;
        Patches.Examine_Patch.ApplyRemoteRequest(p.PlayerId, p.ObjectName, new Vector3(p.X, p.Y, p.Z));
    }

    private void HandleExamineState(Packet packet)
    {
        if (packet is not ExamineStatePacket p) return;
        if (p.PlayerId == LocalPlayerId) return;
        Patches.Examine_Patch.ApplyRemoteState(p.ObjectName, new Vector3(p.X, p.Y, p.Z), p.Examined, p.DescriptionPool);
    }

    private void HandleContainerState(Packet packet)
    {
        if (packet is not ContainerStatePacket p) return;
        if (p.PlayerId == LocalPlayerId) return;
        _containerSync?.OnRemoteContainer(p.ContainerId, p.PayloadCsv);
    }

    private void HandleContainerRequest(Packet packet)
    {
        if (packet is not ContainerRequestPacket p) return;
        if (p.PlayerId == LocalPlayerId) return;
        if (!IsTimeAuthority) return;
        if (!string.IsNullOrEmpty(p.ContainerId))
            _containerSync?.RespondContainerSnapshot(p.PlayerId, p.ContainerId);
    }

    private void HandleDeathDropSpawn(Packet packet)
    {
        if (packet is not DeathDropSpawnPacket p) return;
        if (p.PlayerId == LocalPlayerId) return;
        _containerSync?.OnRemoteDeathDrop(p.Prefab, p.Uid, new Vector3(p.X, p.Y, p.Z), p.PayloadCsv);
    }

    private void HandleBarricadeState(Packet packet)
    {
        if (packet is not BarricadeStatePacket p) return;
        if (p.PlayerId == LocalPlayerId) return;
        if (p.Kind == BarricadeStatePacket.KindWindow)
            _barricadeSync?.OnRemoteWindowBarricade(p.ObjectId, p.Barricaded, p.Health);
        else
            _barricadeSync?.OnRemoteDoorBarricade(p.ObjectId, p.Barricaded, p.Health);
    }

    private void HandleBuildPlaced(Packet packet)
    {
        if (packet is not BuildPlacedPacket p) return;
        if (p.PlayerId == LocalPlayerId) return;
        _buildSync?.OnRemotePlaced(p.ItemType, new Vector3(p.X, p.Y, p.Z), new Quaternion(p.Rx, p.Ry, p.Rz, p.Rw));
    }

    private void HandleBuildConstruct(Packet packet)
    {
        if (packet is not BuildConstructPacket p) return;
        if (p.PlayerId == LocalPlayerId) return;
        _buildSync?.OnRemoteConstruct(p.ObjectId, p.Option);
    }

    private void HandleVaultState(Packet packet)
    {
        if (packet is not VaultStatePacket p) return;
        if (p.PlayerId == LocalPlayerId) return;
        Patches.Vault_Patch.ApplyRemote(p.PlayerId, p.Vaulting);
    }

    private void HandleWorldObjectGone(Packet packet)
    {
        if (packet is not WorldObjectGonePacket p) return;
        if (p.PlayerId == LocalPlayerId) return;
        Patches.WorldHarvest_Patch.ApplyRemote(p.ObjectName, new Vector3(p.X, p.Y, p.Z));
    }

    private void HandleInteractionLockSync(Packet packet)
    {
        if (packet is not InteractionLockSyncPacket p) return;
        if (p.PlayerId == LocalPlayerId) return;
        if (p.Locked)
            _interactionLock?.OnRemoteLock(p.PlayerId, InteractionLock.KindFromChar((char)p.KindChar), p.ObjectId);
        else
            _interactionLock?.OnRemoteUnlock(p.PlayerId, p.ObjectId);
    }

    private void AdoptAuthoritySeed(int playerId, int authSeed)
    {
        if (playerId == LocalPlayerId) return;
        if (authSeed == 0) return;

        var localSeed = ModConfig.Load().WorldSeed;
        Patches.WorldGenSeed_Patch.AdoptNetworkSeed(authSeed);

        if (localSeed == authSeed)
            ModLogger.Msg("World", $"Authority seed {authSeed} matches local config");
        else
            _chatManager.AddSystemMessage($"World seed synced with host ({authSeed}) - random objects will now match.");
    }

    private void CheckWorldSeed(int playerId, int remoteSeed)
    {
        if (playerId == LocalPlayerId) return;

        var localSeed = ModConfig.Load().WorldSeed;
        if (localSeed != 0 && remoteSeed == localSeed)
        {
            ModLogger.Msg("World", $"Seed check OK ({localSeed})");
            _chatManager.AddSystemMessage($"World seed check OK ({localSeed})");
            return;
        }

        var reason = localSeed == 0 || remoteSeed == 0
            ? "WorldSeed is not set on both machines"
            : $"seeds differ (yours: {localSeed}, theirs: {remoteSeed})";
        ModLogger.Warning("World", $"WORLD MISMATCH: {reason}");
        ModLogger.Warning("World", "Worlds generated differently — expect object desyncs");
        ModLogger.Warning("World", "Fix: same WorldSeed on all peers, then each start a NEW GAME");
        _chatManager.AddSystemMessage($"WARNING: world mismatch - {reason}. Set the same WorldSeed on both machines and start new games!");
    }

    private void HandleInventoryUpdate(Packet packet)
    {
        // Deliberately quiet: pushing every pickup into the chat overlay was
        // constant noise once the passive chat log became always visible
        if (packet is InventoryUpdatePacket inv && NetworkLayer.VerboseLogging)
            ModLogger.Msg($"[Inventory] Player {inv.PlayerId} acquired {inv.ItemId} x{inv.Quantity}");
    }

    private void HandleDamageUpdate(Packet packet)
    {
        if (packet is DamageUpdatePacket dmg)
            _damageSync?.OnDamageUpdate(dmg);
    }

    private void HandleInteractiveState(Packet packet)
    {
        if (packet is InteractiveStatePacket interactive)
            _interactiveSync?.OnInteractiveState(interactive);
    }

    private void HandleObjectMove(Packet packet)
    {
        if (packet is ObjectMovePacket move)
            _movableSync?.OnObjectMove(move);
    }

    private void HandlePlayerAnim(Packet packet)
    {
        if (packet is PlayerAnimPacket anim)
            _playerAnimSync?.OnPlayerAnim(anim);
    }

    private void HandleWorldRequest(Packet packet)
    {
        if (packet is not WorldRequestPacket req) return;
        // Serve only if we're the in-game host (a joining client asked), or a
        // dedicated server explicitly asked us to seed it (RequesterId == -1).
        // Without this, every client would try to answer a host-relayed request.
        if (!IsHost && req.RequesterId != -1) return;
        _worldTransfer?.HandleRequest(req);
    }

    public bool IsDownloadingWorld => _worldTransfer?.IsDownloading ?? false;

    /// <summary>Client handshake in flight (menu JOIN feedback).</summary>
    public bool IsConnecting => _network != null && _network.IsConnecting;

    private DateTime _lastServerSaveUpload = DateTime.MinValue;
    private static readonly TimeSpan ServerSaveUploadInterval = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Called by Save_Patch after the authority's save beat. On a DEDICATED
    /// server (no in-game host) the elected authority pushes its fresh save so
    /// the server's canonical world survives everyone disconnecting. A P2P
    /// host serves joiners straight from disk, so it never uploads.
    /// </summary>
    public void NotifyAuthoritySaved()
    {
        if (IsHost || !IsConnected || !IsTimeAuthority) return;
        var now = DateTime.UtcNow;
        if (now - _lastServerSaveUpload < ServerSaveUploadInterval) return;
        if (_worldTransfer == null || !_worldTransfer.UploadNow()) return;
        _lastServerSaveUpload = now;
    }

    /// <summary>Host/seeder (Phase 3): flush the save and hand our world files + profile meta to the transfer.</summary>
    private WorldTransfer.WorldPayload BuildWorldPayload()
    {
        try
        {
            var profile = Core.currentProfile;
            if (profile == null)
            {
                ModLogger.Warning("[WorldTransfer] World requested but no profile is loaded");
                return null;
            }

            // Flush the current game to disk so the transferred save is up to date.
            // This is bookkeeping for the transfer, not a story beat - don't emit
            // a network save beat for it (Save_Patch.SuppressBeat).
            Patches.Save_Patch.SuppressBeat = true;
            try { Singleton<SaveManager>.Instance?.Save(true, true, true, false, false, false, false); }
            catch (Exception ex) { ModLogger.Warning($"[WorldTransfer] save flush failed: {ex.Message}"); }
            finally { Patches.Save_Patch.SuppressBeat = false; }

            var files = SaveFiles.ReadWorld(profile.id);
            if (files.Count == 0)
            {
                ModLogger.Warning($"[WorldTransfer] no world files found for prof{profile.id}");
                return null;
            }

            return new WorldTransfer.WorldPayload
            {
                Chapter = profile.chapter,
                Day = profile.day,
                Difficulty = (int)profile.difficulty,
                MajorVersion = profile.majorVersion,
                MinorVersion = profile.minorVersion,
                RCVersion = profile.RCVersion,
                Files = files
            };
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[WorldTransfer] BuildWorldPayload failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Client (Phase 4): the world was written to the reserved MP slot. Register
    /// it as a proper, compatible profile MERGED into the real profile list, then
    /// prompt the player to Continue it (the game's own robust load path).
    ///
    /// Critical: read the real profiles from disk FIRST and merge into them - a
    /// bare saveProfilesFile() serializes whatever Core.profiles currently holds,
    /// so writing it while the menu list was empty overwrote profs.dat with only
    /// our slot. Version fields are set to the LOCAL game's version so
    /// GameProfile.isCompatible passes and the slot is clickable.
    /// </summary>
    private void LoadDownloadedWorld(WorldOfferPacket offer)
    {
        try
        {
            var id = SaveFiles.MultiplayerProfileId;
            var sm = Singleton<SaveManager>.Instance;

            // Real profile list from disk (never overwrite it with a partial one).
            // GetProfiles/SaveState are non-public, so read them via reflection.
            List<GameProfile> profiles = null;
            try
            {
                var getProfiles = typeof(SaveManager).GetMethod("GetProfiles",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var state = getProfiles?.Invoke(sm, null);
                var profilesField = state?.GetType().GetField("profiles");
                profiles = profilesField?.GetValue(state) as List<GameProfile>;
            }
            catch (Exception ex) { ModLogger.Warning($"[WorldTransfer] GetProfiles failed: {ex.Message}"); }
            profiles ??= Core.profiles ?? new List<GameProfile>();

            var profile = new GameProfile(id, true, offer.Day)
            {
                chapter = offer.Chapter,
                Active = true,
                fullRelease = true,
                // Use THIS game's version so isCompatible passes (host+client match)
                majorVersion = Core.majorVersion,
                minorVersion = Core.minorVersion,
                RCVersion = Core.RCVersion
            };
            var diffField = typeof(GameProfile).GetField("difficulty");
            if (diffField != null)
                diffField.SetValue(profile, Enum.ToObject(diffField.FieldType, offer.Difficulty));

            profiles.RemoveAll(p => p != null && p.id == id);
            profiles.Add(profile);
            Core.profiles = profiles;
            Core.currentProfile = profile;

            if (sm != null)
            {
                try { sm.updateFilePaths(); } catch (Exception ex) { ModLogger.Warning($"[WorldTransfer] updateFilePaths: {ex.Message}"); }
                try { sm.saveProfilesFile(); } catch (Exception ex) { ModLogger.Warning($"[WorldTransfer] saveProfilesFile: {ex.Message}"); }
            }

            // Auto-load through the game's own Continue path: Button.playProfile
            // is literally UI.initLoadGame() once Core.currentProfile is set
            // (which it is, above) - no UI clicks needed. Fall back to the
            // manual prompt if the UI singleton isn't there.
            var ui = Singleton<global::UI>.Instance;
            if (Core.mainMenu && ui != null)
            {
                var menu = UnityEngine.Object.FindObjectOfType(typeof(MainMenu)) as MainMenu;
                if (menu != null) menu.creatingProfile = false;
                ModLogger.Msg($"[WorldTransfer] World written to prof{id} - loading it (native Continue path)");
                _chatManager?.AddSystemMessage("World downloaded - loading...");
                // Allow ClientStateBackup.MaybeRestoreAfterSpawn after this load.
                ClientStateBackup.ResetSession();
                ui.StartCoroutine(ui.initLoadGame());
            }
            else
            {
                ModLogger.Msg($"[WorldTransfer] World written to prof{id} and registered - prompt player to Continue");
                _chatManager?.AddSystemMessage($"World downloaded! Open Profiles and press Continue on profile {id} to join.");
                ClientStateBackup.ResetSession();
            }
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[WorldTransfer] LoadDownloadedWorld failed: {ex.Message}");
        }
    }

    private void HandleWorldOffer(Packet packet)
    {
        if (packet is WorldOfferPacket offer)
            _worldTransfer?.HandleOffer(offer);
    }

    private void HandleWorldChunk(Packet packet)
    {
        if (packet is WorldChunkPacket chunk)
            _worldTransfer?.HandleChunk(chunk);
    }

    private void HandleWorldEnd(Packet packet)
    {
        if (packet is WorldEndPacket end)
            _worldTransfer?.HandleEnd(end);
    }

    private void HandlePlayerList(Packet packet)
    {
        if (packet is PlayerListPacket list)
        {
            ModLogger.Msg($"[NetworkManager] Player list: {list.Players.Length} players");
            foreach (var p in list.Players)
                ModLogger.Msg($"  - {p.Name} (ID: {p.Id})");
        }
    }

    private void HandlePlayerListEvent(PlayerListPacket list)
    {
        ModLogger.Msg($"[NetworkManager] Received player list: {list.Players.Length} players");
        // A list with no id 0 among the players means we are on a dedicated
        // server (no in-game host); the authority election reads this list.
        _playerListReceived = true;
        var hostPresent = false;
        foreach (var p in list.Players)
        {
            ModLogger.Msg($"  - {p.Name} (ID: {p.Id})");
            _playerNames[p.Id] = p.Name;
            if (p.Id == 0) hostPresent = true;
            if (p.Id != LocalPlayerId && !_remotePlayers.ContainsKey(p.Id))
            {
                var obj = AddRemotePlayer(p.Id, p.Name, Vector3.zero, Quaternion.identity);
                _remotePlayers[p.Id] = obj;
                if (!_connectedPlayers.Contains(p.Id))
                    _connectedPlayers.Add(p.Id);
            }
        }
        if (!IsHost && !hostPresent)
            ModLogger.Msg("Session", $"Dedicated server — IsTimeAuthority={(IsTimeAuthority ? "THIS client" : "another client")}");
    }

    private void HandleHeartbeat(HeartbeatPacket hb)
    {
        // Send heartbeat ack to keep connection alive. Deliberately
        // UNRELIABLE: the next heartbeat (5s) covers a lost ack, and acking
        // a periodic keepalive with ack/resend would be pure overhead.
        var ack = new HeartbeatAckPacket
        {
            ClientId = LocalPlayerId,
            Timestamp = System.DateTime.UtcNow.ToBinary()
        };
        _network.Send(ack);
    }

    private void TriggerPlayerJoined(int playerId, string playerName)
    {
        TriggerPlayerJoinedWithPos(playerId, playerName, Vector3.zero, Quaternion.identity);
    }

    private void TriggerPlayerJoinedWithPos(int playerId, string playerName, Vector3 spawnPos, Quaternion spawnRot)
    {
        ModLogger.Msg($"[NetworkManager] Player joined: {playerName} (ID: {playerId}) at {spawnPos}");
        _playerNames[playerId] = playerName;
        _chatManager.OnPlayerJoined(playerId, playerName);

        // Don't create visual for self
        if (playerId == LocalPlayerId) return;

        // Guard against duplicate creation (race between PlayerJoined event and PlayerList event)
        if (_remotePlayers.ContainsKey(playerId)) return;

        var obj = AddRemotePlayer(playerId, playerName, spawnPos, spawnRot);
        _remotePlayers[playerId] = obj;
        _connectedPlayers.Add(playerId);

        // Every machine re-announces its active light items (torch lit before
        // the new player joined would otherwise stay invisible to them)
        Patches.ItemActive_Patch.RebroadcastActive();

        // Send the new player our world state (movables, doors, switches,
        // builds, barricades, locks) so their world converges to ours. Gated on
        // the elected authority, NOT IsHost: on a DEDICATED server nobody is
        // IsHost, so this used to never fire - a barricade built while a partner
        // was offline stayed invisible to them on join (only a later live
        // dismantle revealed it). IsTimeAuthority is the lowest-id client (== the
        // host when there is an in-game host), so exactly one machine sends it.
        // Reject join mid-dream (Horde DreamSession.ShouldRejectNewConnections)
        if (DreamSession.ShouldRejectNewConnections && IsTimeAuthority)
        {
            ModLogger.Warning($"[DreamSession] Player {playerId} joined during dream — story may desync until dream ends");
            _chatManager?.AddSystemMessage("A player joined mid-dream — expect possible story desync.");
        }

        if (IsTimeAuthority)
        {
            EnqueueWorldSnapshot(playerId);
            // Seeds are shared world config — Broadcast (not join-only) is fine.
            SendWorldSeed();
            SendAuthoritySeed();
        }
    }

    /// <summary>Announce our world seed so both sides can detect mismatched worlds.</summary>
    private void SendWorldSeed()
    {
        var seed = ModConfig.Load().WorldSeed;
        _network.Broadcast(new WorldSeedPacket
        {
            PlayerId = LocalPlayerId,
            Seed = seed
        }, reliable: true);
    }

    /// <summary>
    /// Authority-only: publish the seed that drives ongoing deterministic rolls
    /// so every client adopts it (keeps daily random objects / weather in sync,
    /// including on the world-download path where configured seeds may differ or
    /// be unset). Mints + arms a seed if none is configured.
    /// </summary>
    private void SendAuthoritySeed()
    {
        var seed = Patches.WorldGenSeed_Patch.GetPublishSeed();
        if (seed == 0) return;
        _network.Broadcast(new WorldSeedAuthPacket
        {
            PlayerId = LocalPlayerId,
            Seed = seed
        }, reliable: true);
    }

    /// <summary>
    /// Queue join bulk for one player only (Horde join discipline).
    /// Drain path uses <see cref="NetworkLayer.SendToPlayer"/>.
    /// </summary>
    private void EnqueueWorldSnapshot(int targetPlayerId)
    {
        var packets = new List<Packet>();
        try
        {
            JoinSnapshotRegistry.CollectAll(packets, targetPlayerId);
        }
        catch (Exception ex)
        {
            ModLogger.Error("Join", $"Snapshot collection failed: {ex.Message}");
        }
        foreach (var p in packets)
        {
            _snapshotQueue.Enqueue(new SnapshotItem
            {
                TargetPlayerId = targetPlayerId,
                Packet = p
            });
        }
        ModLogger.Msg("Join", $"Queued {packets.Count} snapshot packets for player {targetPlayerId}");
    }

    private void TriggerPlayerLeft(int playerId, string playerName)
    {
        ModLogger.Msg($"[NetworkManager] Player left: {playerName} (ID: {playerId})");
        _chatManager.OnPlayerLeft(playerId, playerName);

        if (_remotePlayers.TryGetValue(playerId, out var obj))
        {
            Destroy(obj);
            _remotePlayers.Remove(playerId);
        }
        _connectedPlayers.Remove(playerId);
        _playerNames.Remove(playerId);
        _interactionLock?.OnPlayerLeft(playerId);
        _playerSync?.OnRemotePlayerRemoved(playerId);
        _playerAnimSync?.OnRemotePlayerRemoved(playerId);
        _heldLightSync?.OnRemotePlayerRemoved(playerId);
        PlayerPositionRegistry.Remove(playerId);
        DeathStateTracker.OnRemoteDisconnected(playerId);
        FinalDreamsceneManager.OnRemoteDisconnected(playerId);
        // If we were holding night morning for them, maybe we're all "dead" now
        Patches.Death_Patch.TryBroadcastMorningIfAllDead();
    }

    public GameObject AddRemotePlayer(int playerId, string playerName, Vector3 position, Quaternion rotation = default)
    {
        if (_remotePlayers.ContainsKey(playerId))
            return _remotePlayers[playerId];

        rotation = rotation == default ? Quaternion.identity : rotation;

        // Find the actual player model: Darkwood's player class is `Player`.
        // Skip existing remote-player clones (their Player component is disabled)
        // so we never clone a clone.
        GameObject sourceModel = null;
        var playerType = DarkwoodMP.Patches.GameTypes.GetType("Player");
        if (playerType != null)
        {
            foreach (var obj in UnityEngine.Object.FindObjectsOfType(playerType))
            {
                if (obj is not Behaviour behaviour || !behaviour.enabled) continue;
                if (behaviour.gameObject.name.StartsWith("RemotePlayer_")) continue;
                sourceModel = behaviour.gameObject;
                break;
            }
        }

        // Fallback: common player GameObject names
        if (sourceModel == null)
        {
            var playerNames = new[] { "MainPlayer", "Player", "Hero", "Character", "PlayerModel", "PlayerBody" };
            foreach (var name in playerNames)
            {
                var playerObj = GameObject.Find(name);
                if (playerObj != null && playerObj.GetComponentInChildren<Renderer>() != null)
                {
                    sourceModel = playerObj;
                    break;
                }
            }
        }

        if (sourceModel != null)
        {
            var clone = GameObject.Instantiate(sourceModel, position, rotation);
            clone.name = $"RemotePlayer_{playerId}_{playerName}";
            clone.transform.position = position;
            clone.transform.rotation = rotation;

            // Disable all game logic on the clone so it doesn't react to input,
            // regenerate health, play audio etc. Keep tk2d sprite components so
            // the model still renders (Darkwood is a 2D Toolkit game).
            foreach (var behaviour in clone.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (behaviour == null) continue;
                var type = behaviour.GetType();
                var asmName = type.Assembly.GetName().Name;
                var isGameScript = asmName == "Assembly-CSharp" || asmName == "Assembly-CSharp-firstpass";
                if (isGameScript && !type.Name.StartsWith("tk2d"))
                    behaviour.enabled = false;
            }
            foreach (var cam in clone.GetComponentsInChildren<Camera>(true))
                cam.enabled = false;
            foreach (var listener in clone.GetComponentsInChildren<AudioListener>(true))
                listener.enabled = false;
            foreach (var audio in clone.GetComponentsInChildren<AudioSource>(true))
                audio.enabled = false;

            // v0.5.1: strip the player's own Light2D objects from the clone.
            // Darkwood's vision cone IS a Light2D with a runtime-generated
            // shadow mesh - with its script disabled, the stale mesh kept
            // RENDERING and the observer saw a glitchy copy of the partner's
            // FOV glued to the clone. Remote lights are managed explicitly by
            // HeldLightSync, so the clone must carry none of its own.
            foreach (var light in clone.GetComponentsInChildren<Light2D>(true))
            {
                if (light == null) continue;
                if (light.transform == clone.transform)
                    Destroy(light);
                else
                    Destroy(light.gameObject);
            }
            // Safety net for light/vision helper meshes that live NEXT to the
            // Light2D (never touch tk2d sprite objects - that is the body)
            foreach (var tr in clone.GetComponentsInChildren<Transform>(true))
            {
                if (tr == null || tr == clone.transform) continue;
                var lower = tr.name.ToLowerInvariant();
                if (!lower.Contains("fov") && !lower.Contains("vision") && !lower.Contains("lightmesh")) continue;
                if (tr.GetComponent<tk2dBaseSprite>() != null || tr.GetComponent<tk2dSpriteAnimator>() != null) continue;
                Destroy(tr.gameObject);
            }

            // Apply very subtle player tint to all MeshRenderers and SkinnedMeshRenderers
            var baseColor = GetPlayerColor(playerId);
            var tint = new Color(
                Mathf.Lerp(0.95f, baseColor.r, 0.08f),
                Mathf.Lerp(0.95f, baseColor.g, 0.08f),
                Mathf.Lerp(0.95f, baseColor.b, 0.08f)
            );

            // Set color on all MeshRenderer materials
            foreach (var mr in clone.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (mr.sharedMaterial != null)
                {
                    var mrMat = InstantiateMaterial(mr.sharedMaterial);
                    mrMat.color = tint;
                    mr.sharedMaterial = mrMat;
                }
            }

            // Set color on all SkinnedMeshRenderers
            foreach (var smr in clone.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr.sharedMaterial != null)
                {
                    var smrMat = InstantiateMaterial(smr.sharedMaterial);
                    smrMat.color = tint;
                    smr.sharedMaterial = smrMat;
                }
            }

            AttachRemoteProxy(clone, playerId, playerName, position);
            UnityEngine.Object.DontDestroyOnLoad(clone);
            ModLogger.Msg($"[NetworkManager] Created remote player {playerName} (ID: {playerId}) from source '{sourceModel.name}' at {position}");
            return clone;
        }

        // Fallback: create capsule mesh
        var capsuleObj = new GameObject($"RemotePlayer_{playerId}_{playerName}");
        capsuleObj.transform.position = position;
        capsuleObj.transform.rotation = rotation;
        UnityEngine.Object.DontDestroyOnLoad(capsuleObj);

        var meshFilter = capsuleObj.AddComponent<MeshFilter>();
        meshFilter.sharedMesh = CreatePlayerCapsuleMesh();

        var meshRenderer = capsuleObj.AddComponent<MeshRenderer>();
        var capsuleMat = new Material(Shader.Find("Unlit/Color") ?? Shader.Find("Sprites/Default") ?? Shader.Find("Legacy Shaders/Diffuse"));
        var pc = GetPlayerColor(playerId);
        capsuleMat.color = new Color(
            Mathf.Lerp(0.95f, pc.r, 0.08f),
            Mathf.Lerp(0.95f, pc.g, 0.08f),
            Mathf.Lerp(0.95f, pc.b, 0.08f)
        );
        meshRenderer.material = capsuleMat;

        var visual = capsuleObj.AddComponent<PlayerVisual>();
        visual.Initialize(playerId, playerName, capsuleMat.color);
        AttachRemoteProxy(capsuleObj, playerId, playerName, position);

        ModLogger.Msg($"[NetworkManager] Created remote player {playerName} (ID: {playerId}) at {position} (fallback capsule)");
        return capsuleObj;
    }

    /// <summary>Horde-style proxy identity on every remote visual.</summary>
    private static void AttachRemoteProxy(GameObject go, int playerId, string playerName, Vector3 position)
    {
        if (go == null) return;
        var proxy = go.GetComponent<RemotePlayerProxy>() ?? go.AddComponent<RemotePlayerProxy>();
        proxy.PlayerId = playerId;
        proxy.DisplayName = playerName ?? $"Player_{playerId}";
        proxy.FreezePosition = false;
        proxy.ApplyNetworkHint(position, 0f);
    }

    private static Material InstantiateMaterial(Material source)
    {
        var newMat = UnityEngine.Object.Instantiate(source);
        return newMat;
    }

    public GameObject GetRemotePlayer(int playerId)
    {
        return _remotePlayers.TryGetValue(playerId, out var obj) ? obj : null;
    }

    private float _lastRemoteUpgradeCheck;

    /// <summary>
    /// Remote players created before the local world existed (e.g. a client that
    /// connected from the main menu, before its downloaded world loaded) fall
    /// back to a capsule + healthbar because there was no player model to clone.
    /// Once we ARE in a game, re-create those as real model clones - otherwise
    /// the partner is only ever a floating healthbar on that machine.
    /// </summary>
    private void MaybeUpgradeRemotePlayers()
    {
        if (_remotePlayers.Count == 0) return;
        if (Time.time - _lastRemoteUpgradeCheck < 1f) return;
        _lastRemoteUpgradeCheck = Time.time;

        // Need a real local player model to clone from.
        if (_playerSync?.LocalPlayerTransform == null) return;

        List<int> toUpgrade = null;
        foreach (var kvp in _remotePlayers)
        {
            // Capsule-fallback visuals carry a PlayerVisual component; clones don't.
            if (kvp.Value != null && kvp.Value.GetComponent<PlayerVisual>() != null)
                (toUpgrade ??= new List<int>()).Add(kvp.Key);
        }
        if (toUpgrade == null) return;

        foreach (var id in toUpgrade)
        {
            var old = _remotePlayers[id];
            var pos = old != null ? old.transform.position : Vector3.zero;
            var rot = old != null ? old.transform.rotation : Quaternion.identity;
            var name = _playerNames.TryGetValue(id, out var n) ? n : $"Player_{id}";

            if (old != null) Destroy(old);
            _remotePlayers.Remove(id);
            _playerAnimSync?.OnRemotePlayerRemoved(id);
            _heldLightSync?.OnRemotePlayerRemoved(id);

            _remotePlayers[id] = AddRemotePlayer(id, name, pos, rot);
            ModLogger.Msg($"[NetworkManager] Upgraded remote player {id} to a full model clone");
        }
        Patches.ItemActive_Patch.RebroadcastActive();
    }

    private static readonly Color[] _playerColors =
    {
        Color.green, Color.cyan, Color.yellow, Color.magenta,
        Color.white, Color.gray, new Color(0.8f, 0.4f, 0.8f), new Color(0.8f, 0.6f, 0.2f)
    };

    private static Color GetPlayerColor(int playerId)
    {
        return _playerColors[Mathf.Abs(playerId) % _playerColors.Length];
    }

    private static Mesh CreatePlayerCapsuleMesh()
    {
        var mesh = new Mesh();
        mesh.name = "PlayerCapsule";

        var vertices = new[]
        {
            // Body
            new Vector3(-0.4f, -1f, 0f), new Vector3(0f, -1f, 0.4f), new Vector3(0.4f, -1f, 0f), new Vector3(0f, -1f, -0.4f),
            // Top of cylinder
            new Vector3(-0.4f, 1f, 0f), new Vector3(0f, 1f, 0.4f), new Vector3(0.4f, 1f, 0f), new Vector3(0f, 1f, -0.4f),
            // Hemisphere top
            new Vector3(0f, 1.4f, 0f),
            // Hemisphere bottom
            new Vector3(0f, -1.4f, 0f),
        };

        var triangles = new int[]
        {
            // Body sides
            0, 1, 2, 2, 1, 3,    // front
            2, 3, 6, 6, 3, 7,    // right
            6, 7, 4, 4, 7, 5,    // back
            4, 5, 0, 0, 5, 1,    // left
            // Body top
            4, 5, 6, 4, 6, 7,
            // Body bottom
            0, 3, 1, 0, 2, 3,
            // Hemisphere top
            8, 4, 5, 8, 5, 6, 8, 6, 7, 8, 7, 4,
            // Hemisphere bottom
            9, 0, 3, 9, 3, 1, 9, 1, 2, 9, 2, 0,
        };

        mesh.vertices = vertices;
        mesh.triangles = triangles;
        mesh.RecalculateNormals();
        return mesh;
    }

    public void Disconnect()
    {
        ModLogger.Msg("[NetworkManager] Disconnecting...");
        IsConnected = false;
        IsHost = false;
        LocalPlayerId = -1;
        _playerListReceived = false;
        _connectedPlayers.Clear();
        _playerNames.Clear();

        // Clean up remote players
        foreach (var kvp in _remotePlayers)
            Destroy(kvp.Value);
        _remotePlayers.Clear();

        // Horde NetworkResetRegistry: all module session maps (try/catch per action)
        NetworkResetRegistry.ResetAll();
        NetworkApplyGuard.ResetDepth();

        _patchRegistry.RemovePatches();
        _network.Disconnect();
        ModLogger.Msg("[NetworkManager] Disconnected");
    }

}
