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

        private NetManager _net;
        /// <summary>
        /// Per-session peer bookkeeping. StopNetwork replaces it; a transport stop (migration, soft
        /// reconnect) replaces its <see cref="SessionState.Link"/>.
        /// </summary>
        private SessionState _session = new SessionState();

        /// <summary>LAN (LiteNetLib) peers by player id.</summary>
        private readonly LanPeerTable _lanPeers = new LanPeerTable();
        private NetworkRole _role = NetworkRole.Offline;
        private readonly Dictionary<int, RemotePlayerProxy> _remoteProxies = new Dictionary<int, RemotePlayerProxy>();

        internal Dictionary<int, RemotePlayerProxy> RemoteProxies => _remoteProxies;
        private WorldSyncService _worldSync;

        internal WorldSyncService WorldSync => _worldSync;

        private WorldSaveShareService _worldSaveShare;
        private float _sendTimer;
        private uint _nextPlayerStateSequence;
        internal Dictionary<int, uint> LastPhysicsStateSequence => _session.Link.LastPhysicsStateSequence;
        internal Dictionary<int, uint> LastReliablePhysicsStateSequence => _session.Link.LastReliablePhysicsStateSequence;
        private float _proxyAggroTimer;
        private float _effectSyncTimer;
        private Vector3 _lastSentPosition;
        private bool _wasDragging;
        private string _lastDraggedItemName;

        internal bool WasDragging { get => _wasDragging; set => _wasDragging = value; }
        internal string LastDraggedItemName { get => _lastDraggedItemName; set => _lastDraggedItemName = value; }
        internal Dictionary<int, uint> LastPlayerStateSequence => _session.Link.LastPlayerStateSequence;
        internal Dictionary<string, Vector3> LastDragSyncPos => PlayerInteractHandlers.LastDragSyncPos;
        internal Dictionary<string, float> DragEndedAt => PlayerInteractHandlers.DragEndedAt;
        /// <summary>Local E-drag scrape intent (player walking). False → reliable quiet stop for peers.</summary>
        private bool _dragScrapeActive;
        private float _dragScrapeQuietSince = -1f;
        /// <summary>Matches body-push <see cref="ItemMovingSoundHelper"/> local stop speed.</summary>
        private const float DragScrapeStopSpeed = 1f;
        private const float DragScrapeStopGrace = 0.05f;


        internal bool HandshakeComplete
        {
            get => _session.Link.HandshakeComplete;
            set => _session.Link.HandshakeComplete = value;
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
                        $"[{snapshotKind}] stale snapshot rejected sender={senderId} seq={sequence} last={last}");
                return false;
            }

            lastBySender[senderId] = sequence;
            return true;
        }

        /// <summary>
        /// Per-peer handshake tracking on the host. Prevents a newly joining peer from
        /// freezing gameplay traffic for peers that are already ready.
        /// </summary>
        internal HashSet<int> HandshakedPeers => _session.Link.Handshaked;


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
            PlayerLightFxApplyHandlers.PendingPlayerLights;

        private int _nextThrowId = 1;


        private const int HeavyLateJoinPhaseCount = 12; // weather through fired GameEvents bulk

        /// <summary>Title-join: wait after first PlayerState before bulk (avoids half-loaded apply).</summary>
        private const float ClientBulkSettleSeconds = 8f;
        /// <summary>Phase-3 reconnect: client finished offline load, so use a short settle.</summary>
        private const float CoopReconnectBulkSettleSeconds = 1.5f;








        internal Dictionary<int, string> RemoteOutsideLocation => _session.RemoteOutsideLocation;


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
        public IEnumerable<int> GetHandshakedPeerIds() => _session.Link.Handshaked;

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

        private static bool _explicitApplyingRemoteState; // process-scoped: call-scoped, unwound by its Finalizer/finally

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
            => playerId > 0 && _session.Link.Handshaked.Contains(playerId);

        internal void RecordPendingContainerRemove(Vector3 pos, int slotIdx) =>
            ContainerPendingHandlers.RecordPendingContainerRemove(pos, slotIdx);

        internal void RecordPendingTakePreCount(Vector3 pos, int slotIdx, int preCount,
            bool isRecipe = false, string itemType = null, float durability = -1f, int ammo = 0) =>
            ContainerPendingHandlers.RecordPendingTakePreCount(pos, slotIdx, preCount, isRecipe, itemType, durability, ammo);

        internal void ClearPendingTakePreCount(Vector3 pos, int slotIdx) =>
            ContainerPendingHandlers.ClearPendingTakePreCount(pos, slotIdx);

        /// <summary>Thin forward: drag claim maps live on <see cref="PlayerInteractNetHandlers"/>.</summary>

        internal bool IsDragClaimedByOther(string objectName, int localPlayerId) =>
            PlayerInteractHandlers.IsDragClaimedByOther(objectName, localPlayerId);

        /// <summary>True while processing an incoming BarricadeEventMessage.
        /// Suppresses Postfix re-broadcast to prevent loops (unlike the broader
        /// IsApplyingRemoteState which also blocks legitimate HandleMeleeWorldHit
        /// feedback).</summary>
        internal static bool ProcessingBarricadeEvent; // process-scoped: call-scoped, unwound by its Finalizer/finally

        // body-push/drag sounds now use native ItemSounds via Rigidbody velocity
        /// <summary>Thin forward: remote drag ids live on <see cref="PlayerInteractNetHandlers"/>.</summary>
        internal const float DragStopStaleGraceConst = PlayerInteractNetHandlers.DragStopStaleGraceConst;

        /// <summary>True while performing a save triggered by the remote peer.</summary>
        internal static bool RemoteSaveInProgress; // reset-in: ResetStaticSessionFlags

        /// <summary>
        /// Host: set when a take or place loses a race, so the payload is not
        /// forwarded.
        /// </summary>
        private bool _suppressForwardThisMessage;

        /// <summary>
        /// Inside an inbound handler: do not relay this message to the other clients (the handler
        /// rejected it, or fans out its own corrected copy). Cleared before the next message.
        /// </summary>
        internal void SuppressRelay() => _suppressForwardThisMessage = true;


        public event Action Connected;

        /// <summary>One-shot host→client new-world save transfer.</summary>
        public WorldSaveShareService WorldSaveShare => _worldSaveShare;

        /// <summary>True when local side has finished protocol handshake with at least one peer.</summary>
        public bool IsHandshakeComplete => _session.Link.HandshakeComplete;

        private void Awake()
        {
            ModRuntime.AttachNetwork(this);
            _steamPeers = new SteamPeerTable(() => Steam, SteamPasswordGateActive);
            _worldSync = new WorldSyncService();
            _worldSaveShare = new WorldSaveShareService(this);
            CombatDeathBagHandlers = new CombatDeathBagNetHandlers(this);
            CombatAttackHandlers = new CombatAttackNetHandlers(this);
            CombatDeathStateHandlers = new CombatDeathStateNetHandlers(this);
            DreamHandlers = new DreamNetHandlers(this);
            EpilogueHandlers = new EpilogueNetHandlers(this);
            ContainerPendingHandlers = new ContainerPendingNetHandlers(this);
            ContainerDeathDropHandlers = new ContainerDeathDropNetHandlers(this);
            ContainerLootHandlers = new ContainerLootNetHandlers(this, ContainerPendingHandlers);
            DialogOutcomeApplyHandlers = new DialogOutcomeApplyNetHandlers(this);
            DialogOutcomeCloseHandlers = new DialogOutcomeCloseNetHandlers(DialogOutcomeApplyHandlers);
            DialogOutcomeApplyHandlers.BindClose(DialogOutcomeCloseHandlers);
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
            PlayerPresenceHandlers = new PlayerPresenceNetHandlers(this);
            PlayerInteractHandlers = new PlayerInteractNetHandlers(this);
            PlayerFXHandlers = new PlayerFXNetHandlers(this);
            JournalHandlers = new JournalNetHandlers(this);
            ChapterHandlers = new ChapterNetHandlers(this);
            CutsceneHandlers = new CutsceneNetHandlers(this);
            GameEventHandlers = new GameEventNetHandlers(this);
            LocationEnterExitHandlers = new LocationEnterExitNetHandlers(this);
            LocationEntityTrapHandlers = new LocationEntityTrapNetHandlers(this);
            WorldObjectSendHandlers = new WorldObjectSendNetHandlers(this);
            WorldSendHandlers = new WorldSendNetHandlers(this);
            WorldPhysicsHandlers = new WorldPhysicsNetHandlers(this);
            WorldWeatherTimeHandlers = new WorldWeatherTimeNetHandlers(this);
            WorldLateJoinHandlers = new WorldLateJoinNetHandlers(this);
            WorldProxyLifecycleHandlers = new WorldProxyLifecycleNetHandlers(this);
            WorldProxyEffectHandlers = new WorldProxyEffectNetHandlers(this);
            WorldFxHandlers = new WorldFxNetHandlers(this);
            PlayerLightFxApplyHandlers = new PlayerLightFxApplyNetHandlers(this);
            PlayerLightFxAmbientHandlers = new PlayerLightFxAmbientNetHandlers();
            CombatFxImpactHandlers = new CombatFxImpactNetHandlers(this);
            CombatFxGasBurnHandlers = new CombatFxGasBurnNetHandlers(this);
            SaveHandlers = new SaveNetHandlers(this);
            BulkSyncHandlers = new BulkSyncNetHandlers(this);
            RegisterInboundHandlers();
            Sync.DreamAudioPlayer.Initialize();
        }
    }
}
