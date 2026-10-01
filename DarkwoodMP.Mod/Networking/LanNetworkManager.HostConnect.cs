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

        /// <summary>
        /// One place for LiteNetLib settings so host, join, and migration sockets behave the same.
        /// AutoRecycle: every receive path copies the payload (<c>GetRemainingBytes</c>) and never keeps
        /// the reader, so LiteNetLib can return it to its pool instead of leaving one garbage reader per
        /// packet. MTU discovery stays off (LiteNetLib default 1024): hot senders split to each peer's
        /// <c>GetMaxSinglePacketSize</c>, and discovery is known to confuse some routers / VPNs.
        /// </summary>
        private NetManager CreateNetManager() => new NetManager(this)
        {
            UnconnectedMessagesEnabled = false,
            DisconnectTimeout = 30000,
            AutoRecycle = true
        };

        public void StartHost(int port)
        {
            StopNetwork();
            _role = NetworkRole.Host;
            _localPlayerId = 1;
            _hostPlayerId = 1;
            _backend = ConnectionBackend.Lan;
            NoteSessionPort(port);
            _net = CreateNetManager();
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
                + " | " + PluginInfo.DisplayVersion + " proto=" + PluginInfo.ProtocolVersion
                + " maxPlayers=" + (Config.ModConfig.MaxPlayers?.Value ?? 8));
        }

        public void ConnectToHost(string address, int port)
        {
            LastClientSteamLobbyId = 0;
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
                    WorldProxyHandlers.DestroyRemoteProxy(id);
                _remoteProxies.Clear();
                _remotePlayers.Clear();
                // Proxies gone — drop sticky membership so host LocationEnter re-places.
                LocationHandlers?.ClearMembershipForSoftReconnect();
                PlayerLightFxHandlers?.ClearPendingPlayerLights();
                PlayerFXHandlers?.ClearAllPendingAnimLibraries();
                _handshakeComplete = false;
                _handshakedPeers.Clear();
                _rejectedPeers.Clear();
                _awaitingLateJoinBulk.Clear();
                _pendingHeavyLateJoinBulk.Clear();
                _peersLoadingWorld.Clear();
                _peersCoopReconnect.Clear();
                _hostWasShareableForWaitingClients = false;
                _hostWorldReadyEmitted = false;
                _clientHostWorldReady = false;
                NoteSoftReconnectAttempt(address, port, 0);
            }
            else
            {
                StopNetwork();
            }

            _role = NetworkRole.Client;
            _hostPlayerId = 1;
            _backend = ConnectionBackend.Lan;
            NoteSessionPort(port);
            _net = CreateNetManager();
            _net.Start();
            string key = Config.ModConfig.GetConnectionKey();
            _peers[1] = _net.Connect(address, port, key);
            StatusText = "Connecting to " + address + ":" + port;
            // Event line is IP-redacted under Public; Trace keeps detail for Dev/Trace only.
            ModLog.Event(LogCat.Network, "Connecting to " + address + ":" + port
                + " | " + PluginInfo.DisplayVersion + " proto=" + PluginInfo.ProtocolVersion
                + (softReconnect ? " (soft)" : ""));
            ModLog.Trace(LogCat.Network, () => "Connect target detail: " + address + ":" + port);
        }

        /// <param name="keepSteamLobby">
        /// Chapter resume, Steam host only: stay a member of the lobby through the scene load so
        /// returning clients can rejoin the same lobby id. Everything else leaves the lobby.
        /// </param>
        public void StopNetwork(bool keepSteamLobby = false)
        {
            // This is an intentional teardown, not a host-crash migration.
            _suppressHostMigration = true;

            // Before tearing the wire: host flushes sav.dat (next session ownership),
            // then client snapshots exit pos/inv. Skip title/offline-load tear.
            // Migration promote still does NOT auto-Save (survivor slot corruption).
            TryHostWorldSaveCheckpointOnExit();
            TrySnapshotClientBackupOnExit();

            // Snapshot for public session-stop line before we wipe peers/ids
            NetworkRole wasRole = _role;
            int wasLocalId = _localPlayerId;
            int wasPeers = PeerCount;

            NetworkResetRegistry.ResetAll();
            ResetCombatSessionState();
            ResetSessionNetworkState();
            foreach (int id in new List<int>(_remoteProxies.Keys))
                WorldProxyHandlers.DestroyRemoteProxy(id);
            _remoteProxies.Clear();
            PlayerLightFxHandlers?.ClearPendingPlayerLights();
                PlayerFXHandlers?.ClearAllPendingAnimLibraries();
            _wasDragging = false;
            _lastDraggedItemName = null;
            _dragScrapeActive = false;
            _dragScrapeQuietSince = -1f;
            PlayerInteractHandlers?.ClearDragSessionState();
            DWMPHorde.Audio.MovingObjectSoundService.Reset();
            _handshakeComplete = false;
            _handshakedPeers.Clear();
            _rejectedPeers.Clear();
            // Clean up per-player light objects before clearing state
            foreach (var state in _remotePlayers.Values)
            {
                if (state.FlareLight != null)
                    DestroyRemoteFlareLight(state.PlayerId);
                if (state.ItemLight != null)
                    DestroyRemoteItemLight(state.PlayerId);
            }
            _remotePlayers.Clear();
            ShutdownSteamBackend(leaveLobby: !keepSteamLobby);
            ClearAllPeerSlots();
            ClearAllStableClientKeys();
            ResetWorldIdentityState();
            _sendTimer = 0f;
            _nextPlayerStateSequence = 0;
            _nextPhysicsStateSequence = 0;
            ResetInboundSequenceState();
            _physicsSendTimer = 0f;
            _timeSyncTimer = 0f;
            _shadowBroadcastTimer = 0f;
            _effectSyncTimer = 0f;
            ResetLocalLightSendCache();
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

        private NetPeer _currentReceivePeer;
        private int _currentReceivePlayerId = -1;

        /// <summary>GUIDs of dropped items that have already been picked up (host-authoritative).
        /// Prevents item multiplication when both players pick up the same GUID
        /// network message is processed.</summary>
        internal static readonly HashSet<string> _consumedDropGuids = new HashSet<string>(); // reset-in: ResetConsumedDropGuids
    }
}
