using System;
using System.Collections.Generic;
using DWMPHorde.Logging;
using DWMPHorde.Networking.Steam;
using LiteNetLib;
using Steamworks;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>
    /// Steam P2P backend — separate from LiteNetLib LAN. Same Ironbark/Horde messages over SteamNetworking.
    /// Host migration stays LAN-only (Steam path tears cleanly on host leave).
    /// </summary>
    public sealed partial class LanNetworkManager
    {
        private SteamCoopTransport _steam;
        private ConnectionBackend _backend = ConnectionBackend.None;
        private readonly Dictionary<int, CSteamID> _steamPeers = new Dictionary<int, CSteamID>();
        private readonly Dictionary<ulong, int> _steamIdToPlayer = new Dictionary<ulong, int>();
        private CSteamID _currentReceiveSteamId = CSteamID.Nil;

        public ConnectionBackend Backend => _backend;
        public bool IsSteamSession => _backend == ConnectionBackend.Steam;
        public string SteamLobbyIdText => _steam != null && _steam.LobbyId.IsValid()
            ? _steam.LobbyIdString
            : "";

        private SteamCoopTransport Steam
        {
            get
            {
                if (_steam == null)
                    _steam = new SteamCoopTransport(this);
                return _steam;
            }
        }

        private int PeerCount => IsSteamSession ? _steamPeers.Count : _peers.Count;

        private IEnumerable<int> EnumeratePeerIds()
        {
            if (IsSteamSession)
            {
                foreach (int id in _steamPeers.Keys)
                    yield return id;
            }
            else
            {
                foreach (int id in _peers.Keys)
                    yield return id;
            }
        }

        private bool HasPeer(int playerId)
        {
            return IsSteamSession ? _steamPeers.ContainsKey(playerId) : _peers.ContainsKey(playerId);
        }

        private void RemovePeerSlot(int playerId)
        {
            if (IsSteamSession)
            {
                if (_steamPeers.TryGetValue(playerId, out CSteamID sid))
                {
                    _steamPeers.Remove(playerId);
                    _steamIdToPlayer.Remove(sid.m_SteamID);
                    Steam.CloseSession(sid);
                }
            }
            else
            {
                _peers.Remove(playerId);
            }
        }

        private void ClearAllPeerSlots()
        {
            if (_steamPeers.Count > 0)
            {
                foreach (var kvp in _steamPeers)
                    Steam.CloseSession(kvp.Value);
            }
            _steamPeers.Clear();
            _steamIdToPlayer.Clear();
            _peers.Clear();
            _currentReceiveSteamId = CSteamID.Nil;
        }

        /// <summary>Host: friends-only Steam lobby + P2P. Separate from <see cref="StartHost"/>.</summary>
        public void StartHostSteam()
        {
            StopNetwork();
            if (!SteamCoopTransport.IsSteamReady(out string fail))
            {
                StatusText = "Steam unavailable: " + fail;
                _role = NetworkRole.Offline;
                _backend = ConnectionBackend.None;
                return;
            }

            _role = NetworkRole.Host;
            _localPlayerId = 1;
            _hostPlayerId = 1;
            _backend = ConnectionBackend.Steam;
            NoteSessionPort(0);

            if (!Steam.StartHost())
            {
                StatusText = "Steam host failed";
                _role = NetworkRole.Offline;
                _backend = ConnectionBackend.None;
                return;
            }

            StatusText = "Steam host — creating lobby…";
            ModLog.Event(LogCat.Network,
                "Hosting via Steam P2P | v" + PluginInfo.DisplayVersion
                + " proto=" + PluginInfo.ProtocolVersion
                + " maxPlayers=" + (Config.ModConfig.MaxPlayers?.Value ?? 8));
        }

        /// <summary>Client: join a YokWare Steam lobby by id (ulong or steam://joinlobby/…).</summary>
        public void ConnectSteam(string lobbyIdRaw)
        {
            if (!Steam.TryParseLobbyId(lobbyIdRaw, out CSteamID lobbyId))
            {
                StatusText = "Invalid Steam lobby id";
                ModLog.Error(LogCat.Network, "ConnectSteam: bad lobby id '" + lobbyIdRaw + "'");
                return;
            }
            ConnectSteamLobby(lobbyId);
        }

        public void ConnectSteamLobby(CSteamID lobbyId)
        {
            bool softReconnect = false;
            try
            {
                softReconnect = !Core.mainMenu && Player.Instance != null && !Core.loadingGame;
            }
            catch { softReconnect = false; }

            if (softReconnect)
            {
                ModLog.Event(LogCat.Network, "ConnectSteam soft reconnect (keep world)");
                StopTransportOnly("steam phase3 soft reconnect");
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

            if (!SteamCoopTransport.IsSteamReady(out string fail))
            {
                StatusText = "Steam unavailable: " + fail;
                _role = NetworkRole.Offline;
                _backend = ConnectionBackend.None;
                return;
            }

            _role = NetworkRole.Client;
            _hostPlayerId = 1;
            _backend = ConnectionBackend.Steam;
            NoteSessionPort(0);

            if (!Steam.JoinLobby(lobbyId))
            {
                StatusText = "Steam join failed";
                _role = NetworkRole.Offline;
                _backend = ConnectionBackend.None;
                return;
            }

            StatusText = "Steam joining lobby…";
            ModLog.Event(LogCat.Network,
                "Connecting via Steam lobby " + lobbyId.m_SteamID
                + " | v" + PluginInfo.DisplayVersion + " proto=" + PluginInfo.ProtocolVersion
                + (softReconnect ? " (soft)" : ""));
        }

        public void InviteSteamFriends()
        {
            if (!IsSteamSession || _role != NetworkRole.Host)
                return;
            Steam.OpenInviteOverlay();
        }

        // --- callbacks from SteamCoopTransport ---

        internal void OnSteamLobbyReady(CSteamID lobbyId, bool isHost)
        {
            if (_backend != ConnectionBackend.Steam)
                return;

            if (isHost)
            {
                StatusText = "Steam hosting lobby " + lobbyId.m_SteamID;
                ModLog.BannerSessionStart();
                // Persist last lobby id for UI convenience.
                if (Config.ModConfig.SteamLobbyId != null)
                    Config.ModConfig.SteamLobbyId.Value = lobbyId.m_SteamID.ToString();
                return;
            }

            // Client: map host steam id and run client peer-join (sends Handshake).
            CSteamID hostSid = Steam.HostSteamId;
            if (!hostSid.IsValid())
            {
                StatusText = "Steam: no host steam id";
                StopNetwork();
                return;
            }

            _steamPeers[1] = hostSid;
            _steamIdToPlayer[hostSid.m_SteamID] = 1;
            Steam.AcceptSession(hostSid);
            CompleteClientPeerJoin();
            StatusText = "Steam connected — handshaking…";
        }

        internal void OnSteamLobbyFailed(string reason)
        {
            StatusText = "Steam lobby failed: " + reason;
            ModLog.Error(LogCat.Network, "Steam lobby failed: " + reason);
            _suppressHostMigration = true;
            // Full tear so peer maps / lobby / role cannot stick half-open.
            StopNetwork();
            StatusText = "Steam lobby failed: " + reason;
        }

        internal void OnSteamHostLeftLobby()
        {
            if (_backend != ConnectionBackend.Steam || _role != NetworkRole.Client)
                return;
            // No LAN-style host migration on Steam — clean stop.
            _suppressHostMigration = true;
            StopNetwork();
            StatusText = "Steam host left";
        }

        internal void OnSteamSessionFailed(CSteamID remote)
        {
            if (_backend != ConnectionBackend.Steam)
                return;
            if (!_steamIdToPlayer.TryGetValue(remote.m_SteamID, out int playerId))
                return;
            HandleSteamPeerDisconnected(playerId, "P2P fail");
        }

        internal void CloseAllSteamSessions()
        {
            foreach (var kvp in _steamPeers)
            {
                try { Steam.CloseSession(kvp.Value); }
                catch { /* tear */ }
            }
        }

        internal void OnSteamPacket(CSteamID remote, byte[] payload)
        {
            if (_backend != ConnectionBackend.Steam || payload == null || payload.Length == 0)
                return;

            // Host: first packet from unknown steam user → register peer (like OnPeerConnected).
            if (_role == NetworkRole.Host && !_steamIdToPlayer.ContainsKey(remote.m_SteamID))
            {
                if (!TryHostAcceptSteamPeer(remote))
                    return;
            }

            if (!_steamIdToPlayer.TryGetValue(remote.m_SteamID, out int playerId))
            {
                // Client: first packet from host before map (race) — bind host.
                if (_role == NetworkRole.Client && Steam.HostSteamId.IsValid() && remote == Steam.HostSteamId)
                {
                    _steamPeers[1] = remote;
                    _steamIdToPlayer[remote.m_SteamID] = 1;
                    playerId = 1;
                }
                else
                {
                    return;
                }
            }

            DispatchSteamPayload(remote, playerId, payload);
        }

        private bool TryHostAcceptSteamPeer(CSteamID remote)
        {
            int maxPlayers = Config.ModConfig.MaxPlayers?.Value ?? 8;
            if (maxPlayers < 2) maxPlayers = 8;
            // Host counts as 1.
            if (_steamPeers.Count + 1 >= maxPlayers)
            {
                ModLog.Warn(LogCat.Network, "Steam reject " + remote.m_SteamID + " — full");
                Steam.CloseSession(remote);
                return false;
            }

            if (Sync.DreamSyncManager.IsDreamActive
                && Config.ModConfig.AllowJoinDuringDream != null
                && !Config.ModConfig.AllowJoinDuringDream.Value)
            {
                ModLog.Warn(LogCat.Network, "Steam reject " + remote.m_SteamID + " — dream join blocked");
                Steam.CloseSession(remote);
                return false;
            }

            Steam.AcceptSession(remote);
            int playerId = _nextPlayerId++;
            _steamPeers[playerId] = remote;
            _steamIdToPlayer[remote.m_SteamID] = playerId;

            if (_handshakedPeers.Count == 0)
                _handshakeComplete = false;
            StatusText = $"Steam player {playerId} connected";
            ModLog.Event(LogCat.Network,
                $"Steam player {playerId} connected sid={remote.m_SteamID} (peers={_steamPeers.Count})");

            CompleteHostPeerJoin(playerId);
            return true;
        }

        /// <summary>Shared host post-connect (Handshake + WorldSession) for LAN and Steam.</summary>
        private void CompleteHostPeerJoin(int playerId)
        {
            SendToPlayer(playerId, NetMessageType.Handshake, w =>
            {
                new HandshakeMessage
                {
                    ProtocolVersion = PluginInfo.ProtocolVersion,
                    PlayerId = (short)playerId,
                    HostPlayerId = (short)_localPlayerId
                }.Serialize(w);
            }, DeliveryMethod.ReliableOrdered);

            WorldSessionMessage session = _worldSync.BuildHostSession();
            SendToPlayer(playerId, NetMessageType.WorldSession, w => session.Serialize(w),
                DeliveryMethod.ReliableOrdered);

            if (HostHasShareableWorld())
            {
                ModLog.Event(LogCat.Session,
                    "Peer " + playerId + " connected while host in-world — "
                    + "deferring gameplay bulk until after world share");
            }
            else
            {
                SendLateJoinGameplayBulk(playerId);
            }

            Connected?.Invoke();
        }

        /// <summary>Shared client post-connect (outbound Handshake) for LAN and Steam.</summary>
        private void CompleteClientPeerJoin()
        {
            _handshakeComplete = false;
            _handshakedPeers.Clear();

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

            SyncCurrentLightState();
            Connected?.Invoke();
        }

        private void DispatchSteamPayload(CSteamID remote, int playerId, byte[] payload)
        {
            if (payload == null || payload.Length < 1)
                return;

            var type = (NetMessageType)payload[0];
            byte[] body;
            if (payload.Length == 1)
            {
                body = new byte[0];
            }
            else
            {
                body = new byte[payload.Length - 1];
                Buffer.BlockCopy(payload, 1, body, 0, body.Length);
            }

            _currentReceivePeer = null;
            _currentReceiveSteamId = remote;
            _currentReceivePlayerId = playerId;
            if (IsConnected)
                ClientPerfProbe.NotePacketRx(type);
            try
            {
                ProcessInboundMessage(type, body);
            }
            finally
            {
                _currentReceiveSteamId = CSteamID.Nil;
            }
        }

        private void HandleSteamPeerDisconnected(int playerId, string reason)
        {
            // Reuse LAN disconnect cleanup by synthesizing the host branch.
            ModLog.Event(LogCat.Network, $"Steam player {playerId} disconnected: " + reason);

            var toRemove = new List<string>();
            foreach (var kv in _dragClaims)
            {
                if (kv.Value == playerId)
                    toRemove.Add(kv.Key);
            }
            foreach (string key in toRemove)
            {
                _dragClaims.Remove(key);
                RemoveRemoteDragIds(key);
                ReleaseRemoteDragKinematic(key);
                DWMPHorde.Audio.ItemMovingSoundHelper.ForceStopByName(key);
            }

            if (_role == NetworkRole.Host)
            {
                if (playerId > 0)
                {
                    Sync.WorkbenchOpenLock.HostReleaseAllForPlayer(this, playerId);
                    RemovePeerSlot(playerId);
                    _handshakedPeers.Remove(playerId);
                    bool wasLoadingOnly = _peersLoadingWorld.Contains(playerId)
                        && !_peersCoopReconnect.Contains(playerId)
                        && (!_awaitingLateJoinBulk.TryGetValue(playerId, out float seen) || seen <= 0f);
                    bool expectedJoinDetach = _peersLoadingWorld.Contains(playerId)
                        && !_peersCoopReconnect.Contains(playerId);

                    _awaitingLateJoinBulk.Remove(playerId);
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
                    if (!expectedJoinDetach && !wasLoadingOnly)
                    {
                        DeathStateTracker.OnRemoteDisconnected(playerId);
                        DeathStateTracker.TryResolveNightMorning("steam peer disconnect");
                    }
                    StatusText = $"Steam player {playerId} left ({_steamPeers.Count} remaining)";
                }
            }
            else
            {
                if (_suppressHostMigration)
                    return;
                // Steam: no host migration — stop.
                _suppressHostMigration = true;
                StopNetwork();
            }
        }

        private void PollSteamBackend()
        {
            if (_backend != ConnectionBackend.Steam || _steam == null || !_steam.IsActive)
                return;
            _steam.Poll();
        }

        private void ShutdownSteamBackend()
        {
            if (_steam != null && _steam.IsActive)
                _steam.Shutdown();
            // Do not clear peer maps here when called from ClearAllPeerSlots path —
            // always wipe steam routing so a later LAN session cannot leak Steam ids.
            _steamPeers.Clear();
            _steamIdToPlayer.Clear();
            _currentReceiveSteamId = CSteamID.Nil;
            if (_backend == ConnectionBackend.Steam)
                _backend = ConnectionBackend.None;
        }

        private bool SendSteamToPlayer(int playerId, byte[] data, DeliveryMethod method)
        {
            if (!_steamPeers.TryGetValue(playerId, out CSteamID sid))
                return false;
            return Steam.Send(sid, data, method);
        }

        private void DisconnectCurrentReceivePeer()
        {
            if (_currentReceivePeer != null)
            {
                _currentReceivePeer.Disconnect();
                return;
            }
            if (_currentReceiveSteamId.IsValid())
            {
                Steam.CloseSession(_currentReceiveSteamId);
                if (_steamIdToPlayer.TryGetValue(_currentReceiveSteamId.m_SteamID, out int pid))
                    HandleSteamPeerDisconnected(pid, "protocol mismatch");
            }
        }

        private int TryRebindPreferredSteamPlayerId(int provisionalId, int preferredId, CSteamID steamId)
        {
            if (preferredId <= 0 || preferredId == provisionalId || !steamId.IsValid())
                return provisionalId;
            if (preferredId == _localPlayerId)
                return provisionalId;
            if (_steamPeers.ContainsKey(preferredId))
                return provisionalId;

            _steamPeers.Remove(provisionalId);
            _steamPeers[preferredId] = steamId;
            _steamIdToPlayer[steamId.m_SteamID] = preferredId;
            if (_handshakedPeers.Remove(provisionalId))
                _handshakedPeers.Add(preferredId);
            if (_awaitingLateJoinBulk.TryGetValue(provisionalId, out float t))
            {
                _awaitingLateJoinBulk.Remove(provisionalId);
                _awaitingLateJoinBulk[preferredId] = t;
            }
            if (_pendingHeavyLateJoinBulk.TryGetValue(provisionalId, out int heavyPhase))
            {
                _pendingHeavyLateJoinBulk.Remove(provisionalId);
                _pendingHeavyLateJoinBulk[preferredId] = heavyPhase;
            }
            if (_peersLoadingWorld.Remove(provisionalId))
                _peersLoadingWorld.Add(preferredId);
            if (_peersCoopReconnect.Remove(provisionalId))
                _peersCoopReconnect.Add(preferredId);

            if (preferredId >= _nextPlayerId)
                _nextPlayerId = preferredId + 1;

            ModLog.Event(LogCat.Network,
                "Steam rebind peer id " + provisionalId + " → preferred " + preferredId);
            return preferredId;
        }
    }
}
