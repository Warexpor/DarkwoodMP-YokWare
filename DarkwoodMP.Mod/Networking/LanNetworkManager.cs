using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
// IEnumerable for GetHandshakedPeerIds
using DWMPHorde;
using DWMPHorde.Audio;
using DWMPHorde.Config;
using DWMPHorde.Logging;
using DWMPHorde.Patches;
using DWMPHorde.Players;
using DWMPHorde.Sync;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Networking
{
    public sealed partial class LanNetworkManager : MonoBehaviour, INetEventListener
    {
        public const float SendInterval = 0.033f;

        public static LanNetworkManager Instance { get; private set; }

        private NetManager _net;
        private readonly Dictionary<int, NetPeer> _peers = new Dictionary<int, NetPeer>();
        private NetworkRole _role = NetworkRole.Offline;
        private readonly Dictionary<int, RemotePlayerProxy> _remoteProxies = new Dictionary<int, RemotePlayerProxy>();

        internal Dictionary<int, RemotePlayerProxy> RemoteProxies => _remoteProxies;
        private WorldSyncService _worldSync;

        internal WorldSyncService WorldSync => _worldSync;

        internal int PhysicsRecvLogCounter
        {
            get => _physicsRecvLogCounter;
            set => _physicsRecvLogCounter = value;
        }

        internal Dictionary<short, ShadowCreature> ShadowTracked => _shadowTracked;
        internal short NextShadowId
        {
            get => _nextShadowId;
            set => _nextShadowId = value;
        }
        private WorldSaveShareService _worldSaveShare;
        private float _sendTimer;
        private uint _nextPlayerStateSequence;
        internal readonly Dictionary<int, uint> _lastPlayerStateSequence = new Dictionary<int, uint>();
        internal Dictionary<int, uint> LastPhysicsStateSequence => _lastPhysicsStateSequence;
        internal readonly Dictionary<int, uint> _lastPhysicsStateSequence = new Dictionary<int, uint>();
        internal Dictionary<int, uint> LastReliablePhysicsStateSequence => _lastReliablePhysicsStateSequence;
        internal readonly Dictionary<int, uint> _lastReliablePhysicsStateSequence = new Dictionary<int, uint>();
        private float _proxyAggroTimer;
        private float _effectSyncTimer;
        private Vector3 _lastSentPosition;
        internal bool _wasDragging;
        internal string _lastDraggedItemName;

        internal bool WasDragging { get => _wasDragging; set => _wasDragging = value; }
        internal string LastDraggedItemName { get => _lastDraggedItemName; set => _lastDraggedItemName = value; }
        internal Dictionary<int, uint> LastPlayerStateSequence => _lastPlayerStateSequence;
        internal Dictionary<string, Vector3> LastDragSyncPos => PlayerInteractHandlers.LastDragSyncPos;
        internal Dictionary<string, float> DragEndedAt => PlayerInteractHandlers.DragEndedAt;
        internal static HashSet<string> ConsumedDropGuids => _consumedDropGuids;
        /// <summary>Local E-drag scrape intent (player walking). False → reliable quiet stop for peers.</summary>
        private bool _dragScrapeActive;
        private float _dragScrapeQuietSince = -1f;
        /// <summary>Matches body-push <see cref="ItemMovingSoundHelper"/> local stop speed.</summary>
        private const float DragScrapeStopSpeed = 1f;
        private const float DragScrapeStopGrace = 0.05f;

        /// <summary>
        /// Local side is ready to exchange gameplay traffic.
        /// Host: true once at least one peer has completed handshake (never cleared when more peers join).
        /// Client: true after receiving host handshake.
        /// </summary>
        private bool _handshakeComplete;

        internal bool HandshakeComplete
        {
            get => _handshakeComplete;
            set => _handshakeComplete = value;
        }

        internal bool AcceptSnapshotSequence(
            Dictionary<int, uint> lastBySender,
            int senderId,
            uint sequence,
            string snapshotKind)
        {
            if (senderId <= 0 || sequence == 0)
            {
                ModLog.Warn(LogCat.Network,
                    snapshotKind + " snapshot rejected: invalid sender/sequence sender="
                    + senderId + " seq=" + sequence);
                return false;
            }

            if (lastBySender.TryGetValue(senderId, out uint last)
                && !SnapshotSequencePolicy.IsNewer(sequence, last, true))
            {
                if (ModRuntime.VerboseLogging)
                    ModRuntime.LegacyInfo(
                        "[" + snapshotKind + "] stale snapshot rejected sender="
                        + senderId + " seq=" + sequence + " last=" + last);
                return false;
            }

            lastBySender[senderId] = sequence;
            return true;
        }

        /// <summary>
        /// Per-peer handshake tracking on the host. Prevents a newly joining peer from
        /// freezing gameplay traffic for peers that are already ready.
        /// </summary>
        internal HashSet<int> HandshakedPeers => _handshakedPeers;
        internal readonly HashSet<int> _handshakedPeers = new HashSet<int>();

        private int _nextPlayerId = 2;
        private int _localPlayerId = 1; // Host is always player 1

        // Per-player state tracking (consolidated)
        private readonly Dictionary<int, RemotePlayerState> _remotePlayers = new Dictionary<int, RemotePlayerState>();

        internal Dictionary<int, RemotePlayerState> RemotePlayers => _remotePlayers;
        private bool _previousInOutsideLocation;
        private string _previousLocationName = "";
        private int _locationSyncCounter;

        internal bool PreviousInOutsideLocation
        {
            get => _previousInOutsideLocation;
            set => _previousInOutsideLocation = value;
        }
        internal string PreviousLocationName
        {
            get => _previousLocationName;
            set => _previousLocationName = value;
        }
        internal int LocationSyncCounter
        {
            get => _locationSyncCounter;
            set => _locationSyncCounter = value;
        }
        /// <summary>PlayerLightState arrived before proxy existed (phase-3 / early handshake).</summary>
        internal Dictionary<int, PlayerLightStateMessage> PendingPlayerLights =>
            PlayerLightFxHandlers.PendingPlayerLights;

        private int _nextThrowId = 1;

        /// <summary>
        /// Peers awaiting late-join bulk. Value = realtime of first in-world PlayerState
        /// (0 = share done, not seen in-world yet). Bulk after ClientBulkSettleSeconds.
        /// </summary>
        private readonly Dictionary<int, float> _awaitingLateJoinBulk = new Dictionary<int, float>();

        /// <summary>
        /// Heavy sticky-world bulk after the light dump (FindObjects scans). Value = next phase index.
        /// One phase per peer per frame so host join frame does not freeze.
        /// </summary>
        private readonly Dictionary<int, int> _pendingHeavyLateJoinBulk = new Dictionary<int, int>();
        private const int HeavyLateJoinPhaseCount = 12; // weather through fired GameEvents bulk

        /// <summary>Title-join: wait after first PlayerState before bulk (avoids half-loaded apply).</summary>
        private const float ClientBulkSettleSeconds = 8f;
        /// <summary>Phase-3 reconnect: client finished offline load, so use a short settle.</summary>
        private const float CoopReconnectBulkSettleSeconds = 1.5f;

        /// <summary>
        /// Host: peers mid world-download or LoadScene. Gameplay traffic is held
        /// while the client loads a save and may stop polling network events.
        /// Cleared on first in-world PlayerState or disconnect.
        /// </summary>
        private readonly HashSet<int> _peersLoadingWorld = new HashSet<int>();

        /// <summary>
        /// Host: peers that reconnected with AlreadyInWorld during phase 3.
        /// These peers use a shorter late-join bulk settle.
        /// </summary>
        private readonly HashSet<int> _peersCoopReconnect = new HashSet<int>();

        /// <summary>
        /// Host: last observed HostHasShareableWorld for rising-edge auto-share to title clients.
        /// </summary>
        private bool _hostWasShareableForWaitingClients;


        /// <summary>Remote peer OutsideLocation membership (location sync / late-join).</summary>
        private readonly Dictionary<int, string> _remoteOutsideLocation = new Dictionary<int, string>();

        internal Dictionary<int, string> RemoteOutsideLocation => _remoteOutsideLocation;


        public NetworkRole Role => _role;
        public bool IsConnected => PeerCount > 0;
        public int ConnectedPlayerCount => PeerCount;
        public int LocalPlayerId => _localPlayerId;

        internal void AssignCurrentReceivePlayerId(int playerId) => _currentReceivePlayerId = playerId;
        public string StatusText { get; internal set; } = "Offline";
        /// <summary>Snapshot of connected peer player-ids (LAN or Steam). Safe to mutate after call.</summary>
        public IReadOnlyCollection<int> ConnectedPlayerIds
        {
            get
            {
                var list = new List<int>(PeerCount);
                foreach (int id in EnumeratePeerIds())
                    list.Add(id);
                return list;
            }
        }

        /// <summary>Handshaked peer IDs used for session and night-death accounting.</summary>
        public IEnumerable<int> GetHandshakedPeerIds() => _handshakedPeers;

        /// <summary>
        /// True while applying inbound/remote state. ORs <see cref="NetworkApplyGuard.IsActive"/>
        /// so nested <c>IsApplyingRemoteState = false</c> cannot clear the flag mid-guard
        /// (same pattern as <see cref="Sync.TraverseHack.ApplyingFromNetwork"/>).
        /// </summary>
        public static bool IsApplyingRemoteState
        {
            get => _explicitApplyingRemoteState || NetworkApplyGuard.IsActive;
            internal set => _explicitApplyingRemoteState = value;
        }

        private static bool _explicitApplyingRemoteState;

        internal static bool GetExplicitApplyingRemoteState() => _explicitApplyingRemoteState;

        internal static void SetExplicitApplyingRemoteState(bool value) =>
            _explicitApplyingRemoteState = value;

        /// <summary>
        /// Returns the RemotePlayerState for the given playerId, creating one if needed.
        /// </summary>
        internal RemotePlayerState GetOrCreateState(int playerId)
        {
            if (!_remotePlayers.TryGetValue(playerId, out var state))
            {
                state = new RemotePlayerState { PlayerId = playerId };
                _remotePlayers[playerId] = state;
            }
            return state;
        }

        /// <summary>Lookup remote presentation state without creating.</summary>
        internal bool TryGetRemoteState(int playerId, out RemotePlayerState state)
            => _remotePlayers.TryGetValue(playerId, out state);

        /// <summary>True if this player id completed handshake with us.</summary>
        internal bool IsHandshakedPeer(int playerId)
            => playerId > 0 && _handshakedPeers.Contains(playerId);

        internal void RecordPendingContainerRemove(Vector3 pos, int slotIdx) =>
            ContainerHandlers.RecordPendingContainerRemove(pos, slotIdx);

        internal void RecordPendingTakePreCount(Vector3 pos, int slotIdx, int preCount) =>
            ContainerHandlers.RecordPendingTakePreCount(pos, slotIdx, preCount);

        internal void ClearPendingTakePreCount(Vector3 pos, int slotIdx) =>
            ContainerHandlers.ClearPendingTakePreCount(pos, slotIdx);

        /// <summary>Thin forward: drag claim maps live on <see cref="PlayerInteractNetHandlers"/>.</summary>
        internal Dictionary<string, int> _dragClaims => PlayerInteractHandlers.DragClaims;

        internal bool IsDragClaimedByOther(string objectName, int localPlayerId) =>
            PlayerInteractHandlers.IsDragClaimedByOther(objectName, localPlayerId);

        /// <summary>True while processing an incoming BarricadeEventMessage.
        /// Suppresses Postfix re-broadcast to prevent loops (unlike the broader
        /// IsApplyingRemoteState which also blocks legitimate HandleMeleeWorldHit
        /// feedback).</summary>
        internal static bool _processingBarricadeEvent;

        // body-push/drag sounds now use native ItemSounds via Rigidbody velocity
        /// <summary>Thin forward: remote drag ids live on <see cref="PlayerInteractNetHandlers"/>.</summary>
        internal HashSet<int> _remoteDragItemIds => PlayerInteractHandlers.RemoteDragItemIds;
        internal HashSet<string> _remoteDragItemNames => PlayerInteractHandlers.RemoteDragItemNames;
        internal Dictionary<string, Vector3> _lastDragSyncPos => PlayerInteractHandlers.LastDragSyncPos;
        internal Dictionary<string, float> _dragEndedAt => PlayerInteractHandlers.DragEndedAt;
        internal const float DragStopStaleGraceConst = PlayerInteractNetHandlers.DragStopStaleGraceConst;

        /// <summary>True while performing a save triggered by the remote peer.</summary>
        internal static bool _isRemoteSaveInProgress;

        /// <summary>
        /// Host: set when a take or place loses a race, so the payload is not
        /// forwarded.
        /// </summary>
        internal bool _suppressForwardThisMessage;


        public event Action Connected;
        public event Action Disconnected;

        /// <summary>One-shot host→client new-world save transfer.</summary>
        public WorldSaveShareService WorldSaveShare => _worldSaveShare;

        /// <summary>True when local side has finished protocol handshake with at least one peer.</summary>
        public bool IsHandshakeComplete => _handshakeComplete;

        private void Awake()
        {
            Instance = this;
            _worldSync = new WorldSyncService();
            _worldSaveShare = new WorldSaveShareService(this);
            CombatDeathBagHandlers = new CombatDeathBagNetHandlers(this);
            CombatAttackHandlers = new CombatAttackNetHandlers(this);
            CombatDeathStateHandlers = new CombatDeathStateNetHandlers(this);
            CombatHandlers = new CombatNetHandlers(
                CombatDeathBagHandlers, CombatAttackHandlers, CombatDeathStateHandlers);
            DreamHandlers = new DreamNetHandlers(this);
            EpilogueHandlers = new EpilogueNetHandlers(this);
            ContainerPendingHandlers = new ContainerPendingNetHandlers(this);
            ContainerDeathDropHandlers = new ContainerDeathDropNetHandlers(this);
            ContainerLootHandlers = new ContainerLootNetHandlers(this, ContainerPendingHandlers);
            ContainerHandlers = new ContainerNetHandlers(
                ContainerLootHandlers, ContainerDeathDropHandlers, ContainerPendingHandlers);
            DialogOutcomeApplyHandlers = new DialogOutcomeApplyNetHandlers(this);
            DialogOutcomeCloseHandlers = new DialogOutcomeCloseNetHandlers(DialogOutcomeApplyHandlers);
            DialogOutcomeApplyHandlers.BindClose(DialogOutcomeCloseHandlers);
            DialogOutcomeHandlers = new DialogOutcomeNetHandlers(
                DialogOutcomeApplyHandlers, DialogOutcomeCloseHandlers);
            DialogNpcLockHandlers = new DialogNpcLockNetHandlers(this);
            MapHandlers = new MapNetHandlers(this);
            ExaminableHandlers = new ExaminableNetHandlers(this);
            TradeHandlers = new TradeNetHandlers(this);
            DoorHandlers = new DoorNetHandlers(this);
            LockHandlers = new LockNetHandlers(this);
            BarricadeHandlers = new BarricadeNetHandlers(this);
            CursorActionHandlers = new CursorActionNetHandlers(this);
            StationHandlers = new StationNetHandlers(this);
            ChainHandlers = new ChainNetHandlers(this);
            ShadowArmorHandlers = new ShadowArmorNetHandlers(this);
            WorldBurnHandlers = new WorldBurnNetHandlers(this);
            NightHandlers = new NightNetHandlers(this);
            FlagHandlers = new FlagNetHandlers(this);
            PlayerStateHandlers = new PlayerStateNetHandlers(this);
            PlayerHeldLightPackHandlers = new PlayerHeldLightPackNetHandlers(this);
            PlayerHeldLightApplyHandlers = new PlayerHeldLightApplyNetHandlers(this);
            PlayerHeldLightHandlers = new PlayerHeldLightNetHandlers(
                PlayerHeldLightPackHandlers, PlayerHeldLightApplyHandlers);
            PlayerPresenceHandlers = new PlayerPresenceNetHandlers(this);
            PlayerInteractHandlers = new PlayerInteractNetHandlers(this);
            PlayerFXHandlers = new PlayerFXNetHandlers(this);
            JournalHandlers = new JournalNetHandlers(this);
            ChapterHandlers = new ChapterNetHandlers(this);
            CutsceneHandlers = new CutsceneNetHandlers(this);
            GameEventHandlers = new GameEventNetHandlers(this);
            LocationEnterExitHandlers = new LocationEnterExitNetHandlers(this);
            LocationEntityTrapHandlers = new LocationEntityTrapNetHandlers(this);
            LocationHandlers = new LocationNetHandlers(
                LocationEnterExitHandlers, LocationEntityTrapHandlers);
            WorldObjectSendHandlers = new WorldObjectSendNetHandlers(this);
            WorldSendHandlers = new WorldSendNetHandlers(this);
            WorldPhysicsHandlers = new WorldPhysicsNetHandlers(this);
            WorldWeatherTimeHandlers = new WorldWeatherTimeNetHandlers(this);
            WorldLateJoinHandlers = new WorldLateJoinNetHandlers(this);
            WorldProxyLifecycleHandlers = new WorldProxyLifecycleNetHandlers(this);
            WorldProxyEffectHandlers = new WorldProxyEffectNetHandlers(this);
            WorldProxyHandlers = new WorldProxyNetHandlers(
                WorldProxyLifecycleHandlers, WorldProxyEffectHandlers);
            WorldFxHandlers = new WorldFxNetHandlers(this);
            PlayerLightFxApplyHandlers = new PlayerLightFxApplyNetHandlers(this);
            PlayerLightFxAmbientHandlers = new PlayerLightFxAmbientNetHandlers();
            PlayerLightFxHandlers = new PlayerLightFxNetHandlers(PlayerLightFxApplyHandlers);
            CombatFxImpactHandlers = new CombatFxImpactNetHandlers(this);
            CombatFxGasBurnHandlers = new CombatFxGasBurnNetHandlers(this);
            CombatFxHandlers = new CombatFxNetHandlers(
                CombatFxImpactHandlers, CombatFxGasBurnHandlers);
            SaveHandlers = new SaveNetHandlers(this);
            BulkSyncHandlers = new BulkSyncNetHandlers(this);
            Sync.DreamAudioPlayer.Initialize();
        }

        public void StartHost(int port)
        {
            StopNetwork();
            _role = NetworkRole.Host;
            _localPlayerId = 1;
            _hostPlayerId = 1;
            _backend = ConnectionBackend.Lan;
            NoteSessionPort(port);
            _net = new NetManager(this) { UnconnectedMessagesEnabled = false, DisconnectTimeout = 30000 };
            if (!_net.Start(port))
            {
                StatusText = "Failed to bind port " + port;
                _role = NetworkRole.Offline;
                _backend = ConnectionBackend.None;
                return;
            }

            StatusText = "Hosting on port " + port;
            InvalidateLanIPv4Cache();
            string keyHint = string.IsNullOrEmpty(Config.ModConfig.HostPassword?.Value?.Trim())
                ? "open LAN"
                : "password protected";
            ModLog.Event(LogCat.Network, "Hosting on port " + port + " (" + keyHint + ")"
                + " | v" + PluginInfo.DisplayVersion + " proto=" + PluginInfo.ProtocolVersion
                + " maxPlayers=" + (Config.ModConfig.MaxPlayers?.Value ?? 8));
        }

        public void ConnectToHost(string address, int port)
        {
            // Phase-3 / migration: already in chapter with a live Player. Full StopNetwork
            // runs NetworkResetRegistry (entity interp reset + CharacterTracker scene scan)
            // then first snapshot re-purges ~60 chars → client FPS crater right after enter.
            // Soft transport tear keeps world + host entity maps; only rebuild the socket.
            bool softReconnect = false;
            try
            {
                softReconnect = !Core.mainMenu && Player.Instance != null && !Core.loadingGame;
            }
            catch { softReconnect = false; }

            if (softReconnect)
            {
                ModLog.Event(LogCat.Network,
                    "ConnectToHost soft reconnect (keep world / entity state) → " + address + ":" + port);
                StopTransportOnly("phase3 soft reconnect");
                foreach (int id in new List<int>(_remoteProxies.Keys))
                    DestroyRemoteProxy(id);
                _remoteProxies.Clear();
                _remotePlayers.Clear();
                PlayerLightFxHandlers?.ClearPendingPlayerLights();
                _handshakeComplete = false;
                _handshakedPeers.Clear();
                _awaitingLateJoinBulk.Clear();
                _pendingHeavyLateJoinBulk.Clear();
                _peersLoadingWorld.Clear();
                _peersCoopReconnect.Clear();
                _hostWasShareableForWaitingClients = false;
            }
            else
            {
                StopNetwork();
            }

            _role = NetworkRole.Client;
            _hostPlayerId = 1;
            _backend = ConnectionBackend.Lan;
            NoteSessionPort(port);
            _net = new NetManager(this) { UnconnectedMessagesEnabled = false, DisconnectTimeout = 30000 };
            _net.Start();
            string key = Config.ModConfig.GetConnectionKey();
            _peers[1] = _net.Connect(address, port, key);
            StatusText = "Connecting to " + address + ":" + port;
            // Event line is IP-redacted under Public; Trace keeps detail for Dev/Trace only.
            ModLog.Event(LogCat.Network, "Connecting to " + address + ":" + port
                + " | v" + PluginInfo.DisplayVersion + " proto=" + PluginInfo.ProtocolVersion
                + (softReconnect ? " (soft)" : ""));
            ModLog.Trace(LogCat.Network, () => "Connect target detail: " + address + ":" + port);
        }

        public void StopNetwork()
        {
            // This is an intentional teardown, not a host-crash migration.
            _suppressHostMigration = true;

            // Before tearing the wire: snapshot client exit pos/inv so next rejoin is current
            // even if they quit without a coordinated Save. Skip title/offline-load tear.
            TrySnapshotClientBackupOnExit();

            // Snapshot for public session-stop line before we wipe peers/ids
            NetworkRole wasRole = _role;
            int wasLocalId = _localPlayerId;
            int wasPeers = PeerCount;

            NetworkResetRegistry.ResetAll();
            ResetCombatSessionState();
            ResetSessionNetworkState();
            foreach (int id in new List<int>(_remoteProxies.Keys))
                DestroyRemoteProxy(id);
            _remoteProxies.Clear();
            PlayerLightFxHandlers?.ClearPendingPlayerLights();
            _wasDragging = false;
            _lastDraggedItemName = null;
            _dragScrapeActive = false;
            _dragScrapeQuietSince = -1f;
            PlayerInteractHandlers?.ClearDragSessionState();
            DWMPHorde.Audio.MovingObjectSoundService.Reset();
            _handshakeComplete = false;
            _handshakedPeers.Clear();
            // Clean up per-player light objects before clearing state
            foreach (var state in _remotePlayers.Values)
            {
                if (state.FlareLight != null)
                    DestroyRemoteFlareLight(state.PlayerId);
                if (state.ItemLight != null)
                    DestroyRemoteItemLight(state.PlayerId);
            }
            _remotePlayers.Clear();
            ShutdownSteamBackend();
            ClearAllPeerSlots();
            _sendTimer = 0f;
            _nextPlayerStateSequence = 0;
            _lastPlayerStateSequence.Clear();
            _lastPhysicsStateSequence.Clear();
            _lastReliablePhysicsStateSequence.Clear();
            _physicsSendTimer = 0f;
            _timeSyncTimer = 0f;
            _shadowBroadcastTimer = 0f;
            _effectSyncTimer = 0f;
            ResetLocalLightSendCache();
            _worldSync?.Reset();
            _worldSaveShare?.Reset();

            if (_net != null)
            {
                _net.Stop();
                _net = null;
            }

            _nextPlayerId = 2;
            _localPlayerId = 1;
            _backend = ConnectionBackend.None;
            ResetMigrationState();
            _suppressHostMigration = false;

            if (wasRole != NetworkRole.Offline)
            {
                ModLog.BannerSessionStop(wasRole.ToString(), wasLocalId, wasPeers);
                Disconnected?.Invoke();
            }

            _role = NetworkRole.Offline;
            StatusText = "Offline";
        }

        /// <summary>Mint a stable throw id (host and thrower both may call; host authoritative expire).</summary>
        public int MintThrowId()
        {
            int id = _nextThrowId++;
            if (_nextThrowId <= 0) _nextThrowId = 1;
            return id;
        }

        private void Update()
        {
            bool perf = IsConnected && _handshakeComplete
                && (_role == NetworkRole.Client || _role == NetworkRole.Host);
            ClientPerfProbe.SetActive(perf, _role);
            if (perf) ClientPerfProbe.FrameBegin();

            _net?.PollEvents();
            PollSteamBackend();
            Audio.VoiceChatService.Tick();
            if (perf) ClientPerfProbe.MarkPoll();

            if (perf) ClientPerfProbe.BeginUpdateSegment("walkie");
            Items.WalkieItem.Tick();
            if (perf) ClientPerfProbe.EndUpdateSegment();

            if (perf) ClientPerfProbe.BeginUpdateSegment("flushPending");
            // Apply join bulk/deltas that arrived before Flags existed (menu → load)
            TryFlushPendingFlags();
            TryFlushPendingJournal();
            TryFlushPendingTradeInventories();
            TryFlushPendingConstructibles();
            TryFlushPendingSawStates();
            TryFlushPendingFeederStates();
            TryFlushPendingLureStates();
            TryFlushPendingChainStates();
            TryFlushPendingShadowArmorStates();
            TryFlushPendingWorldBurnStates();
            Sync.StationSyncHelpers.FlushLureOutbox(force: false);
            TryFlushPendingBarricadeEvents();
            TryFlushPendingScenario();
            TryFlushPendingLocks();
            Sync.WorldPhysicsSyncService.TryFlushPendingLights();
            Sync.TrapNetworkId.FlushPending(
                (p, n) => Sync.WorldPhysicsSyncService.FindTrapByPos(p, n),
                (go, trig, silent) => Sync.WorldPhysicsSyncService.ApplyTrapState(go, trig, silentDisarm: silent));
            Sync.WorldPhysicsSyncService.TickThrownLightExpiry(this);
            TickClientCorpseSetup();
            if (perf)
            {
                ClientPerfProbe.SetPendingCounts(
                    StationHandlers.PendingLureCount,
                    PendingLockCount,
                    Sync.WorldPhysicsSyncService.PendingLightCount,
                    Sync.TrapNetworkId.PendingCount,
                    StationHandlers.PendingFeederCount,
                    StationHandlers.PendingSawCount,
                    LockHandlers.PendingConstructibleCount);
            }
            TickHeavyLateJoinBulk();
            TickHostWorldShareWhenReady();
            TickSaveSyncBroadcast();
            if (perf) ClientPerfProbe.EndUpdateSegment();

            if (perf) ClientPerfProbe.BeginUpdateSegment("peerRoster");
            TickPeerRosterGossip();
            TickHostMigrationRetry();
            if (perf) ClientPerfProbe.EndUpdateSegment();

            if (perf) ClientPerfProbe.BeginUpdateSegment("gameEvents");
            TryFlushPendingGameEvents();
            if (perf) ClientPerfProbe.EndUpdateSegment();

            if (perf) ClientPerfProbe.BeginUpdateSegment("meleeDebounce");
            CombatFxHandlers?.TickMeleeHitDebounceCleanup();
            if (perf) ClientPerfProbe.EndUpdateSegment();

            if (!IsConnected || !_handshakeComplete)
            {
                if (perf) ClientPerfProbe.MarkUpdateRest();
                return;
            }

            // Flush flag updates deferred by the cooldown.
            if (_role == NetworkRole.Host || _role == NetworkRole.Client)
            {
                if (perf) ClientPerfProbe.BeginUpdateSegment("flagSync");
                Patches.FlagSyncBoolPatch.TickFlush();
                Patches.FlagSyncIntPatch.TickFlush();
                if (perf) ClientPerfProbe.EndUpdateSegment();
            }

            _sendTimer += Time.deltaTime;

            // Pause entity and physics traffic while packing and sending the world share.
            bool shareBusy = _worldSaveShare != null && _worldSaveShare.IsBusy;

            // Host: broadcast entity states to clients
            if (_role == NetworkRole.Host && !shareBusy)
            {
                if (perf) ClientPerfProbe.BeginUpdateSegment("entityBroadcast");
                EntityStateBroadcastService.Tick();
                if (perf) ClientPerfProbe.EndUpdateSegment();

                _proxyAggroTimer += Time.deltaTime;
                if (_proxyAggroTimer >= 0.5f)
                {
                    _proxyAggroTimer = 0f;
                    if (perf) ClientPerfProbe.BeginUpdateSegment("proxyAggro");
                    WorldProxyHandlers.ProxyAggroCheck();
                    if (perf) ClientPerfProbe.EndUpdateSegment();
                }

                if (perf) ClientPerfProbe.BeginUpdateSegment("timeShadow");
                _timeSyncTimer += Time.deltaTime;
                if (_timeSyncTimer >= TimeSyncInterval)
                {
                    _timeSyncTimer = 0f;
                    SendTimeSync();
                }

                _shadowBroadcastTimer += Time.deltaTime;
                if (_shadowBroadcastTimer >= ShadowBroadcastInterval)
                {
                    _shadowBroadcastTimer = 0f;
                    BroadcastShadowStates();
                }
                if (perf) ClientPerfProbe.EndUpdateSegment();
            }

            // Both host and client send their local physics state:
            // - Host broadcasts to all clients (authoritative)
            // - Client sends to host so it can merge + forward
            // Skip while local player is in a dream -- dream objects don't exist
            // in the shared world and would cause phantom spawns on the other side.
            if (perf) ClientPerfProbe.BeginUpdateSegment("physTimer");
            _physicsSendTimer += Time.deltaTime;
            // Physics runs while awake; dream free bodies are also allowed.
            bool physTick = _physicsSendTimer >= PhysicsSendInterval && !shareBusy;
            if (physTick)
                _physicsSendTimer = 0f;
            if (perf) ClientPerfProbe.EndUpdateSegment();

            if (physTick)
            {
                bool clientNotReady = _role == NetworkRole.Client
                    && (Core.mainMenu || Core.loadingGame || !Core.coreStarted);
                if (!clientNotReady)
                {
                    if (perf) ClientPerfProbe.MarkUpdateRest();
                    bool built = Sync.WorldPhysicsSyncService.TryBuildWorldSnapshot(out var snap);
                    if (perf) ClientPerfProbe.MarkPhysBuild();
                    if (built)
                    {
                        if (_role == NetworkRole.Host)
                            Broadcast(NetMessageType.PhysicsState, w => snap.Serialize(w),
                                skipLoadingPeers: true);
                        else
                            Send(NetMessageType.PhysicsState, w => snap.Serialize(w));
                    }
                }
                else if (perf)
                {
                    ClientPerfProbe.MarkUpdateRest();
                }
            }
            else if (perf)
            {
                ClientPerfProbe.MarkUpdateRest();
            }

            // Host still needs light PlayerState for proxies, but not mid-share
            if (shareBusy && _role == NetworkRole.Host)
                return;

            if (_sendTimer < SendInterval)
                return;

            Player local = Player.Instance;
            if (local == null)
                return;

            // Client join: do not emit PlayerState during title / LoadScene / before core
            // is ready. The host waits for the first in-world packet before
            // sending heavy bulk.
            if (_role == NetworkRole.Client
                && (Core.mainMenu || Core.loadingGame || !Core.coreStarted))
                return;

            // Don't send position updates while dead in a dream (freezes proxy at death position)
            if (Sync.FinalDreamsceneManager.IsLocalDead)
                return;

            _sendTimer = 0f;
            Vector3 pos = local.transform.position;

            // When spectating, report original (saved) position to network so the remote
            // doesn't see the host's proxy teleport into the client and cause body-pushing
            var netPosOverride = Spectator.SpectatorModeController.Instance?.NetworkPositionOverride;
            if (netPosOverride.HasValue)
                pos = netPosOverride.Value;

            Vector3 vel = (pos - _lastSentPosition) / SendInterval;
            _lastSentPosition = pos;

            // Host + clients: periodically sync wards / poison / bleed / skill flags to peers.
            // Include host effect flags so clients can present shadow and forest
            // spirit wards.
            _effectSyncTimer += Time.deltaTime;
            if (_effectSyncTimer >= 2f)
            {
                _effectSyncTimer = 0f;
                SendPlayerEffects();
            }

            // Both sides: send own position to the other side at ~30 Hz
            string torsoClip = PlayerAnimationSnapshot.ReadTorsoClip(local);
            string legsClip = PlayerAnimationSnapshot.ReadLegsClip(local);
            // Night-dead + spectating: vanilla still plays get-up clips on the local body.
            // Force death clips so host never "revives" our proxy mid-spectate.
            if (DeathStateTracker.LocalNightDeath)
            {
                torsoClip = "Death1";
                legsClip = "Death1";
            }

            var msg = new PlayerStateMessage
            {
                PlayerId = _localPlayerId,
                Sequence = ++_nextPlayerStateSequence,
                PosX = pos.x,
                PosY = pos.y,
                PosZ = pos.z,
                VelX = vel.x,
                VelZ = vel.z,
                LocomotionState = (byte)PlayerAnimationSnapshot.ReadLocomotion(local),
                FlipX = false, // The game uses rotation for this pose.
                Running = local.running && !DeathStateTracker.LocalNightDeath,
                LegFacingY = PlayerAnimationSnapshot.ReadLegFacingY(local),
                ReverseLegs = PlayerAnimationSnapshot.ReadReverseLegs(local),
                TorsoFacingY = PlayerAnimationSnapshot.ReadTorsoFacingY(local),
                TorsoClip = torsoClip,
                LegsClip = legsClip,
                CurrentFrame = PlayerAnimationSnapshot.ReadCurrentFrame(local),
                InBearTrap = local.inBearTrap,
                HasLightProtection = local.isInLight,
                HasNightShadows = local.skills != null && local.skills.NightShadows,
                AfterNightActive = Singleton<Controller>.Instance != null && Singleton<Controller>.Instance.isAfterNight,
                TrapNetId = local.inBearTrap
                    ? Sync.TrapNetworkId.ResolveOccupyingTrapId(pos, hostMint: _role == NetworkRole.Host)
                    : 0
            };

            PackContinuousLights(ref msg, local);

            // Continue sending PlayerState to loading peers so their proxies
            // can be created before the rest of the join traffic.
            // while host muted PlayerState under skipLoadingPeers during world share / phase-3 mute.
            Broadcast(NetMessageType.PlayerState, w => msg.Serialize(w),
                skipLoadingPeers: false);

            // Detect OutsideLocation (basement/bunker) transitions
            if (Singleton<OutsideLocations>.Instance != null)
            {
                bool inOutsideLoc = Singleton<OutsideLocations>.Instance.playerInOutsideLocation;
                string locName = Singleton<OutsideLocations>.Instance.currentLocationName ?? "";

                // While inside, send LocationEnter every 30 frames (~1 Hz) so the
                // receiver can retry after an async location spawn completes.
                // Dreams: send once on enter or rename instead of every tick.
                _locationSyncCounter++;
                bool dreamLocActive = Sync.DreamSyncManager.IsDreamActive;
                bool locChanged = !_previousInOutsideLocation || locName != _previousLocationName;
                bool heartbeatRetry = !dreamLocActive && _locationSyncCounter >= 30;
                if (inOutsideLoc && (locChanged || heartbeatRetry))
                {
                    _locationSyncCounter = 0;
                    if (!string.IsNullOrEmpty(locName))
                    {
                        // Use the live dream pad name during the session, not the
                        // vanilla completed-location name.
                        string txName = dreamLocActive
                            ? Sync.DreamSyncManager.CanonicalDreamLocationName(locName)
                            : locName;
                        Broadcast(NetMessageType.LocationEnter,
                            w => new LocationEnterMessage
                            {
                                LocationName = txName,
                                PlayerId = _localPlayerId
                            }.Serialize(w),
                            DeliveryMethod.ReliableOrdered);
                        ModRuntime.LegacyInfo($"[LocationSync] sent LocationEnter: {txName} pid={_localPlayerId}");
                    }
                }

                // Exited the location (left the sub-location area)
                if (!inOutsideLoc && _previousInOutsideLocation)
                {
                    // Dream transport (dreamPrepared branch) never sets playerInOutsideLocation=true,
                    // so settle hooks set previous=true while live flag stays false → false Exit.
                    // While dreaming / EnteringDream, stay "inside" the dream pad.
                    bool stayInDreamPad = Sync.DreamSyncManager.IsDreamActive
                        || (Dreams.Instance != null && (Dreams.Instance.dreaming || Dreams.Instance.dreamPrepared))
                        || Core.EnteringDream;
                    if (stayInDreamPad)
                    {
                        inOutsideLoc = true;
                        if (string.IsNullOrEmpty(locName) && !string.IsNullOrEmpty(_previousLocationName))
                            locName = _previousLocationName;
                    }
                    else
                    {
                        Vector3 exitPos = local.transform.position;
                        Broadcast(NetMessageType.LocationExit,
                            w => new LocationExitMessage
                            {
                                PosX = exitPos.x,
                                PosY = exitPos.y,
                                PosZ = exitPos.z,
                                PlayerId = _localPlayerId
                            }.Serialize(w),
                            DeliveryMethod.ReliableOrdered);
                        ModRuntime.LegacyInfo($"[LocationSync] sent LocationExit pid={_localPlayerId} pos={exitPos}");
                    }
                }

                _previousInOutsideLocation = inOutsideLoc;
                _previousLocationName = locName;
            }

            // Host: also track own position locally for AI checks
            if (_role == NetworkRole.Host)
                PlayerPositionManager.ReportHostPosition(pos);

            // Both sides: if dragging an object, sync its position at ~30 Hz
            if (local.dragging && local.itemBeingDragged != null)
            {
                Item dragged = local.itemBeingDragged;
                _lastDraggedItemName = dragged.gameObject.name;
                // Claim this object so other players can't grab it simultaneously
                _dragClaims[_lastDraggedItemName] = _localPlayerId;
                // Keep scrape authority so host PhysicsState / DragSync echo cannot arm MOS.
                DWMPHorde.Audio.ItemMovingSoundHelper.NoteLocalPushAuthority(_lastDraggedItemName);

                // Scrape intent follows player movement, using the same gate as
                // body push rather than object position delta.
                // hinge jitter kept scrape armed for observers after the host stopped walking.
                float hSpeed = 0f;
                if (local.Rigidbody != null)
                {
                    Vector3 v = local.Rigidbody.velocity;
                    hSpeed = new Vector3(v.x, 0f, v.z).magnitude;
                }
                bool playerMoving = hSpeed >= DragScrapeStopSpeed;
                if (playerMoving)
                {
                    _dragScrapeQuietSince = -1f;
                    _dragScrapeActive = true;
                }
                else
                {
                    if (_dragScrapeQuietSince < 0f)
                        _dragScrapeQuietSince = Time.unscaledTime;
                    if (Time.unscaledTime - _dragScrapeQuietSince >= DragScrapeStopGrace)
                        _dragScrapeActive = false;
                }

                var dragMsg = new DragSyncMessage
                {
                    PosX = dragged.transform.position.x,
                    PosY = dragged.transform.position.y,
                    PosZ = dragged.transform.position.z,
                    RotX = dragged.transform.eulerAngles.x,
                    RotY = dragged.transform.eulerAngles.y,
                    RotZ = dragged.transform.eulerAngles.z,
                    IsDragging = true,
                    ObjectName = _lastDraggedItemName,
                    ItemType = dragged.invItem != null ? dragged.invItem.type : "",
                    ClaimedByPlayerId = _localPlayerId,
                    ScrapeActive = _dragScrapeActive
                };
                // Quiet scrape stop must be reliable; unreliable quiet ticks can be lost
                // observers kept the last NoteMoving loop until full release.
                var dragDelivery = _dragScrapeActive
                    ? DeliveryMethod.Unreliable
                    : DeliveryMethod.ReliableOrdered;
                Broadcast(NetMessageType.DragSync, w => dragMsg.Serialize(w), dragDelivery);
                _wasDragging = true;
            }
            else if (_wasDragging)
            {
                // Backup if stopDragging never ran (edge cases). Prefer NotifyLocalDragEnded.
                NotifyLocalDragEnded(_lastDraggedItemName);
            }
        }

        /// <summary>
        /// Intentional E-drag release, in the same frame as vanilla
        /// <c>Item.stopDragging</c>.
        /// Push stop is frame-perfect via <see cref="ItemMovingSoundHelper.TickLocalPushScrapeStop"/>;
        /// drag used to wait for the next 30 Hz PlayerState tick, so observers heard scrape longer.
        /// Reliable DragSync stop + local ForceStop; host also emits body-push stop signal so
        /// residual PhysicsState cannot re-arm MOS after claim clears.
        /// </summary>
        public void NotifyLocalDragEnded(string objectName)
        {
            if (!_wasDragging && string.IsNullOrEmpty(objectName) && string.IsNullOrEmpty(_lastDraggedItemName))
                return;

            string endedName = !string.IsNullOrEmpty(objectName) ? objectName : (_lastDraggedItemName ?? "");
            _wasDragging = false;
            _lastDraggedItemName = null;
            _dragScrapeActive = false;
            _dragScrapeQuietSince = -1f;

            if (!string.IsNullOrEmpty(endedName)
                && _dragClaims.TryGetValue(endedName, out int cid)
                && cid == _localPlayerId)
                _dragClaims.Remove(endedName);

            if (!string.IsNullOrEmpty(endedName))
                _dragEndedAt[endedName] = Time.unscaledTime;

            if (!IsConnected)
            {
                if (!string.IsNullOrEmpty(endedName))
                    DWMPHorde.Audio.ItemMovingSoundHelper.ForceStopByName(endedName);
                return;
            }

            var dragMsg = new DragSyncMessage
            {
                IsDragging = false,
                ObjectName = endedName,
                ClaimedByPlayerId = _localPlayerId
            };
            Broadcast(NetMessageType.DragSync, w => dragMsg.Serialize(w), LiteNetLib.DeliveryMethod.ReliableOrdered);

            if (!string.IsNullOrEmpty(endedName))
            {
                DWMPHorde.Audio.ItemMovingSoundHelper.ForceStopByName(endedName);
                // Release the host rigidbody hold after our own drag so peers
                // can interact with it.
                Sync.WorldPhysicsSyncService.ReleaseClientPushHoldByName(endedName);
                // Host: dual-path intentional stop (PlayerAudio IsStopSignal) so residual
                // PhysicsState after claim release cannot keep scrape armed on peers.
                if (_role == NetworkRole.Host)
                    NotifyBodyPushStopped(endedName);
            }
        }

        private void LateUpdate()
        {
            if (!IsConnected || !_handshakeComplete) return;

            bool perf = IsConnected && _handshakeComplete
                && (_role == NetworkRole.Client || _role == NetworkRole.Host);
            if (perf) ClientPerfProbe.LateBegin();

            // Both sides: interpolate world physics objects
            Sync.WorldPhysicsSyncService.UpdateObjectInterpolation();
            if (perf) ClientPerfProbe.MarkObjInterp();

            // Client only: interpolate remote entity positions for smooth movement
            if (_role == NetworkRole.Client)
            {
                ClientEntityInterpolationService.TickLateUpdate();
                if (perf) ClientPerfProbe.MarkEntityTick();
            }

            if (perf) ClientPerfProbe.LateEnd();
        }

        /// <summary>
        /// Build a complete packet with a stack-local writer so nested Send/Broadcast
        /// from writeBody callbacks cannot corrupt a shared buffer.
        /// </summary>
        private static byte[] BuildPacket(NetMessageType type, Action<NetWriter> writeBody)
        {
            var writer = new NetWriter();
            writer.Put((byte)type);
            writeBody(writer);
            return writer.CopyData();
        }

        /// <summary>Send a message to a specific peer by PlayerId (LAN or Steam).</summary>
        public void SendToPlayer(int playerId, NetMessageType type, Action<NetWriter> writeBody,
            DeliveryMethod method = DeliveryMethod.Unreliable)
        {
            byte[] data = BuildPacket(type, writeBody);
            SendRawToPlayer(playerId, data, method);
        }

        /// <summary>Raw bytes already framed with message type byte (entity snapshot path).</summary>
        public void SendRawToPlayer(int playerId, byte[] data, DeliveryMethod method = DeliveryMethod.Unreliable)
        {
            if (data == null || data.Length == 0)
                return;
            if (IsSteamSession)
            {
                SendSteamToPlayer(playerId, data, method);
                return;
            }
            if (!_peers.TryGetValue(playerId, out NetPeer peer))
                return;
            peer.Send(data, method);
        }

        /// <summary>
        /// Entity broadcast hot path: send framed packet to gameplay-ready peers without
        /// allocating ConnectedPlayerIds list each 10 Hz tick (Steam-era peer abstraction).
        /// </summary>
        public void SendRawToReadyPeers(byte[] data, DeliveryMethod method = DeliveryMethod.Unreliable)
        {
            if (data == null || data.Length == 0 || PeerCount == 0)
                return;
            foreach (int peerId in EnumeratePeerIds())
            {
                if (!IsPeerReadyForGameplay(peerId))
                    continue;
                SendRawToPlayer(peerId, data, method);
            }
        }

        /// <summary>Send a message to all connected peers.</summary>
        /// <param name="skipLoadingPeers">
        /// When true, skip peers in <see cref="_peersLoadingWorld"/> (title join / LoadScene).
        /// World share must pass false (default) so targeted broadcast resends still land.
        /// </param>
        public void SendToAll(NetMessageType type, Action<NetWriter> writeBody,
            DeliveryMethod method = DeliveryMethod.Unreliable, bool skipLoadingPeers = false)
        {
            if (PeerCount == 0) return;
            byte[] data = null;
            foreach (int peerId in EnumeratePeerIds())
            {
                if (skipLoadingPeers && _peersLoadingWorld.Contains(peerId))
                    continue;
                if (data == null)
                    data = BuildPacket(type, writeBody);
                SendRawToPlayer(peerId, data, method);
            }
        }

        /// <summary>Send a message to all peers except one.</summary>
        public void SendToAllExcept(int excludePlayerId, NetMessageType type, Action<NetWriter> writeBody,
            DeliveryMethod method = DeliveryMethod.Unreliable, bool skipLoadingPeers = false)
        {
            if (PeerCount == 0) return;
            byte[] data = null;
            foreach (int peerId in EnumeratePeerIds())
            {
                if (peerId == excludePlayerId) continue;
                if (skipLoadingPeers && _peersLoadingWorld.Contains(peerId))
                    continue;
                if (data == null)
                    data = BuildPacket(type, writeBody);
                SendRawToPlayer(peerId, data, method);
            }
        }

        /// <summary>
        /// Sends a message to all connected peers if host, or to the first peer if client.
        /// Use this in sender methods that can be called from both host and client roles.
        /// Host to clients: broadcast to all. Client to host: first peer only.
        /// </summary>
        /// <param name="skipLoadingPeers">Host only: skip joiners still loading the world package.</param>
        public void Broadcast(NetMessageType type, Action<NetWriter> writeBody,
            DeliveryMethod method = DeliveryMethod.Unreliable, bool skipLoadingPeers = false)
        {
            if (_role == NetworkRole.Host)
                SendToAll(type, writeBody, method, skipLoadingPeers);
            else
                Send(type, writeBody, method);
        }

        /// <summary>Host: joiner is downloading, applying, or loading a scene.</summary>
        public void MarkPeerLoadingWorld(int playerId)
        {
            if (_role != NetworkRole.Host || playerId <= 1)
                return;
            if (_peersLoadingWorld.Add(playerId))
                ModLog.Event(LogCat.Session, "Peer " + playerId + " marked loading-world (gameplay flood muted)");
        }

        /// <summary>Host: mark every non-host peer as loading (broadcast world resend).</summary>
        /// <param name="excludeCoopReconnect">Skip phase-3 soft reconnect peers (already in world).</param>
        public void MarkAllClientPeersLoadingWorld(bool excludeCoopReconnect = false)
        {
            if (_role != NetworkRole.Host)
                return;
            foreach (int id in EnumeratePeerIds())
            {
                if (id > 1)
                {
                    if (excludeCoopReconnect && _peersCoopReconnect.Contains(id))
                        continue;
                    MarkPeerLoadingWorld(id);
                }
            }
        }

        /// <summary>Host: peer reconnected with AlreadyInWorld (soft join pipeline phase 3).</summary>
        public bool IsCoopReconnectPeer(int playerId)
        {
            return playerId > 1 && _peersCoopReconnect.Contains(playerId);
        }

        /// <summary>Host: joiner sent its first in-world PlayerState.</summary>
        public void MarkPeerGameplayReady(int playerId)
        {
            if (_role != NetworkRole.Host || playerId <= 1)
                return;
            if (_peersLoadingWorld.Remove(playerId))
                ModLog.Event(LogCat.Session, "Peer " + playerId + " gameplay-ready (first PlayerState)");
        }

        /// <summary>Host: true if peer should receive high-rate gameplay packets.</summary>
        public bool IsPeerReadyForGameplay(int playerId)
        {
            return playerId > 0 && !_peersLoadingWorld.Contains(playerId);
        }

        /// <summary>
        /// Client handshake flag: we already materialised + entered the host world offline.
        /// Prefer playable Player; also true mid-load if reconnect raced (legacy path).
        /// Host uses this to skip a second WorldSaveShare.
        /// </summary>
        internal static bool ClientReportsAlreadyInWorld()
        {
            try
            {
                // Preferred: fully playable after offline load.
                if (Sync.ChapterSessionResume.IsLocalPlayableForCoopReconnect())
                    return true;
                // Mid LoadScene / SaveManager.Load (should be rare once resume waits for playable).
                if (Core.loadingGame || Core.loadedGame)
                    return true;
                if (!Core.mainMenu && Core.currentProfile != null)
                    return true;
                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Legacy send to first connected peer (backward compat during migration).</summary>
        public void Send(NetMessageType type, Action<NetWriter> writeBody,
            DeliveryMethod method = DeliveryMethod.Unreliable)
        {
            foreach (int peerId in EnumeratePeerIds())
            {
                SendRawToPlayer(peerId, BuildPacket(type, writeBody), method);
                return; // Send to first peer only
            }
        }

        private NetPeer _currentReceivePeer;
        private int _currentReceivePlayerId = -1;

        /// <summary>GUIDs of dropped items that have already been picked up (host-authoritative).
        /// Prevents item multiplication when both players pick up the same GUID
        /// network message is processed.</summary>
        internal static readonly HashSet<string> _consumedDropGuids = new HashSet<string>();

        private enum ForwardableKind { None, Direct, Player }

        private static readonly Dictionary<NetMessageType, ForwardableKind> _forwardableMap = BuildForwardableMap();

        private static Dictionary<NetMessageType, ForwardableKind> BuildForwardableMap()
        {
            var map = new Dictionary<NetMessageType, ForwardableKind>();
            foreach (var field in typeof(NetMessageType).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
            {
                var value = (NetMessageType)field.GetValue(null);
                if (System.Attribute.GetCustomAttribute(field, typeof(ForwardableAttribute), false) != null)
                    map[value] = ForwardableKind.Direct;
                else if (System.Attribute.GetCustomAttribute(field, typeof(ForwardablePlayerAttribute), false) != null)
                    map[value] = ForwardableKind.Player;
            }
            return map;
        }

        /// <summary>True while the current OnNetworkReceive is forwarding a
        /// RemotePlayerForwardMessage's inner payload to prevent re-forwarding.</summary>
        private bool _isForwardedMessage;

        /// <summary>Get the PlayerId for a given NetPeer, or -1 if unknown.</summary>
        private int GetPlayerId(NetPeer peer)
        {
            foreach (var kvp in _peers)
            {
                if (kvp.Value == peer)
                    return kvp.Key;
            }
            return -1;
        }

        public void OnPeerConnected(NetPeer peer)
        {
            int playerId;
            if (_role == NetworkRole.Host)
            {
                playerId = _nextPlayerId++;
                _peers[playerId] = peer;
                // Keep _handshakeComplete set when additional peers join; that
                // froze PlayerState/drag traffic for every already-ready client.
                // Only block gameplay until the first peer completes handshake.
                if (_handshakedPeers.Count == 0)
                    _handshakeComplete = false;
                StatusText = $"Player {playerId} connected";
                ModLog.Event(LogCat.Network, $"Player {playerId} connected (peers={_peers.Count}, ready={_handshakedPeers.Count})");
                CompleteHostPeerJoin(playerId);
            }
            else
            {
                _peers[1] = peer; // Host is always player 1 for client
                StatusText = "Connected to host";
                ModLog.Event(LogCat.Network, "Connected to host");
                CompleteClientPeerJoin();
            }
        }

        public void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
        {
            int playerId = GetPlayerId(peer);
            ModLog.Event(LogCat.Network, $"Player {playerId} disconnected: " + disconnectInfo.Reason);

            // Clear drag claims from the disconnected player so their objects become free
            var toRemove = new List<string>();
            foreach (var kv in _dragClaims)
            {
                if (kv.Value == playerId)
                    toRemove.Add(kv.Key);
            }
            foreach (string key in toRemove)
                _dragClaims.Remove(key);

            // Clean up drag tracking for items this player was dragging
            foreach (string key in toRemove)
            {
                RemoveRemoteDragIds(key);
                ReleaseRemoteDragKinematic(key);
                DWMPHorde.Audio.ItemMovingSoundHelper.ForceStopByName(key);
            }

            if (_role == NetworkRole.Host)
            {
                if (playerId > 0)
                {
                    // Free workbench / craft locks held by the leaver.
                    Sync.WorkbenchOpenLock.HostReleaseAllForPlayer(this, playerId);
                    _peers.Remove(playerId);
                    _handshakedPeers.Remove(playerId);
                    bool wasLoadingOnly = _peersLoadingWorld.Contains(playerId)
                        && !_peersCoopReconnect.Contains(playerId)
                        && (!_awaitingLateJoinBulk.TryGetValue(playerId, out float seen) || seen <= 0f);
                    // Phase-2 expected leave: client disconnects after share to load offline.
                    bool expectedJoinDetach = _peersLoadingWorld.Contains(playerId)
                        && !_peersCoopReconnect.Contains(playerId);

                    _awaitingLateJoinBulk.Remove(playerId); // Dictionary.Remove
                    _pendingHeavyLateJoinBulk.Remove(playerId);
                    _peersLoadingWorld.Remove(playerId);
                    _peersCoopReconnect.Remove(playerId);
                    if (_handshakedPeers.Count == 0)
                        _handshakeComplete = false;
                    DestroyRemoteProxy(playerId);
                    DestroyRemoteFlareLight(playerId);
                    DestroyRemoteItemLight(playerId);
                    _remotePlayers.Remove(playerId);
                    PlayerPositionManager.RemovePlayer(playerId);
                    _remoteOutsideLocation.Remove(playerId);
                    Sync.FinalDreamsceneManager.OnRemoteDisconnected(playerId);
                    // Don't treat transfer-link teardown / pre-PlayerState leave as night death.
                    if (!expectedJoinDetach && !wasLoadingOnly)
                    {
                        if (DeathStateTracker.OnRemoteDisconnected(playerId))
                            DeathStateTracker.TryResolveNightMorning("peer disconnect");
                    }
                    else
                    {
                        ModLog.Event(LogCat.Session,
                            "Peer " + playerId + " detached during join pipeline (expected — offline load or pre-ready)");
                    }
                    StatusText = $"Player {playerId} left ({_peers.Count} remaining, ready={_handshakedPeers.Count})";
                }
            }
            else
            {
                // Client lost the only peer (host). Grant host to elect if mid-coop play.
                // Intentional StopNetwork / join offline-load sets _suppressHostMigration.
                if (_suppressHostMigration)
                {
                    // Already tearing or intentional; do not nest StopNetwork.
                    return;
                }
                TryBeginHostMigration(disconnectInfo.Reason.ToString());
            }
        }

        public void OnNetworkError(IPEndPoint endPoint, SocketError socketError)
        {
            ModLog.Error(LogCat.Network, "Network error: " + socketError);
            StatusText = "Error: " + socketError;
        }

        public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod)
        {
            OnNetworkReceiveBody(peer, reader, channelNumber, deliveryMethod);
        }

        private void OnNetworkReceiveBody(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod)
        {
            if (!reader.TryGetByte(out byte messageType))
                return;

            var type = (NetMessageType)messageType;
            byte[] payload = reader.GetRemainingBytes();

            // Track which peer sent this message so handlers can look up PlayerId
            _currentReceivePeer = peer;
            _currentReceivePlayerId = GetPlayerId(peer);
            if (IsConnected)
                ClientPerfProbe.NotePacketRx(type);
            ProcessInboundMessage(type, payload);
        }
    }
}
