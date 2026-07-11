using System;
using System.Collections.Generic;
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
        private WorldSyncService _worldSync;
        private WorldSaveShareService _worldSaveShare;
        private float _sendTimer;
        private float _proxyAggroTimer;
        private float _effectSyncTimer;
        private Vector3 _lastSentPosition;
        private bool _wasDragging;
        private string _lastDraggedItemName;
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

        /// <summary>
        /// Per-peer handshake tracking on the host. Prevents a newly joining peer from
        /// freezing gameplay traffic for peers that are already ready.
        /// </summary>
        private readonly HashSet<int> _handshakedPeers = new HashSet<int>();

        private int _nextPlayerId = 2;
        private int _localPlayerId = 1; // Host is always player 1

        // Per-player state tracking (consolidated)
        private readonly Dictionary<int, RemotePlayerState> _remotePlayers = new Dictionary<int, RemotePlayerState>();
        private bool _previousInOutsideLocation;
        private string _previousLocationName = "";
        private int _locationSyncCounter;

        // Protocol 19 continuous light dirty cache (local send path)
        private bool _prevSentFlareActive;
        private bool _prevSentFlashActive;
        private bool _prevSentMatchActive;
        private float _lastSentFlareRadius, _lastSentFlareIntensity;
        private float _lastSentFlareColorR, _lastSentFlareColorG, _lastSentFlareColorB;
        private float _lastSentFlashRadius, _lastSentFlashIntensity;
        private float _lastSentFlashColorR, _lastSentFlashColorG, _lastSentFlashColorB;
        private string _lastSentFlareItemType;
        private float _lightParamsForceTimer;
        private float _localHeldLightStartTime = -1f;
        private float _localHeldLightLongevity = 3f;
        private int _nextThrowId = 1;
        private const float LightParamsForceInterval = 0.15f; // ~6.6 Hz while active (was 1 Hz)
        private const float LightRadiusDirtyEps = 5f;
        private const float LightIntensityDirtyEps = 0.02f;
        private const float LightColorDirtyEps = 0.02f;

        // Flag sync arrived before Flags.Instance existed (main menu / loading)
        private bool _hasPendingFlagBulk;
        private FlagBulkSyncMessage _pendingFlagBulk;
        private readonly List<FlagSyncMessage> _pendingFlagDeltas = new List<FlagSyncMessage>();
        private const int MaxPendingFlagDeltas = 256;

        // Journal bulk / world-object cleanup before UI.journal or scene pickups exist
        private bool _hasPendingJournalBulk;
        private JournalBulkSyncMessage _pendingJournalBulk;
        private bool _needsJournalWorldCleanup;

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
        private const int HeavyLateJoinPhaseCount = 7; // weather…deathbags (phases 0–6)
        /// <summary>Title-join: wait after first PlayerState before bulk (avoids half-loaded apply).</summary>
        private const float ClientBulkSettleSeconds = 8f;
        /// <summary>Phase-3 reconnect: client already finished offline load — short settle only.</summary>
        private const float CoopReconnectBulkSettleSeconds = 1.5f;

        /// <summary>
        /// Host: peers mid world-download / LoadScene. Gameplay flood (PlayerState, physics,
        /// entity snapshots) to these peers stalls dual-box host when the client stops
        /// PollEvents during SaveManager.Load — kill-client-to-unfreeze symptom.
        /// Cleared on first in-world PlayerState or disconnect.
        /// </summary>
        private readonly HashSet<int> _peersLoadingWorld = new HashSet<int>();

        /// <summary>
        /// Host: peers that reconnected with AlreadyInWorld (join pipeline phase 3).
        /// Shorter late-join bulk settle; disconnect during load mute is not a "mid-night" leave.
        /// </summary>
        private readonly HashSet<int> _peersCoopReconnect = new HashSet<int>();

        // Trader absolute stock arrived before NPC GameObject existed
        private readonly Dictionary<string, TradeInventorySyncMessage> _pendingTradeInventories =
            new Dictionary<string, TradeInventorySyncMessage>();

        // Constructible sites (rounded pos → option) for late-join bulk
        private readonly Dictionary<string, ConstructibleMessage> _constructedSites =
            new Dictionary<string, ConstructibleMessage>();
        private readonly List<ConstructibleMessage> _pendingConstructibles = new List<ConstructibleMessage>();
        private const int MaxPendingConstructibles = 64;

        // Saw state arrived before the Saw component existed in the scene
        private readonly List<SawStateMessage> _pendingSawStates = new List<SawStateMessage>();
        private const int MaxPendingSawStates = 16;

        // Feeder / Lure state before component exists in scene
        private readonly List<FeederStateMessage> _pendingFeederStates = new List<FeederStateMessage>();
        private readonly List<LureStateMessage> _pendingLureStates = new List<LureStateMessage>();
        private const int MaxPendingStationStates = 16;

        // Barricade/door/window events before target exists in the scene
        private readonly List<BarricadeEventMessage> _pendingBarricadeEvents = new List<BarricadeEventMessage>();
        private const int MaxPendingBarricadeEvents = 64;

        // Night scenario join/live before NightScenarios.Instance exists
        private bool _hasPendingScenarioSync;
        private ScenarioSyncMessage _pendingScenarioSync;
        private bool _hasPendingScenarioEvent;
        private ScenarioEventFiredMessage _pendingScenarioEvent;

        // GameEventsFired arrived before the matching GameEvents existed in the scene
        private readonly List<GameEventsFiredMessage> _pendingGameEvents = new List<GameEventsFiredMessage>();
        private const int MaxPendingGameEvents = 64;

        public NetworkRole Role => _role;
        public bool IsConnected => _peers.Count > 0;
        public int ConnectedPlayerCount => _peers.Count;
        public int LocalPlayerId => _localPlayerId;
        public string StatusText { get; internal set; } = "Offline";
        public WorldSyncService WorldSync => _worldSync;
        public IReadOnlyCollection<int> ConnectedPlayerIds => _peers.Keys;

        /// <summary>Handshaked peer ids (host: clients; client: usually {1}). For dream all-dead set (D7).</summary>
        public IEnumerable<int> GetHandshakedPeerIds() => _handshakedPeers;

        public static bool IsApplyingRemoteState { get; internal set; }

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

        /// <summary>
        /// Records a pending RemoveItem/TakeItem so HandleContainerStateSync
        /// won't re-add the item to this slot (infinite loot dupe prevention).
        /// </summary>
        internal void RecordPendingContainerRemove(Vector3 pos, int slotIdx)
        {
            string key = $"{pos.x:F2}_{pos.y:F2}_{pos.z:F2}";
            if (!_pendingContainerRemoves.TryGetValue(key, out var set))
            {
                set = new HashSet<int>();
                _pendingContainerRemoves[key] = set;
            }
            set.Add(slotIdx);
        }

        /// <summary>
        /// Records the player inventory count of an item type before a container take
        /// was sent. Used by HandleContainerTakeDenied for precise refund (H6).
        /// Key = container position + slot index, Value = player's pre-take count of that item type.
        /// </summary>
        internal void RecordPendingTakePreCount(Vector3 pos, int slotIdx, int preCount)
        {
            string key = $"{pos.x:F2}_{pos.y:F2}_{pos.z:F2}_{slotIdx}";
            _pendingTakePreCounts[key] = preCount;
        }

        /// <summary>Removes a pending take pre-count entry after it's consumed or stale.</summary>
        internal void ClearPendingTakePreCount(Vector3 pos, int slotIdx)
        {
            string key = $"{pos.x:F2}_{pos.y:F2}_{pos.z:F2}_{slotIdx}";
            _pendingTakePreCounts.Remove(key);
        }

        /// <summary>
        /// Tracks which player (by peer ID) currently claims each dragged object.
        /// Key = ObjectName from the drag sync, Value = player ID (-1 = unclaimed).
        /// Prevents two players from dragging the same object simultaneously.
        /// </summary>
        internal readonly Dictionary<string, int> _dragClaims = new Dictionary<string, int>();

        /// <summary>
        /// Returns true if the given object is claimed by a remote player (not the local one).
        /// </summary>
        internal bool IsDragClaimedByOther(string objectName, int localPlayerId)
        {
            if (string.IsNullOrEmpty(objectName)) return false;
            if (_dragClaims.TryGetValue(objectName, out int claimerId))
                return claimerId >= 0 && claimerId != localPlayerId;
            return false;
        }

        /// <summary>True while processing an incoming BarricadeEventMessage.
        /// Suppresses Postfix re-broadcast to prevent loops (unlike the broader
        /// IsApplyingRemoteState which also blocks legitimate HandleMeleeWorldHit
        /// feedback).</summary>
        internal static bool _processingBarricadeEvent;

        // body-push/drag sounds now use native ItemSounds via Rigidbody velocity
        /// <summary>Instance IDs of items currently being dragged by a remote peer.
        /// These items are skipped in TryBuildWorldSnapshot to prevent PhysicsState
        /// (0.3 Hz) from fighting with DragSync (30 Hz).
        /// Uses GetInstanceID() -- this is correct for local scene objects, but
        /// DOES NOT work cross-peer for claim checking (see _remoteDragItemNames).</summary>
        internal readonly HashSet<int> _remoteDragItemIds = new HashSet<int>();
        /// <summary>Names of items currently being dragged by a remote peer.
        /// Separate from _remoteDragItemIds (which uses InstanceID) -- this set
        /// exists only for the DragClaimStartPatch cross-peer check, where
        /// InstanceID would never match between processes.</summary>
        internal readonly HashSet<string> _remoteDragItemNames = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        /// <summary>Last synced position per dragged item -- used to only play drag
        /// sound when the item actually moves, not on every DragSync tick.</summary>
        private readonly Dictionary<string, Vector3> _lastDragSyncPos = new Dictionary<string, Vector3>();


        /// <summary>Tracks which container slots the local player has sent a pending
        /// RemoveItem/TakeItem for. Key = container position string, Value = set of
        /// slot indices. Prevents HandleContainerStateSync from re-adding items the
        /// player already took (infinite loot dupe fix).</summary>
        internal readonly Dictionary<string, HashSet<int>> _pendingContainerRemoves = new Dictionary<string, HashSet<int>>();

        /// <summary>Tracks player inventory item count before each pending container take.
        /// Key = "$pos_{slotIdx}", Value = pre-take count. Used for precise H6 refund
        /// so ContainerTakeDenied doesn't over-remove when player already had items of that type.</summary>
        internal readonly Dictionary<string, int> _pendingTakePreCounts = new Dictionary<string, int>();

        /// <summary>True while performing a save triggered by the remote peer.</summary>
        internal static bool _isRemoteSaveInProgress;

        /// <summary>
        /// Host: set in HandleContainerItem when take/place loses a race — skip Forwardable fan-out.
        /// </summary>
        internal bool _suppressForwardThisMessage;

        /// <summary>Debounce rapid melee hits to the same door/window (e.g. shotgun
        /// pellets) to avoid 15Ã— particle/sound spam on the host.</summary>
        private const float MELEE_HIT_DEBOUNCE_SEC = 0.2f;
        private readonly Dictionary<string, float> _meleeHitDebounce = new Dictionary<string, float>();

        public event Action Connected;
        public event Action Disconnected;

        /// <summary>One-shot host→client new-world save transfer.</summary>
        public WorldSaveShareService WorldSaveShare => _worldSaveShare;

        /// <summary>True when local side has finished protocol handshake with at least one peer.</summary>
        public bool IsHandshakeComplete => _handshakeComplete;

        private void Awake()
        {
            Instance = this;
            _worldSync = new WorldSyncService(ModRuntime.Log);
            _worldSaveShare = new WorldSaveShareService(this);
            Sync.DreamAudioPlayer.Initialize();
        }

        public void StartHost(int port)
        {
            StopNetwork();
            _role = NetworkRole.Host;
            _localPlayerId = 1;
            _hostPlayerId = 1;
            NoteSessionPort(port);
            _net = new NetManager(this) { UnconnectedMessagesEnabled = false, DisconnectTimeout = 30000 };
            if (!_net.Start(port))
            {
                StatusText = "Failed to bind port " + port;
                _role = NetworkRole.Offline;
                return;
            }

            StatusText = "Hosting on port " + port;
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
                _handshakeComplete = false;
                _handshakedPeers.Clear();
                _awaitingLateJoinBulk.Clear();
                _pendingHeavyLateJoinBulk.Clear();
                _peersLoadingWorld.Clear();
                _peersCoopReconnect.Clear();
            }
            else
            {
                StopNetwork();
            }

            _role = NetworkRole.Client;
            _hostPlayerId = 1;
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
            // Intentional tear — never treat ensuing peer-down as host-crash migration.
            _suppressHostMigration = true;

            // Snapshot for public session-stop line before we wipe peers/ids
            NetworkRole wasRole = _role;
            int wasLocalId = _localPlayerId;
            int wasPeers = _peers.Count;

            NetworkResetRegistry.ResetAll();
            ResetCombatSessionState();
            ResetSessionNetworkState();
            foreach (int id in new List<int>(_remoteProxies.Keys))
                DestroyRemoteProxy(id);
            _remoteProxies.Clear();
            _wasDragging = false;
            _lastDraggedItemName = null;
            _dragScrapeActive = false;
            _dragScrapeQuietSince = -1f;
            _spawnedDragProxyItems.Clear();
            _lastDragSyncPos.Clear();
            _dragClaims.Clear();
            DWMPHorde.Audio.MovingObjectSoundService.Reset();
            _remoteDragItemIds.Clear();
            _remoteDragItemNames.Clear();
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
            _peers.Clear();
            _sendTimer = 0f;
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

        private void ResetLocalLightSendCache()
        {
            _prevSentFlareActive = false;
            _prevSentFlashActive = false;
            _prevSentMatchActive = false;
            _localHeldLightStartTime = -1f;
            _localHeldLightLongevity = 3f;
            _lastSentFlareRadius = 0f;
            _lastSentFlareIntensity = 0f;
            _lastSentFlareColorR = _lastSentFlareColorG = _lastSentFlareColorB = 0f;
            _lastSentFlashRadius = 0f;
            _lastSentFlashIntensity = 0f;
            _lastSentFlashColorR = _lastSentFlashColorG = _lastSentFlashColorB = 0f;
            _lastSentFlareItemType = null;
            _lightParamsForceTimer = 0f;
        }

        /// <summary>
        /// Protocol 19: pack continuous flare/match/flashlight into LightFlags + conditional payload.
        /// Active: offset every tick; params dirty / rising / ~6 Hz force; remain + flash aim trailer.
        /// </summary>
        private void PackContinuousLights(ref PlayerStateMessage msg, Player local)
        {
            _lightParamsForceTimer += SendInterval;
            bool forceParams = _lightParamsForceTimer >= LightParamsForceInterval;
            if (forceParams)
                _lightParamsForceTimer = 0f;

            byte flags = 0;
            string curType = local.currentItem != null ? local.currentItem.type : null;
            bool flareActive = !string.IsNullOrEmpty(curType)
                && curType.IndexOf("flare", StringComparison.OrdinalIgnoreCase) >= 0;
            bool matchActive = !flareActive && IsMatchLightItem(local);
            bool heldBurnLight = flareActive || matchActive;
            bool flashActive = !heldBurnLight
                && !InvItemClass.isNull(local.currentItem)
                && local.currentItem.baseClass != null
                && local.currentItem.baseClass.isFlashlight
                && local.currentItem.activated;

            if (heldBurnLight)
            {
                flags |= PlayerStateMessage.LightFlagFlare;
                if (matchActive)
                    flags |= PlayerStateMessage.LightFlagMatch;
                msg.FlareActive = flareActive;
                msg.MatchActive = matchActive;
                msg.FlareItemType = curType ?? (matchActive ? "match" : "flare");
                msg.FlareRadius = matchActive ? 180f : 650f;
                msg.FlareIntensity = matchActive ? 0.85f : 1f;
                msg.FlareColorR = 1f;
                msg.FlareColorG = matchActive ? 0.65f : 0.5f;
                msg.FlareColorB = matchActive ? 0.2f : 0.1f;

                Light2D itemLight = null;
                Flare flareComp = null;
                if (local.heldItem != null)
                {
                    flareComp = local.heldItem.GetComponent<Flare>()
                        ?? local.heldItem.GetComponentInChildren<Flare>(true);
                    if (flareComp != null && flareComp.light2D != null)
                        itemLight = flareComp.light2D;
                    if (itemLight == null)
                        itemLight = local.heldItem.GetComponentInChildren<Light2D>(true);
                }

                if (itemLight != null)
                {
                    if (itemLight.isActiveAndEnabled || itemLight.LightRadius > 0f)
                    {
                        msg.FlareRadius = itemLight.LightRadius > 0f ? itemLight.LightRadius : msg.FlareRadius;
                        msg.FlareIntensity = itemLight.LightIntensity > 0f ? itemLight.LightIntensity : msg.FlareIntensity;
                        msg.FlareColorR = itemLight.LightColor.r;
                        msg.FlareColorG = itemLight.LightColor.g;
                        msg.FlareColorB = itemLight.LightColor.b;
                    }
                    Vector3 delta = itemLight.transform.position - local.transform.position;
                    msg.FlareLocalX = delta.x;
                    msg.FlareLocalY = delta.y;
                    msg.FlareLocalZ = delta.z;
                }
                else if (local.heldItem != null)
                {
                    Vector3 delta = local.heldItem.transform.position - local.transform.position;
                    msg.FlareLocalX = delta.x;
                    msg.FlareLocalY = delta.y;
                    msg.FlareLocalZ = delta.z;
                }

                bool rising = flareActive ? !_prevSentFlareActive : !_prevSentMatchActive;
                if (rising)
                {
                    _localHeldLightStartTime = Time.time;
                    _localHeldLightLongevity = flareComp != null && flareComp.longevity > 0.05f
                        ? flareComp.longevity + 2f // waitToDie + fade
                        : (matchActive ? 8f : 5f);
                }

                // Host-side remain for peers (clients stream local estimate; host is visual authority on expire).
                if (_localHeldLightStartTime > 0f && _localHeldLightLongevity > 0.01f)
                {
                    float rem = 1f - (Time.time - _localHeldLightStartTime) / _localHeldLightLongevity;
                    msg.HeldLightRemain01 = (byte)Mathf.Clamp(Mathf.RoundToInt(rem * 255f), 0, 255);
                    flags |= PlayerStateMessage.LightFlagRemain;
                }

                bool typeChanged = !string.Equals(_lastSentFlareItemType, msg.FlareItemType, StringComparison.Ordinal);
                bool dirty = rising || forceParams
                    || Mathf.Abs(msg.FlareRadius - _lastSentFlareRadius) > LightRadiusDirtyEps
                    || Mathf.Abs(msg.FlareIntensity - _lastSentFlareIntensity) > LightIntensityDirtyEps
                    || Mathf.Abs(msg.FlareColorR - _lastSentFlareColorR) > LightColorDirtyEps
                    || Mathf.Abs(msg.FlareColorG - _lastSentFlareColorG) > LightColorDirtyEps
                    || Mathf.Abs(msg.FlareColorB - _lastSentFlareColorB) > LightColorDirtyEps;

                if (dirty)
                {
                    flags |= PlayerStateMessage.LightFlagFlareParams;
                    msg.FlareHasParams = true;
                    _lastSentFlareRadius = msg.FlareRadius;
                    _lastSentFlareIntensity = msg.FlareIntensity;
                    _lastSentFlareColorR = msg.FlareColorR;
                    _lastSentFlareColorG = msg.FlareColorG;
                    _lastSentFlareColorB = msg.FlareColorB;
                }

                if (rising || typeChanged)
                {
                    flags |= PlayerStateMessage.LightFlagFlareItemType;
                    msg.FlareHasItemType = true;
                    _lastSentFlareItemType = msg.FlareItemType;
                }

                if (rising && Config.ModConfig.IsVerboseLightSync)
                    ModRuntime.LegacyInfo("[LightSync] local " + (matchActive ? "match" : "flare")
                        + " ON type=" + msg.FlareItemType);
            }
            else if ((_prevSentFlareActive || _prevSentMatchActive) && Config.ModConfig.IsVerboseLightSync)
            {
                ModRuntime.LegacyInfo("[LightSync] local held burn light OFF");
            }

            if (flashActive)
            {
                flags |= PlayerStateMessage.LightFlagFlashlight;
                msg.FlashlightActive = true;
                Light2D flash = Traverse.Create(local).Field("Flashlight").GetValue<Light2D>();
                if (flash != null)
                {
                    msg.FlashRadius = flash.LightRadius;
                    msg.FlashIntensity = flash.LightIntensity > 0f ? flash.LightIntensity : 1f;
                    msg.FlashColorR = flash.LightColor.r;
                    msg.FlashColorG = flash.LightColor.g;
                    msg.FlashColorB = flash.LightColor.b;
                    // Cone aim follows Flashlight child rotation (SP aims with body/mouse).
                    msg.FlashAimY = (short)Mathf.RoundToInt(flash.transform.eulerAngles.y);
                    flags |= PlayerStateMessage.LightFlagFlashAim;
                }
                else
                {
                    msg.FlashRadius = 400f;
                    msg.FlashIntensity = 1f;
                    msg.FlashColorR = 0.3f;
                    msg.FlashColorG = 0.3f;
                    msg.FlashColorB = 0.3f;
                    msg.FlashAimY = (short)Mathf.RoundToInt(local.transform.eulerAngles.y);
                    flags |= PlayerStateMessage.LightFlagFlashAim;
                }

                bool rising = !_prevSentFlashActive;
                bool dirty = rising || forceParams
                    || Mathf.Abs(msg.FlashRadius - _lastSentFlashRadius) > LightRadiusDirtyEps
                    || Mathf.Abs(msg.FlashIntensity - _lastSentFlashIntensity) > LightIntensityDirtyEps
                    || Mathf.Abs(msg.FlashColorR - _lastSentFlashColorR) > LightColorDirtyEps
                    || Mathf.Abs(msg.FlashColorG - _lastSentFlashColorG) > LightColorDirtyEps
                    || Mathf.Abs(msg.FlashColorB - _lastSentFlashColorB) > LightColorDirtyEps;

                if (dirty)
                {
                    flags |= PlayerStateMessage.LightFlagFlashParams;
                    msg.FlashHasParams = true;
                    _lastSentFlashRadius = msg.FlashRadius;
                    _lastSentFlashIntensity = msg.FlashIntensity;
                    _lastSentFlashColorR = msg.FlashColorR;
                    _lastSentFlashColorG = msg.FlashColorG;
                    _lastSentFlashColorB = msg.FlashColorB;
                }

                if (rising && Config.ModConfig.IsVerboseLightSync)
                    ModRuntime.LegacyInfo("[LightSync] local flashlight ON");
            }
            else if (_prevSentFlashActive && Config.ModConfig.IsVerboseLightSync)
            {
                ModRuntime.LegacyInfo("[LightSync] local flashlight OFF");
            }

            if (!heldBurnLight)
            {
                _lastSentFlareItemType = null;
                _localHeldLightStartTime = -1f;
            }

            msg.LightFlags = flags;
            _prevSentFlareActive = flareActive;
            _prevSentMatchActive = matchActive;
            _prevSentFlashActive = flashActive;
        }

        /// <summary>Match / short-lived held light (not flashlight, not flare, not torch emitter).</summary>
        internal static bool IsMatchLightItem(Player local)
        {
            if (local == null || InvItemClass.isNull(local.currentItem) || local.currentItem.baseClass == null)
                return false;
            if (!local.currentItem.activated)
                return false;
            string t = local.currentItem.type ?? "";
            if (t.IndexOf("match", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            if (t.IndexOf("flare", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;
            if (local.currentItem.baseClass.isFlashlight)
                return false;
            if (local.currentItem.baseClass.lightEmitter != null)
                return false;
            // Short ambient / item light with small radius while activated.
            if (local.currentItem.baseClass.lightRadius > 0f
                && local.currentItem.baseClass.lightRadius < 350f
                && local.heldItem != null
                && local.heldItem.GetComponentInChildren<Light2D>(true) != null)
                return true;
            return false;
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
            _net?.PollEvents();

            // Apply join bulk/deltas that arrived before Flags existed (menu → load)
            TryFlushPendingFlags();
            TryFlushPendingJournal();
            TryFlushPendingTradeInventories();
            TryFlushPendingConstructibles();
            TryFlushPendingSawStates();
            TryFlushPendingFeederStates();
            TryFlushPendingLureStates();
            Sync.StationSyncHelpers.FlushLureOutbox(force: false);
            TryFlushPendingBarricadeEvents();
            TryFlushPendingScenario();
            TryFlushPendingLocks();
            Sync.WorldPhysicsSyncService.TryFlushPendingLights();
            Sync.TrapNetworkId.FlushPending(
                (p, n) => Sync.WorldPhysicsSyncService.FindTrapByPos(p, n),
                (go, trig) => Sync.WorldPhysicsSyncService.ApplyTrapState(go, trig));
            Sync.WorldPhysicsSyncService.TickThrownLightExpiry(this);
            TickHeavyLateJoinBulk();
            TickPeerRosterGossip();
            TickHostMigrationRetry();
            if (_hasPendingScenarioEvent && Singleton<NightScenarios>.Instance != null)
            {
                _hasPendingScenarioEvent = false;
                var ev = _pendingScenarioEvent;
                _pendingScenarioEvent = default;
                ApplyScenarioEventFired(ev);
            }
            TryFlushPendingGameEvents();

            // Periodic cleanup of stale melee hit debounce entries
            if (_meleeHitDebounce.Count > 0)
            {
                float now = Time.time;
                var stale = new List<string>();
                foreach (var kvp in _meleeHitDebounce)
                {
                    if (now - kvp.Value > 5f)
                        stale.Add(kvp.Key);
                }
                foreach (string key in stale)
                    _meleeHitDebounce.Remove(key);
            }

            if (!IsConnected || !_handshakeComplete)
                return;

            // Flush flag updates that were deferred by cooldown (host + client→host H1)
            if (_role == NetworkRole.Host || _role == NetworkRole.Client)
            {
                Patches.FlagSyncBoolPatch.TickFlush();
                Patches.FlagSyncIntPatch.TickFlush();
            }

            _sendTimer += Time.deltaTime;

            // Packing/sending world share — pause entity/physics spam so host can breathe.
            bool shareBusy = _worldSaveShare != null && _worldSaveShare.IsBusy;

            // Host: broadcast entity states to clients
            if (_role == NetworkRole.Host && !shareBusy)
            {
                EntityStateBroadcastService.Tick();

                _proxyAggroTimer += Time.deltaTime;
                if (_proxyAggroTimer >= 0.5f)
                {
                    _proxyAggroTimer = 0f;
                    ProxyAggroCheck();
                }

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
            }

            // Both host and client send their local physics state:
            // - Host broadcasts to all clients (authoritative)
            // - Client sends to host so it can merge + forward
            // Skip while local player is in a dream -- dream objects don't exist
            // in the shared world and would cause phantom spawns on the other side.
            _physicsSendTimer += Time.deltaTime;
            // Physics: always while awake; dream free-bodies allowed (D12 — not full forest).
            if (_physicsSendTimer >= PhysicsSendInterval && !shareBusy)
            {
                _physicsSendTimer = 0f;
                bool clientNotReady = _role == NetworkRole.Client
                    && (Core.mainMenu || Core.loadingGame || !Core.coreStarted);
                if (!clientNotReady
                    && Sync.WorldPhysicsSyncService.TryBuildWorldSnapshot(out var snap))
                {
                    if (_role == NetworkRole.Host)
                        Broadcast(NetMessageType.PhysicsState, w => snap.Serialize(w),
                            skipLoadingPeers: true);
                    else
                        Send(NetMessageType.PhysicsState, w => snap.Serialize(w));
                }
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
            // is ready — host treated first packet as "in world" and dumped heavy bulk.
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
            // Host was missing this — clients never saw host shadowWard / forestSpiritWard (4.10).
            _effectSyncTimer += Time.deltaTime;
            if (_effectSyncTimer >= 2f)
            {
                _effectSyncTimer = 0f;
                SendPlayerEffects();
            }

            // Both sides: send own position to the other side at ~30 Hz
            var msg = new PlayerStateMessage
            {
                PlayerId = _localPlayerId,
                PosX = pos.x,
                PosY = pos.y,
                PosZ = pos.z,
                VelX = vel.x,
                VelZ = vel.z,
                LocomotionState = (byte)PlayerAnimationSnapshot.ReadLocomotion(local),
                FlipX = false, // ponytail: game uses rotation, not sprite mirror
                Running = local.running,
                LegFacingY = PlayerAnimationSnapshot.ReadLegFacingY(local),
                ReverseLegs = PlayerAnimationSnapshot.ReadReverseLegs(local),
                TorsoFacingY = PlayerAnimationSnapshot.ReadTorsoFacingY(local),
                TorsoClip = PlayerAnimationSnapshot.ReadTorsoClip(local),
                LegsClip = PlayerAnimationSnapshot.ReadLegsClip(local),
                CurrentFrame = PlayerAnimationSnapshot.ReadCurrentFrame(local),
                InBearTrap = local.inBearTrap,
                HasLightProtection = local.isInLight,
                AfterNightActive = Singleton<Controller>.Instance != null && Singleton<Controller>.Instance.isAfterNight,
                TrapNetId = local.inBearTrap
                    ? Sync.TrapNetworkId.ResolveOccupyingTrapId(pos, hostMint: _role == NetworkRole.Host)
                    : 0
            };

            PackContinuousLights(ref msg, local);

            // Host: skip joiners still in world download / LoadScene (dual-box freeze).
            Broadcast(NetMessageType.PlayerState, w => msg.Serialize(w),
                skipLoadingPeers: _role == NetworkRole.Host);

            // Detect OutsideLocation (basement/bunker) transitions
            if (Singleton<OutsideLocations>.Instance != null)
            {
                bool inOutsideLoc = Singleton<OutsideLocations>.Instance.playerInOutsideLocation;
                string locName = Singleton<OutsideLocations>.Instance.currentLocationName ?? "";

                // While inside, send LocationEnter every 30 frames (~1 Hz) so the
                // receiver can retry after an async location spawn completes.
                _locationSyncCounter++;
                if (inOutsideLoc && (!_previousInOutsideLocation || locName != _previousLocationName || _locationSyncCounter >= 30))
                {
                    _locationSyncCounter = 0;
                    if (!string.IsNullOrEmpty(locName))
                    {
                        Broadcast(NetMessageType.LocationEnter,
                            w => new LocationEnterMessage
                            {
                                LocationName = locName,
                                PlayerId = _localPlayerId
                            }.Serialize(w),
                            DeliveryMethod.ReliableOrdered);
                        ModRuntime.LegacyInfo($"[LocationSync] sent LocationEnter: {locName} pid={_localPlayerId}");
                    }
                }

                // Exited the location (left the sub-location area)
                if (!inOutsideLoc && _previousInOutsideLocation)
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

                // Scrape intent = player walking (same gate as body-push). Not object pos delta —
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
                // Quiet scrape stop must be reliable — Unreliable quiet ticks were lost and
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
        /// Intentional E-drag release — same frame as vanilla <c>Item.stopDragging</c>.
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
                // Host: dual-path intentional stop (PlayerAudio IsStopSignal) so residual
                // PhysicsState after claim release cannot keep scrape armed on peers.
                if (_role == NetworkRole.Host)
                    NotifyBodyPushStopped(endedName);
            }
        }

        private void ProxyAggroCheck()
        {
            if (_remoteProxies.Count == 0)
                return;

            Character[] all = CharacterTracker.GetAll();
            if (all.Length == 0)
                return;

            foreach (var kvp in _remoteProxies)
            {
                RemotePlayerProxy proxy = kvp.Value;
                if (proxy == null) continue;
                Transform proxyT = proxy.transform;

                int aggroed = 0;
                int skippedFar = 0;
                int skippedAlreadyTargeting = 0;
                bool proxyHasEotF = proxy.RemoteHasEnemyOfTheForest;

                foreach (Character c in all)
                {
                    if (c == null || !c.alive || c.dummy)
                        continue;

                    if (c.target == proxyT)
                    {
                        skippedAlreadyTargeting++;
                        continue;
                    }

                    if (c.aggressiveness == Aggressiveness.neutral)
                    {
                        if (!proxyHasEotF || c.faction != Faction.animalAggressive)
                        {
                            skippedFar++;
                            continue;
                        }
                    }

                    if (!c.attacksFaction(Faction.player))
                    {
                        bool runsFromPlayer = HarmonyLib.Traverse.Create(c)
                            .Method("runsAwayFromFaction", Faction.player)
                            .GetValue<bool>();
                        if (!runsFromPlayer)
                        {
                            if (!proxyHasEotF || c.faction != Faction.animalAggressive)
                            {
                                skippedFar++;
                                continue;
                            }
                        }
                    }

                    bool runsFromProxy = c.aggressiveness == Aggressiveness.flee ||
                        c.aggressiveness == Aggressiveness.fleeAndDespawn ||
                        (c.attacksFaction(Faction.player) == false &&
                         HarmonyLib.Traverse.Create(c)
                             .Method("runsAwayFromFaction", Faction.player)
                             .GetValue<bool>());

                    float distToProxy = Vector3.Distance(c.transform.position, proxyT.position);

                    Sniffer entitySniffer = c.GetComponent<Sniffer>();
                    if (entitySniffer != null && distToProxy < entitySniffer.radius)
                    {
                        skippedFar++;
                        continue;
                    }

                    float proxRange = (float)c.nearViewDistance * c.aniSightRangeModifier;
                    if (proxRange <= 0f || distToProxy > proxRange)
                    {
                        skippedFar++;
                        continue;
                    }

                    if (c.sleeping)
                    {
                        c.wakeup();
                        if (runsFromProxy)
                            c.runAway(proxyT.position);
                        else
                            c.attackCharacter(proxyT);
                        aggroed++;
                        continue;
                    }

                    if (runsFromProxy)
                        c.runAway(proxyT.position);
                    else
                        c.attackCharacter(proxyT);
                    aggroed++;
                }

                if ((aggroed > 0 || ++_aggroLogCounter % 10 == 0) && ModRuntime.VerboseLogging)
                {
                    float now = Time.time;
                    if (now - _lastAggroLogTime >= 5f)
                    {
                        _lastAggroLogTime = now;
                        ModRuntime.LegacyInfo($"[Proxy] player {kvp.Key}: checked {all.Length} chars, aggroed={aggroed}, far={skippedFar}, alreadyTargetingOthers={skippedAlreadyTargeting}");
                    }
                }

                // Periodic cleanup of melee-hit dedup dictionary to prevent unbounded growth
                if (++_aggroLogCounter % 5 == 0)
                    Patches.MeleeSensorDeduplicatePatch.CleanupStaleEntries();
            }
        }



        private static int _aggroLogCounter;
        private static float _lastAggroLogTime;

        private void LateUpdate()
        {
            if (!IsConnected || !_handshakeComplete) return;

            // Both sides: interpolate world physics objects
            Sync.WorldPhysicsSyncService.UpdateObjectInterpolation();

            // Client only: interpolate remote entity positions for smooth movement
            if (_role == NetworkRole.Client)
            {
                ClientEntityInterpolationService.TickLateUpdate();
            }
        }

        /// <summary>
        /// Build a complete packet with a stack-local writer so nested Send/Broadcast
        /// from writeBody callbacks cannot corrupt a shared buffer (P0.5).
        /// </summary>
        private static byte[] BuildPacket(NetMessageType type, Action<NetWriter> writeBody)
        {
            var writer = new NetWriter();
            writer.Put((byte)type);
            writeBody(writer);
            return writer.CopyData();
        }

        /// <summary>Send a message to a specific peer by PlayerId.</summary>
        public void SendToPlayer(int playerId, NetMessageType type, Action<NetWriter> writeBody,
            DeliveryMethod method = DeliveryMethod.Unreliable)
        {
            if (!_peers.TryGetValue(playerId, out NetPeer peer))
                return;
            peer.Send(BuildPacket(type, writeBody), method);
        }

        /// <summary>Send a message to all connected peers.</summary>
        /// <param name="skipLoadingPeers">
        /// When true, skip peers in <see cref="_peersLoadingWorld"/> (title join / LoadScene).
        /// World share must pass false (default) so targeted broadcast resends still land.
        /// </param>
        public void SendToAll(NetMessageType type, Action<NetWriter> writeBody,
            DeliveryMethod method = DeliveryMethod.Unreliable, bool skipLoadingPeers = false)
        {
            if (_peers.Count == 0) return;
            byte[] data = null;
            foreach (var kvp in _peers)
            {
                if (skipLoadingPeers && _peersLoadingWorld.Contains(kvp.Key))
                    continue;
                if (data == null)
                    data = BuildPacket(type, writeBody);
                kvp.Value.Send(data, method);
            }
        }

        /// <summary>Send a message to all peers except one.</summary>
        public void SendToAllExcept(int excludePlayerId, NetMessageType type, Action<NetWriter> writeBody,
            DeliveryMethod method = DeliveryMethod.Unreliable, bool skipLoadingPeers = false)
        {
            if (_peers.Count == 0) return;
            byte[] data = null;
            foreach (var kvp in _peers)
            {
                if (kvp.Key == excludePlayerId) continue;
                if (skipLoadingPeers && _peersLoadingWorld.Contains(kvp.Key))
                    continue;
                if (data == null)
                    data = BuildPacket(type, writeBody);
                kvp.Value.Send(data, method);
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

        /// <summary>Host: joiner is downloading / applying / LoadScene — do not flood them.</summary>
        public void MarkPeerLoadingWorld(int playerId)
        {
            if (_role != NetworkRole.Host || playerId <= 1)
                return;
            if (_peersLoadingWorld.Add(playerId))
                ModLog.Event(LogCat.Session, "Peer " + playerId + " marked loading-world (gameplay flood muted)");
        }

        /// <summary>Host: mark every non-host peer as loading (broadcast world resend).</summary>
        public void MarkAllClientPeersLoadingWorld()
        {
            if (_role != NetworkRole.Host)
                return;
            foreach (int id in _peers.Keys)
            {
                if (id > 1)
                    MarkPeerLoadingWorld(id);
            }
        }

        /// <summary>Host: joiner sent first in-world PlayerState — safe for gameplay traffic.</summary>
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
            foreach (var kvp in _peers)
            {
                kvp.Value.Send(BuildPacket(type, writeBody), method);
                return; // Send to first peer only
            }
        }

        private NetPeer _currentReceivePeer;
        private int _currentReceivePlayerId = -1;

        /// <summary>GUIDs of dropped items that have already been picked up (host-authoritative).
        /// Prevents item multiplication when both players pick up the same GUID
        /// network message is processed.</summary>
        private static readonly HashSet<string> _consumedDropGuids = new HashSet<string>();

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
                // Do NOT clear _handshakeComplete when additional peers join — that
                // froze PlayerState/drag traffic for every already-ready client.
                // Only block gameplay until the first peer completes handshake.
                if (_handshakedPeers.Count == 0)
                    _handshakeComplete = false;
                StatusText = $"Player {playerId} connected";
                ModLog.Event(LogCat.Network, $"Player {playerId} connected (peers={_peers.Count}, ready={_handshakedPeers.Count})");

                // Session messages must be reliable — default SendToPlayer is Unreliable.
                // Lost handshake leaves client with LocalPlayerId=1 (host id) → multi-peer chaos.
                SendToPlayer(playerId, NetMessageType.Handshake, w =>
                {
                    new HandshakeMessage
                    {
                        ProtocolVersion = PluginInfo.ProtocolVersion,
                        PlayerId = (short)playerId,
                        HostPlayerId = (short)_localPlayerId
                    }.Serialize(w);
                }, DeliveryMethod.ReliableOrdered);

                EntityStateBroadcastService.SetPeers(_peers);
                WorldSessionMessage session = _worldSync.BuildHostSession();
                // Session metadata only here — identity + "which world". Heavy bulk is
                // deferred when host is already in-chapter: client is usually still on the
                // title menu and journal/inventory/entity apply NREs without a Player.
                SendToPlayer(playerId, NetMessageType.WorldSession, w => session.Serialize(w),
                    DeliveryMethod.ReliableOrdered);

                if (HostHasShareableWorld())
                {
                    // Title-menu join path: Handshake already schedules world file share.
                    // Gameplay bulk (journal, bags, flags, …) is sent after share completes
                    // so the client can queue until Player exists (see SendLateJoinGameplayBulk).
                    ModLog.Event(LogCat.Session,
                        "Peer " + playerId + " connected while host in-world — "
                        + "deferring gameplay bulk until after world share");
                }
                else
                {
                    // Host still on title / no chapter — safe enough to send bulk now
                    // (mostly empty); world share will run when host enters chapter.
                    SendLateJoinGameplayBulk(playerId);
                }
            }
            else
            {
                _handshakeComplete = false;
                _handshakedPeers.Clear();
                _peers[1] = peer; // Host is always player 1 for client
                StatusText = "Connected to host";
                ModLog.Event(LogCat.Network, "Connected to host");

                // AlreadyInWorld: phase-3 reconnect after offline load (skip re-share on host).
                // PlayerId = preferred stable id (migration reconnect / phase 3).
                bool alreadyInWorld = ClientReportsAlreadyInWorld() || _migrationInProgress;
                short preferredId = _localPlayerId > 0 ? (short)_localPlayerId : (short)0;
                Broadcast(NetMessageType.Handshake, w =>
                {
                    new HandshakeMessage
                    {
                        ProtocolVersion = PluginInfo.ProtocolVersion,
                        PlayerId = preferredId,
                        AlreadyInWorld = alreadyInWorld,
                    }.Serialize(w);
                }, DeliveryMethod.ReliableOrdered);

                if (alreadyInWorld)
                    ModLog.Event(LogCat.Session,
                        "Join pipeline phase 3: co-op reconnect (AlreadyInWorld) — host should skip share");

                // Do NOT EnsureRemoteProxy here — CanSpawnRemoteProxies may pass while the remote
                // has no state yet, and Spawn clones at local feet (body stack). Proxy comes from
                // first host PlayerState once CanSpawnRemoteProxies.
                SyncCurrentLightState();
            }

            Connected?.Invoke();
        }

        /// <summary>
        /// Sends all existing DeathDrop bags to a newly connected player so they
        /// see bags that were dropped before they joined.
        /// </summary>
        private void SyncExistingDeathBags(int targetPlayerId)
        {
            if (_role != NetworkRole.Host) return;
            DeathDrop[] allBags = UnityEngine.Object.FindObjectsOfType<DeathDrop>(true);
            int sent = 0;
            foreach (DeathDrop bag in allBags)
            {
                if (bag == null) continue;
                Inventory inv = bag.GetComponent<Inventory>();
                if (inv == null) continue;

                Vector3 pos = bag.transform.position;
                var types = new System.Collections.Generic.List<string>();
                var amounts = new System.Collections.Generic.List<int>();
                var durabilities = new System.Collections.Generic.List<float>();
                var ammos = new System.Collections.Generic.List<int>();

                if (inv.slots != null)
                {
                    foreach (InvSlot slot in inv.slots)
                    {
                        if (!InvItemClass.isNull(slot.invItem))
                        {
                            types.Add(slot.invItem.type);
                            amounts.Add(slot.invItem.amount);
                            durabilities.Add(slot.invItem.durability);
                            ammos.Add(slot.invItem.ammo);
                        }
                    }
                }

                // Water prefab: component flag, else name heuristic (deathDrop_water).
                var netId = bag.GetComponent<Sync.DeathBagNetworkId>();
                bool inWater = netId != null && netId.InWater;
                if (!inWater)
                {
                    string n = bag.gameObject.name ?? "";
                    inWater = n.IndexOf("water", System.StringComparison.OrdinalIgnoreCase) >= 0;
                }

                string bagId = Sync.DeathBagNetworkId.GetOrAssignBagId(bag.gameObject, inWater);
                if (IsDeathBagLooted(bagId))
                    continue;
                // Skip empty bags (already looted, awaiting destroy)
                if (types.Count == 0)
                    continue;

                RegisterDeathBag(bagId, bag);

                var msg = new DeathBagSpawnMessage
                {
                    PosX = pos.x,
                    PosY = pos.y,
                    PosZ = pos.z,
                    InWater = inWater,
                    ExpAmount = bag.expAmount,
                    ItemCount = types.Count,
                    ItemTypes = types.ToArray(),
                    ItemAmounts = amounts.ToArray(),
                    ItemDurabilities = durabilities.ToArray(),
                    ItemAmmos = ammos.ToArray(),
                    BagId = bagId
                };

                SendToPlayer(targetPlayerId, NetMessageType.DeathBagSpawn,
                    w => msg.Serialize(w), LiteNetLib.DeliveryMethod.ReliableOrdered);
                sent++;
            }

            if (sent > 0)
                ModRuntime.LegacyInfo($"[Death] Synced {sent} existing death bag(s) to player {targetPlayerId}");
            else
                ModRuntime.LegacyInfo($"[Death] No existing death bags to sync");
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
                        DeathStateTracker.OnRemoteDisconnected(playerId);
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
            if (!reader.TryGetByte(out byte messageType))
                return;

            var type = (NetMessageType)messageType;
            byte[] payload = reader.GetRemainingBytes();

            // Track which peer sent this message so handlers can look up PlayerId
            _currentReceivePeer = peer;
            _currentReceivePlayerId = GetPlayerId(peer);

            using (new NetworkApplyGuard())
            {
                try
                {
                    switch (type)
                    {
                        case NetMessageType.Handshake:
                            HandleHandshake(HandshakeMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.PlayerState:
                            HandlePlayerState(PlayerStateMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.WorldSession:
                            HandleWorldSession(WorldSessionMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.PhysicsState:
                            HandlePhysicsState(PhysicsStateMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.ItemSpawn:
                            HandleItemSpawn(ItemSpawnMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.LightState:
                            HandleLightState(LightStateMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.EntityState:
                            HandleEntityState(EntityStateMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.PlayerAttack:
                            HandlePlayerAttack(PlayerAttackMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.DamagePlayer:
                            HandleDamagePlayer(DamagePlayerMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.PlayerDied:
                            HandlePlayerDied(PlayerDiedMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.DeathBagSpawn:
                            HandleDeathBagSpawn(DeathBagSpawnMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.DeathBagLooted:
                            HandleDeathBagLooted(DeathBagLootedMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.NightDeathState:
                            HandleNightDeathState(NightDeathStateMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.ContainerItem:
                            HandleContainerItem(ContainerItemMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.BarricadeEvent:
                            HandleBarricadeEvent(BarricadeEventMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.WorkbenchLevel:
                            HandleWorkbenchLevel(WorkbenchLevelMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.JournalItem:
                            HandleJournalItem(JournalItemMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.FriendlyFire:
                            HandleFriendlyFire(FriendlyFireMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.PlayerSound:
                            HandlePlayerSound(PlayerSoundMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.PlayerScare:
                            HandlePlayerScare(PlayerScareMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.PlayerEffectSync:
                            HandlePlayerEffectSync(PlayerEffectSyncMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.DragSync:
                            HandleDragSync(DragSyncMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.SaveSync:
                            HandleSaveSync();
                            break;
                        case NetMessageType.TimeSync:
                            HandleTimeSync(TimeSyncMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.EntitySound:
                            HandleEntitySound(EntitySoundMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.WorldObjectRemoved:
                            HandleWorldObjectRemoved(WorldObjectRemovedMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.PlayerLightState:
                            HandlePlayerLightState(PlayerLightStateMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.ThrowableSpawn:
                            HandleThrowableSpawn(ThrowableSpawnMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.ExplosionTrigger:
                            HandleExplosionTrigger(ExplosionTriggerMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.PlayerAudio:
                            HandlePlayerAudio(PlayerAudioMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.GasTrailSpawn:
                            HandleGasTrailSpawn(GasTrailSpawnMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.GasIgnite:
                            HandleGasIgnite(GasIgniteMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.PlayerAnimation:
                            HandlePlayerAnimation(PlayerAnimationMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.PlayerAnimLibrary:
                            HandlePlayerAnimLibrary(PlayerAnimLibraryMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.BulletImpact:
                            HandleBulletImpact(BulletImpactMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.PlayerFiredWeapon:
                            HandlePlayerFiredWeapon(PlayerFiredWeaponMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.DroppedItemSpawn:
                            HandleDroppedItemSpawn(DroppedItemSpawnMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.DroppedItemPickup:
                            HandleDroppedItemPickup(DroppedItemPickupMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.SawState:
                            HandleSawState(SawStateMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.FeederState:
                            HandleFeederState(FeederStateMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.LureState:
                            HandleLureState(LureStateMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.SleepEndRequest:
                            HandleSleepEndRequest(SleepEndRequestMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.AfterNightEndRequest:
                            HandleAfterNightEndRequest(AfterNightEndRequestMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.PeerRoster:
                            HandlePeerRoster(PeerRosterMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.HostHandoff:
                            HandleHostHandoff(HostHandoffMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.WorkbenchLock:
                            HandleWorkbenchLock(WorkbenchLockMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.ShadowEvent:
                            HandleShadowEvent(ShadowEventMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.ShadowSpawn:
                            HandleShadowSpawn(ShadowSpawnMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.ScenarioSync:
                            HandleScenarioSync(ScenarioSyncMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.ScenarioEventFired:
                            HandleScenarioEventFired(ScenarioEventFiredMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.EntityBurning:
                            HandleEntityBurning(EntityBurningMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.LiquidStopBurning:
                            HandleLiquidStopBurning(LiquidStopBurningMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.ExplosionSpawnObject:
                            HandleExplosionSpawnObject(ExplosionSpawnObjectMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.PlayerBurning:
                            HandlePlayerBurning(PlayerBurningMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.FlagSync:
                            HandleFlagSync(FlagSyncMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.TradeSync:
                            HandleTradeSync(TradeSyncMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.TradeInventorySync:
                            HandleTradeInventorySync(TradeInventorySyncMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.DialogOutcomeSync:
                            HandleDialogOutcomeSync(DialogOutcomeSyncMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.MeleeWorldHit:
                            HandleMeleeWorldHit(MeleeWorldHitMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.DreamStarted:
                            HandleDreamStarted(DreamStartedMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.DreamEnded:
                            HandleDreamEnded(DreamEndedMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.DreamStartRequest:
                            HandleDreamStartRequest(DreamStartRequestMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.DreamItemPickup:
                            HandleDreamItemPickup(DreamItemPickupMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.DreamAudio:
                            HandleDreamAudio(DreamAudioMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.DreamEntered:
                            HandleDreamEntered(DreamEnteredMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.DreamSessionBulk:
                            HandleDreamSessionBulk(DreamSessionBulkMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.DreamChainStart:
                            HandleDreamChainStart(DreamChainStartMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.FinalDreamsceneDeath:
                            HandleFinalDreamsceneDeath(FinalDreamsceneDeathMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.SceneLoad:
                            HandleSceneLoad(SceneLoadMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.CutsceneSync:
                            HandleCutsceneSync(CutsceneSyncMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.ChapterTransition:
                            HandleChapterTransition(ChapterTransitionMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.ExamineObject:
                            HandleExamineObject(ExamineObjectMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.ConstructibleConstruction:
                            HandleConstructible(ConstructibleMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.ClientStateBackup:
                            HandleClientStateBackup(ClientStateBackupMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.InteractiveItemSwitch:
                            HandleInteractiveItemSwitch(InteractiveItemSwitchMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.PadlockUnlock:
                            HandlePadlockUnlock(PadlockUnlockMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.LockedUnlock:
                            HandleLockedUnlock(LockedUnlockMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.GameEventsFired:
                            HandleGameEventsFired(GameEventsFiredMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.HideoutUpgrade:
                            HandleHideoutUpgrade(HideoutUpgradeMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.MapMarker:
                            HandleMapMarker(MapMarkerMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.MapMarkerRemove:
                            HandleMapMarkerRemove(MapMarkerRemoveMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.MapElementDiscovered:
                            HandleMapElementDiscovered(MapElementDiscoveredMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.OxygenTankStash:
                            HandleOxygenTankStash(OxygenTankStashMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.CompressorTankConvert:
                            HandleCompressorTankConvert(CompressorTankConvertMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.JournalBulkSync:
                            HandleJournalBulkSync(JournalBulkSyncMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.ShadowStateUpdate:
                            HandleShadowStateUpdate(ShadowStateUpdateMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.ContainerStateRequest:
                            HandleContainerStateRequest(ContainerStateRequestMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.ContainerStateSync:
                            HandleContainerStateSync(ContainerStateSyncMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.ContainerTakeDenied:
                            HandleContainerTakeDenied(ContainerTakeDeniedMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.ReputationSync:
                            HandleReputationSync(ReputationSyncMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.DoorOpen:
                            HandleDoorOpen(DoorOpenMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.LocationEnter:
                            HandleLocationEnter(LocationEnterMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.LocationExit:
                            HandleLocationExit(LocationExitMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.EntitySpawn:
                            HandleEntitySpawn(EntitySpawnMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.TrapTriggered:
                            HandleTrapTriggered(TrapTriggeredMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.TrapBulk:
                            HandleTrapBulk(TrapBulkMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.ThrowableDespawn:
                            HandleThrowableDespawn(ThrowableDespawnMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.RemotePlayerForward:
                            {
                                var fwd = RemotePlayerForwardMessage.Deserialize(new NetReader(payload));
                                int saved = _currentReceivePlayerId;
                                _currentReceivePlayerId = fwd.OriginalPlayerId;
                                _isForwardedMessage = true;
                                try
                                {
                                    DispatchRemotePlayerForward((NetMessageType)fwd.InnerType, fwd.InnerPayload);
                                }
                                finally
                                {
                                    _isForwardedMessage = false;
                                    _currentReceivePlayerId = saved;
                                }
                                break;
                            }
                        case NetMessageType.VaultState:
                            HandleVaultState(VaultStateMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.WeatherSync:
                            HandleWeatherSync(WeatherSyncMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.FlagBulkSync:
                            HandleFlagBulkSync(FlagBulkSyncMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.ReputationBulkSync:
                            HandleReputationBulkSync(ReputationBulkSyncMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.ScenarioStateSync:
                            HandleScenarioStateSync(ScenarioSyncMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.HideoutStateSync:
                            HandleHideoutStateSync(HideoutStateSyncMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.WorkbenchLevelSync:
                            HandleWorkbenchLevelSync(WorkbenchLevelMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.MapStateSync:
                            HandleMapStateSync(MapStateSyncMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.PlayerSkillsSync:
                            HandlePlayerSkillsSync(PlayerSkillsSyncMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.WorldSaveBegin:
                            _worldSaveShare?.HandleBegin(WorldSaveBeginMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.WorldSaveChunk:
                            _worldSaveShare?.HandleChunk(WorldSaveChunkMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.WorldSaveEnd:
                            _worldSaveShare?.HandleEnd(WorldSaveEndMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.WorldRequest:
                            HandleWorldRequest(WorldRequestMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.ChatMessage:
                            {
                                var chat = ChatMessagePayload.Deserialize(new NetReader(payload));
                                // Sanitize peer input (Yokyy had no length/content clamp)
                                if (chat.Message != null && chat.Message.Length > 160)
                                    chat.Message = chat.Message.Substring(0, 160);
                                if (chat.SenderName != null && chat.SenderName.Length > 32)
                                    chat.SenderName = chat.SenderName.Substring(0, 32);
                                // Skip echo of our own send (we already drew locally)
                                if (chat.SenderId != _localPlayerId)
                                    ChatHud.OnRemote(chat);
                                break;
                            }
                        case NetMessageType.DialogNpcLock:
                            HandleDialogNpcLock(DialogNpcLockMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.DialogTreeState:
                            HandleDialogTreeState(DialogTreeStateMessage.Deserialize(new NetReader(payload)));
                            break;
                        default:
                            ModRuntime.Log?.LogWarning($"[Network] Unhandled message type: {type}");
                            break;
                    }
                }
                catch (Exception ex)
                {
                    ModLog.Error(LogCat.Network, $"Error handling {type}", ex);
                }
                finally
                {
                    _isForwardedMessage = false;
                }
            }

            // === Forward client messages to other clients (3+ support) ===
            if (_suppressForwardThisMessage)
            {
                _suppressForwardThisMessage = false;
                return;
            }

            if (!_isForwardedMessage && _role == NetworkRole.Host && _currentReceivePlayerId > 0)
            {
                if (_forwardableMap.TryGetValue(type, out var fwdKind))
                {
                    if (fwdKind == ForwardableKind.Direct)
                    {
                        // Direct rebroadcast must be reliable (default SendToAllExcept is Unreliable).
                        // PutRaw: payload is already the message body — length-prefix would break deserializers (3+ peers).
                        SendToAllExcept(_currentReceivePlayerId, type, w => w.PutRaw(payload),
                            DeliveryMethod.ReliableOrdered);
                    }
                    else
                    {
                        var fwd = new RemotePlayerForwardMessage
                        {
                            OriginalPlayerId = _currentReceivePlayerId,
                            InnerType = (byte)type,
                            InnerPayload = payload
                        };
                        SendToAllExcept(_currentReceivePlayerId, NetMessageType.RemotePlayerForward,
                            w => fwd.Serialize(w), DeliveryMethod.ReliableOrdered);
                    }
                }
            }
        }
    }
}

