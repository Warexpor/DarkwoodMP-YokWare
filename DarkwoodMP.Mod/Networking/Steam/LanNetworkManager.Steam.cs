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
        /// <summary>Steam SNS peers by player id (created in Awake).</summary>
        private SteamPeerTable _steamPeers;
        private readonly List<int> _steamSoftReconnectProxyIds = new List<int>(8);

        private static bool HostRequiresPassword()
            => !string.IsNullOrEmpty(Config.ModConfig.HostPassword?.Value?.Trim());

        /// <summary>Unverified Steam peers get nothing but the handshake while this is true.</summary>
        private bool SteamPasswordGateActive() => _role == NetworkRole.Host && HostRequiresPassword();

        /// <summary>The active backend's peer table.</summary>
        private IPeerTable Peers => IsSteamSession ? (IPeerTable)_steamPeers : _lanPeers;

        /// <summary>A lobby member that never sends a valid Handshake is dropped, freeing its player slot.</summary>
        private const float SteamUnauthGraceSec = 20f;

        private void TickSteamUnauthTimeout()
        {
            if (!_steamPeers.HasUnauthenticated || _role != NetworkRole.Host)
                return;
            List<int> expired = _steamPeers.CollectExpiredUnauthenticated(UnityEngine.Time.unscaledTime, SteamUnauthGraceSec);
            if (expired == null)
                return;
            foreach (int playerId in expired)
            {
                ModLog.Warn(LogCat.Network,
                    "Steam peer p" + playerId + " never proved the host password within "
                    + (int)SteamUnauthGraceSec + "s — dropped");
                RemovePeerSlot(playerId);
            }
        }
        private CSteamID _currentReceiveSteamId = CSteamID.Nil;

        /// <summary>
        /// Steam lobby id of the last client join (0 = last join was LAN, or none). Survives StopNetwork
        /// so ENTER WORLD can still arm the phase-3 reconnect after the transfer link already dropped.
        /// </summary>
        internal ulong LastClientSteamLobbyId { get; private set; }

        public ConnectionBackend Backend => _backend;
        public bool IsSteamSession => _backend == ConnectionBackend.Steam;

        /// <summary>SteamID64 of the peer whose message is currently being handled (0 if LAN/none).</summary>
        internal ulong CurrentReceiveSteamId64 =>
            _currentReceiveSteamId.IsValid() ? _currentReceiveSteamId.m_SteamID : 0UL;

        /// <summary>Host map: network PlayerId → SteamID64 when this is a Steam session.</summary>
        internal bool TryGetSteamIdForPlayer(int playerId, out ulong steamId)
        {
            steamId = 0;
            if (!IsSteamSession || playerId <= 0)
                return false;
            if (_steamPeers.TryGetSteamId(playerId, out CSteamID sid) && sid.IsValid() && sid.m_SteamID != 0)
            {
                steamId = sid.m_SteamID;
                return true;
            }
            return false;
        }

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

        internal int PeerCount => Peers.Count;

        /// <summary>
        /// Player ids that may receive session traffic: refused peers and (Steam) lobby members that
        /// have not proved the host password are left out; they only get messages sent to them by id.
        /// </summary>
        internal IEnumerable<int> EnumeratePeerIds()
        {
            IPeerTable peers = Peers;
            IReadOnlyList<int> ids = peers.Ids;
            for (int i = 0; i < ids.Count; i++)
            {
                int id = ids[i];
                if (!peers.IsRoutable(id))
                    continue;
                if (_session.Link.Rejected.Count > 0 && _session.Link.Rejected.Contains(id))
                    continue;
                yield return id;
            }
        }

        internal bool HasPeer(int playerId) => Peers.Contains(playerId);

        private void RemovePeerSlot(int playerId)
        {
            if (IsSteamSession)
            {
                if (_steamPeers.TryGetSteamId(playerId, out CSteamID sid))
                {
                    _steamPeers.Remove(playerId);
                    Steam.CloseSession(sid);
                }
            }
            else
            {
                _lanPeers.Remove(playerId);
            }
        }

        private void ClearAllPeerSlots()
        {
            IReadOnlyList<int> steamIds = _steamPeers.Ids;
            for (int i = 0; i < steamIds.Count; i++)
            {
                if (_steamPeers.TryGetSteamId(steamIds[i], out CSteamID sid))
                    Steam.CloseSession(sid);
            }
            _steamPeers.Clear();
            _lanPeers.Clear();
            _currentReceiveSteamId = CSteamID.Nil;
        }

        /// <summary>Host: Steam lobby + SteamNetworkingSockets listen. Separate from <see cref="StartHost"/>.</summary>
        /// <param name="reuseLobbyId">
        /// Chapter resume: the lobby this host kept open through the scene load (0 = create a new one).
        /// Returning clients rejoin that same id, so it must survive.
        /// </param>
        public void StartHostSteam(ulong reuseLobbyId = 0)
        {
            // Half-applied patches would sync half the game: refuse to start a session.
            if (!ModRuntime.CanStartSession(out string patchBlock)) { StatusText = patchBlock; return; }
            // The kept lobby is not an active transport (StopNetwork(keepSteamLobby) shut the
            // sockets), so this StopNetwork leaves it alone.
            StopNetwork();
            if (!SteamCoopTransport.IsSteamReady(out string fail))
            {
                // Full stop, not a bare role flip: a soft reconnect kept the session state
                // (SessionSettings, death marks, pendings) that must not leak into an offline world.
                StopNetwork();
                StatusText = "Steam unavailable: " + fail;
                return;
            }

            _role = NetworkRole.Host;
            _localPlayerId = 1;
            _hostPlayerId = 1;
            _backend = ConnectionBackend.Steam;
            NoteSessionPort(0);

            // Ensure SNS callbacks exist before StartHost so +connect_lobby is parsed.
            _ = Steam;

            if (!Steam.StartHost(new CSteamID(reuseLobbyId)))
            {
                StatusText = "Steam host failed";
                _role = NetworkRole.Offline;
                _backend = ConnectionBackend.None;
                return;
            }

            // Kept lobby re-listened: OnSteamLobbyReady already set the status.
            if (!Steam.LobbyId.IsValid())
                StatusText = "Steam host — creating lobby…";
            ModLog.Event(LogCat.Network,
                "Hosting via Steam SNS | " + PluginInfo.DisplayVersion
                + " proto=" + PluginInfo.ProtocolVersion
                + " maxPlayers=" + (Config.ModConfig.MaxPlayers?.Value ?? 8));
        }

        /// <summary>Client: join a YokWare Steam lobby by id (ulong or steam://joinlobby/…).</summary>
        public void ConnectSteam(string lobbyIdRaw)
        {
            // Half-applied patches would sync half the game: refuse to start a session.
            if (!ModRuntime.CanStartSession(out string patchBlock)) { StatusText = patchBlock; return; }
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
            LastClientSteamLobbyId = lobbyId.m_SteamID;
            bool softReconnect = false;
            try
            {
                softReconnect = !GameScreen.AtTitle && Player.Instance != null && !Core.loadingGame;
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
                    WorldProxyLifecycleHandlers.DestroyRemoteProxy(_steamSoftReconnectProxyIds[i]);
                _remoteProxies.Clear();
                _remotePlayers.Clear();
                LocationEnterExitHandlers?.ClearMembershipForSoftReconnect();
                PlayerLightFxApplyHandlers?.ClearPendingPlayerLights();
                PlayerFXHandlers?.ClearAllPendingAnimLibraries();
                _session.Link = new LinkState();
                _session.HostWasShareableForWaitingClients = false;
                _session.HostWorldReadyEmitted = false;
                _session.ClientHostWorldReady = false;
                NoteSoftReconnectAttempt(null, 0, lobbyId.m_SteamID);
            }
            else
            {
                StopNetwork();
            }

            if (!SteamCoopTransport.IsSteamReady(out string fail))
            {
                // Full stop, not a bare role flip: a soft reconnect kept the session state
                // (SessionSettings, death marks, pendings) that must not leak into an offline world.
                StopNetwork();
                StatusText = "Steam unavailable: " + fail;
                return;
            }

            _role = NetworkRole.Client;
            _hostPlayerId = 1;
            _backend = ConnectionBackend.Steam;
            NoteSessionPort(0);

            if (!Steam.JoinLobby(lobbyId))
            {
                StopNetwork();
                StatusText = "Steam join failed";
                _role = NetworkRole.Offline;
                _backend = ConnectionBackend.None;
                return;
            }

            StatusText = "Steam joining lobby…";
            ModLog.Event(LogCat.Network,
                "Connecting via Steam lobby " + lobbyId.m_SteamID
                + " | " + PluginInfo.DisplayVersion + " proto=" + PluginInfo.ProtocolVersion
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
            _steamPeers.Set(hostKey, hostSid);
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
            // In-world reconnect: the lobby may simply not be back yet — bounded retry first.
            if (_role == NetworkRole.Client && !_suppressHostMigration && !_migrationInProgress
                && _softReconnectTries > 0)
            {
                OnClientLinkFailed("Steam lobby failed: " + reason);
                return;
            }
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
            if (_steamPeers.TryGetPlayerId(remote.m_SteamID, out int playerId))
            {
                HandleSteamPeerDisconnected(playerId, "SNS fail");
                return;
            }

            // Client mid-join: SNS died before OnSteamLobbyReady mapped the host peer.
            if (_role == NetworkRole.Client)
            {
                ModLog.Warn(LogCat.Network, "Steam SNS fail before peer map — tearing join");
                if (!_suppressHostMigration && !_migrationInProgress && _softReconnectTries > 0)
                {
                    OnClientLinkFailed("Steam SNS failed");
                    return;
                }
                _suppressHostMigration = true;
                StopNetwork();
                StatusText = "Steam SNS failed";
            }
        }

        internal void CloseAllSteamSessions()
        {
            IReadOnlyList<int> ids = _steamPeers.Ids;
            for (int i = 0; i < ids.Count; i++)
            {
                if (!_steamPeers.TryGetSteamId(ids[i], out CSteamID sid))
                    continue;
                try { Steam.CloseSession(sid); }
                catch { /* tear */ }
            }
        }

    }
}
