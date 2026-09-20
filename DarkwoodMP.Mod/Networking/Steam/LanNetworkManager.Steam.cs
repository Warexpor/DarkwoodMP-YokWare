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
    /// Steam SNS backend, separate from LiteNetLib LAN. Both transports use the same Horde messages.
    /// Host migration uses SteamID roster + ConnectP2P (same elect/promote as LAN).
    /// </summary>
    public sealed partial class LanNetworkManager
    {
        private SteamCoopTransport _steam;
        private ConnectionBackend _backend = ConnectionBackend.None;
        private readonly Dictionary<int, CSteamID> _steamPeers = new Dictionary<int, CSteamID>();
        private readonly Dictionary<ulong, int> _steamIdToPlayer = new Dictionary<ulong, int>();
        private readonly List<int> _steamSoftReconnectProxyIds = new List<int>(8);
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

        internal int PeerCount => IsSteamSession ? _steamPeers.Count : _peers.Count;

        internal IEnumerable<int> EnumeratePeerIds()
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

        /// <summary>Host: Steam lobby + SteamNetworkingSockets listen. Separate from <see cref="StartHost"/>.</summary>
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

            // Ensure SNS callbacks exist before StartHost so +connect_lobby is parsed.
            _ = Steam;

            if (!Steam.StartHost())
            {
                StatusText = "Steam host failed";
                _role = NetworkRole.Offline;
                _backend = ConnectionBackend.None;
                return;
            }

            StatusText = "Steam host — creating lobby…";
            ModLog.Event(LogCat.Network,
                "Hosting via Steam SNS | v" + PluginInfo.DisplayVersion
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
                _steamSoftReconnectProxyIds.Clear();
                foreach (int id in _remoteProxies.Keys)
                    _steamSoftReconnectProxyIds.Add(id);
                for (int i = 0; i < _steamSoftReconnectProxyIds.Count; i++)
                    WorldProxyHandlers.DestroyRemoteProxy(_steamSoftReconnectProxyIds[i]);
                _remoteProxies.Clear();
                _remotePlayers.Clear();
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

        /// <summary>Register Steam lobby/SNS callbacks early (overlay invites before HOST/JOIN).</summary>
        public void EnsureSteamCallbacks()
        {
            if (!SteamCoopTransport.IsSteamReady(out _))
                return;
            Steam.EnsureCallbacks();
        }

        /// <summary>One-shot: join lobby from Steam `+connect_lobby` launch arg while offline on title.</summary>
        public bool TryConsumePendingSteamLaunchLobby()
        {
            if (_role != NetworkRole.Offline)
                return false;
            Steam.EnsureCallbacks();
            ulong lobby = Steam.ConsumePendingLaunchLobby();
            if (lobby == 0)
                return false;
            ModLog.Event(LogCat.Session, "Steam +connect_lobby → join " + lobby);
            ConnectSteamLobby(new CSteamID(lobby));
            return true;
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

            int hostKey = _hostPlayerId > 0 ? _hostPlayerId : 1;
            _steamPeers[hostKey] = hostSid;
            _steamIdToPlayer[hostSid.m_SteamID] = hostKey;
            Steam.AcceptSession(hostSid);
            CompleteClientPeerJoin();
            StatusText = _migrationInProgress
                ? "Steam migrating — handshaking…"
                : "Steam connected — handshaking…";
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
            if (_suppressHostMigration)
                return;
            if (_migrationInProgress)
                return;
            ModLog.Event(LogCat.Network, "Steam host left lobby — attempting host grant");
            TryBeginHostMigration("steam host left lobby");
        }

        internal void OnSteamSessionFailed(CSteamID remote)
        {
            if (_backend != ConnectionBackend.Steam)
                return;
            if (_steamIdToPlayer.TryGetValue(remote.m_SteamID, out int playerId))
            {
                HandleSteamPeerDisconnected(playerId, "SNS fail");
                return;
            }

            // Client mid-join: SNS died before OnSteamLobbyReady mapped the host peer.
            if (_role == NetworkRole.Client)
            {
                ModLog.Warn(LogCat.Network, "Steam SNS fail before peer map — tearing join");
                _suppressHostMigration = true;
                StopNetwork();
                StatusText = "Steam SNS failed";
            }
        }

        internal void CloseAllSteamSessions()
        {
            foreach (var kvp in _steamPeers)
            {
                try { Steam.CloseSession(kvp.Value); }
                catch { /* tear */ }
            }
        }

    }
}
