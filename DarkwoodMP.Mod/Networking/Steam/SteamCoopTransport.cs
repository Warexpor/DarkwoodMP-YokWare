using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using DWMPHorde.Config;
using DWMPHorde.Logging;
using LiteNetLib;
using Steamworks;
using UnityEngine;

namespace DWMPHorde.Networking.Steam
{
    /// <summary>
    /// Steam lobbies + SteamNetworkingSockets P2P for YokWare co-op.
    /// Same application framing as LAN (byte type + body); no LiteNetLib on this path.
    /// Requires the game's existing SteamManager (SteamAPI already Init).
    /// </summary>
    public sealed class SteamCoopTransport
    {
        public const string LobbyKeyMod = "yokware";
        public const string LobbyKeyProto = "proto";
        public const string LobbyKeyConn = "conn";
        public const string LobbyKeyName = "name";
        /// <summary>Darkwood Steam AppID.</summary>
        public const uint DarkwoodAppId = 274520;
        /// <summary>SNS virtual port (matches friend Yokyy SteamTransport).</summary>
        private const int VirtualPort = 17;
        private const int MaxMessagesPerPoll = 128;
        private const int CloseReasonGeneric = 1000;
        private const int CloseReasonRejected = 1001;
        private const int CloseReasonShutdown = 1003;
        /// <summary>k_nSteamNetworkingSend_Reliable</summary>
        private const int SendReliable = 8;
        /// <summary>k_nSteamNetworkingSend_Unreliable | NoNagle | NoDelay ≈ friend unreliable=0; use NoNagle(1)+NoDelay(4)=5</summary>
        private const int SendUnreliableNoDelay = 0 | 1 | 4;

        private readonly LanNetworkManager _owner;
        private readonly IntPtr[] _recvBuffer = new IntPtr[MaxMessagesPerPoll];
        private readonly Dictionary<ulong, HSteamNetConnection> _connBySteamId =
            new Dictionary<ulong, HSteamNetConnection>();
        private readonly Dictionary<uint, ulong> _steamIdByConn = new Dictionary<uint, ulong>();
        private readonly Dictionary<uint, Queue<byte[]>> _reliableOutbox =
            new Dictionary<uint, Queue<byte[]>>();

        private Callback<GameLobbyJoinRequested_t> _cbLobbyJoinRequested;
        private Callback<LobbyChatUpdate_t> _cbLobbyChatUpdate;
        private Callback<SteamNetConnectionStatusChangedCallback_t> _cbConnStatus;
        private CallResult<LobbyCreated_t> _crLobbyCreated;
        private CallResult<LobbyEnter_t> _crLobbyEnter;

        private CSteamID _lobbyId = CSteamID.Nil;
        private CSteamID _hostSteamId = CSteamID.Nil;
        private HSteamListenSocket _listenSocket = HSteamListenSocket.Invalid;
        private HSteamNetPollGroup _pollGroup = HSteamNetPollGroup.Invalid;
        private HSteamNetConnection _serverConn = HSteamNetConnection.Invalid;
        private bool _active;
        private bool _hosting;
        private bool _clientTransportReady;
        private DateTime _clientConnectStartedUtc = DateTime.MinValue;
        private ulong _pendingLaunchLobby;

        private static readonly TimeSpan ClientConnectTimeout = TimeSpan.FromSeconds(20);

        public bool IsActive => _active;
        public bool IsHosting => _hosting;
        public CSteamID LobbyId => _lobbyId;
        public CSteamID HostSteamId => _hostSteamId;
        public string LobbyIdString => _lobbyId.IsValid() ? _lobbyId.m_SteamID.ToString() : "";
        public ulong PendingLaunchLobby => _pendingLaunchLobby;

        public SteamCoopTransport(LanNetworkManager owner)
        {
            _owner = owner;
        }

        public static bool IsSteamReady(out string failReason)
        {
            failReason = null;
            try
            {
                if (!SteamManager.Initialized)
                {
                    failReason = "SteamManager not initialized (launch via Steam / check steam_api).";
                    return false;
                }
                if (!SteamUser.BLoggedOn())
                {
                    failReason = "Steam user not logged on.";
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                failReason = "Steam API unavailable: " + ex.Message;
                return false;
            }
        }

        public static CSteamID LocalSteamId()
        {
            try { return SteamUser.GetSteamID(); }
            catch { return CSteamID.Nil; }
        }

        public ulong ConsumePendingLaunchLobby()
        {
            ulong id = _pendingLaunchLobby;
            _pendingLaunchLobby = 0;
            return id;
        }

        public void EnsureCallbacks()
        {
            if (_cbLobbyJoinRequested != null)
                return;
            _cbLobbyJoinRequested = Callback<GameLobbyJoinRequested_t>.Create(OnGameLobbyJoinRequested);
            _cbLobbyChatUpdate = Callback<LobbyChatUpdate_t>.Create(OnLobbyChatUpdate);
            _cbConnStatus = Callback<SteamNetConnectionStatusChangedCallback_t>.Create(OnConnectionStatusChanged);
            _crLobbyCreated = CallResult<LobbyCreated_t>.Create(OnLobbyCreated);
            _crLobbyEnter = CallResult<LobbyEnter_t>.Create(OnLobbyEnter);
            ParseLaunchArgs();
        }

        private void ParseLaunchArgs()
        {
            try
            {
                string[] args = Environment.GetCommandLineArgs();
                for (int i = 0; i < args.Length - 1; i++)
                {
                    if (string.Equals(args[i], "+connect_lobby", StringComparison.OrdinalIgnoreCase)
                        && ulong.TryParse(args[i + 1], out ulong lobby)
                        && lobby != 0)
                    {
                        _pendingLaunchLobby = lobby;
                        ModLog.Event(LogCat.Network,
                            "Steam launched via invite — pending lobby " + lobby);
                    }
                }
            }
            catch { /* ignore */ }
        }

        public bool StartHost()
        {
            if (!IsSteamReady(out string fail))
            {
                ModLog.Error(LogCat.Network, "Steam host failed: " + fail);
                return false;
            }

            EnsureCallbacks();
            ShutdownInternal(leaveLobby: true);

            SteamRelay.WarmRelay();
            if (!CreateListenSocket())
                return false;

            int max = Mathf.Clamp(ModConfig.MaxPlayers?.Value ?? 8, 2, 16);
            ELobbyType lobbyType = ResolveLobbyType();

            _hosting = true;
            _active = true;
            _hostSteamId = LocalSteamId();

            SteamAPICall_t call = SteamMatchmaking.CreateLobby(lobbyType, max);
            _crLobbyCreated.Set(call);
            ModLog.Event(LogCat.Network,
                "Steam host: CreateLobby type=" + lobbyType + " max=" + max + " SNS listen");
            return true;
        }

        private static ELobbyType ResolveLobbyType()
        {
            string raw = (ModConfig.SteamLobbyType?.Value ?? "friends").Trim().ToLowerInvariant();
            switch (raw)
            {
                case "public":
                    return ELobbyType.k_ELobbyTypePublic;
                case "private":
                    return ELobbyType.k_ELobbyTypePrivate;
                default:
                    return ELobbyType.k_ELobbyTypeFriendsOnly;
            }
        }

        private bool CreateListenSocket()
        {
            _listenSocket = SteamNetworkingSockets.CreateListenSocketP2P(VirtualPort, 0, null);
            if (_listenSocket == HSteamListenSocket.Invalid)
            {
                ModLog.Error(LogCat.Network, "CreateListenSocketP2P failed — is Steam running?");
                ShutdownInternal(leaveLobby: false);
                return false;
            }
            _pollGroup = SteamNetworkingSockets.CreatePollGroup();
            if (_pollGroup == HSteamNetPollGroup.Invalid)
            {
                ModLog.Error(LogCat.Network, "CreatePollGroup failed");
                ShutdownInternal(leaveLobby: false);
                return false;
            }
            return true;
        }

        public bool JoinLobby(CSteamID lobbyId)
        {
            if (!IsSteamReady(out string fail))
            {
                ModLog.Error(LogCat.Network, "Steam join failed: " + fail);
                return false;
            }
            if (!lobbyId.IsValid())
            {
                ModLog.Error(LogCat.Network, "Steam join failed: invalid lobby id.");
                return false;
            }

            EnsureCallbacks();
            ShutdownInternal(leaveLobby: true);
            SteamRelay.WarmRelay();

            _hosting = false;
            _active = true;
            _clientTransportReady = false;
            _clientConnectStartedUtc = DateTime.MinValue;
            _lobbyId = lobbyId;

            SteamAPICall_t call = SteamMatchmaking.JoinLobby(lobbyId);
            _crLobbyEnter.Set(call);
            ModLog.Event(LogCat.Network, "Steam join: JoinLobby " + lobbyId.m_SteamID);
            return true;
        }

        public bool TryParseLobbyId(string raw, out CSteamID lobbyId)
        {
            lobbyId = CSteamID.Nil;
            if (string.IsNullOrWhiteSpace(raw))
                return false;
            raw = raw.Trim();
            if (raw.StartsWith("steam://", StringComparison.OrdinalIgnoreCase))
            {
                string[] parts = raw.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < parts.Length; i++)
                {
                    if (string.Equals(parts[i], "joinlobby", StringComparison.OrdinalIgnoreCase)
                        && i + 2 < parts.Length
                        && ulong.TryParse(parts[i + 2], out ulong lid))
                    {
                        lobbyId = new CSteamID(lid);
                        return lobbyId.IsValid();
                    }
                }
            }
            if (ulong.TryParse(raw, out ulong id))
            {
                lobbyId = new CSteamID(id);
                return lobbyId.IsValid();
            }
            return false;
        }

        public void Shutdown()
        {
            ShutdownInternal(leaveLobby: true);
        }

        private void ShutdownInternal(bool leaveLobby)
        {
            if (_active)
            {
                try { _owner.CloseAllSteamSessions(); }
                catch { /* tear */ }
            }

            CloseAllConnections();

            if (_listenSocket != HSteamListenSocket.Invalid)
            {
                try { SteamNetworkingSockets.CloseListenSocket(_listenSocket); }
                catch { /* tear */ }
                _listenSocket = HSteamListenSocket.Invalid;
            }
            if (_pollGroup != HSteamNetPollGroup.Invalid)
            {
                try { SteamNetworkingSockets.DestroyPollGroup(_pollGroup); }
                catch { /* tear */ }
                _pollGroup = HSteamNetPollGroup.Invalid;
            }

            if (leaveLobby && _lobbyId.IsValid())
            {
                try { SteamMatchmaking.LeaveLobby(_lobbyId); }
                catch { /* tear */ }
            }

            _lobbyId = CSteamID.Nil;
            _hostSteamId = CSteamID.Nil;
            _serverConn = HSteamNetConnection.Invalid;
            _active = false;
            _hosting = false;
            _clientTransportReady = false;
            _clientConnectStartedUtc = DateTime.MinValue;
            _reliableOutbox.Clear();
        }

        private void CloseAllConnections()
        {
            foreach (var kvp in _connBySteamId)
            {
                try
                {
                    SteamNetworkingSockets.CloseConnection(kvp.Value, CloseReasonShutdown, "shutdown", true);
                }
                catch { /* tear */ }
            }
            _connBySteamId.Clear();
            _steamIdByConn.Clear();

            if (_serverConn != HSteamNetConnection.Invalid)
            {
                try
                {
                    SteamNetworkingSockets.CloseConnection(_serverConn, CloseReasonShutdown, "client disconnect", true);
                }
                catch { /* tear */ }
                _serverConn = HSteamNetConnection.Invalid;
            }
        }

        public void OpenInviteOverlay()
        {
            if (!_lobbyId.IsValid())
                return;
            try
            {
                SteamFriends.ActivateGameOverlayInviteDialog(_lobbyId);
            }
            catch (Exception ex)
            {
                ModLog.Warn(LogCat.Network, "Invite overlay failed: " + ex.Message);
            }
        }

        public void Poll()
        {
            if (!_active)
                return;

            if (!_hosting && !_clientTransportReady
                && _serverConn != HSteamNetConnection.Invalid
                && _clientConnectStartedUtc != DateTime.MinValue
                && DateTime.UtcNow - _clientConnectStartedUtc > ClientConnectTimeout)
            {
                ModLog.Warn(LogCat.Network,
                    "Steam SNS connect timeout after " + ClientConnectTimeout.TotalSeconds + "s");
                _owner.OnSteamLobbyFailed("SNS timeout");
                return;
            }

            FlushReliableOutbox();

            if (_hosting)
            {
                if (_pollGroup != HSteamNetPollGroup.Invalid)
                    DrainMessages(() => SteamNetworkingSockets.ReceiveMessagesOnPollGroup(
                        _pollGroup, _recvBuffer, MaxMessagesPerPoll));
            }
            else if (_serverConn != HSteamNetConnection.Invalid)
            {
                DrainMessages(() => SteamNetworkingSockets.ReceiveMessagesOnConnection(
                    _serverConn, _recvBuffer, MaxMessagesPerPoll));
            }
        }

        private void DrainMessages(Func<int> receive)
        {
            int n;
            do
            {
                try { n = receive(); }
                catch (Exception ex)
                {
                    ModLog.Warn(LogCat.Network, "Steam SNS receive: " + ex.Message);
                    break;
                }
                if (n <= 0)
                    break;
                for (int i = 0; i < n; i++)
                    HandleMessage(_recvBuffer[i]);
            } while (n >= MaxMessagesPerPoll);
        }

        private void HandleMessage(IntPtr ptr)
        {
            try
            {
                SteamNetworkingMessage_t msg = SteamNetworkingMessage_t.FromIntPtr(ptr);
                if (msg.m_cbSize < 1)
                    return;

                byte[] payload = new byte[msg.m_cbSize];
                Marshal.Copy(msg.m_pData, payload, 0, msg.m_cbSize);

                CSteamID remote = CSteamID.Nil;
                if (_hosting)
                {
                    if (_steamIdByConn.TryGetValue(msg.m_conn.m_HSteamNetConnection, out ulong sid))
                        remote = new CSteamID(sid);
                    else
                    {
                        SteamNetConnectionInfo_t info;
                        if (SteamNetworkingSockets.GetConnectionInfo(msg.m_conn, out info))
                            remote = info.m_identityRemote.GetSteamID();
                    }
                }
                else
                {
                    remote = _hostSteamId;
                }

                if (!remote.IsValid())
                    return;
                _owner.OnSteamPacket(remote, payload);
            }
            catch (Exception ex)
            {
                ModLog.Warn(LogCat.Network, "Steam SNS message: " + ex.Message);
            }
            finally
            {
                SteamNetworkingMessage_t.Release(ptr);
            }
        }

        public bool Send(CSteamID remote, byte[] data, DeliveryMethod method)
        {
            if (!_active || !remote.IsValid() || data == null || data.Length == 0)
                return false;

            HSteamNetConnection conn = HSteamNetConnection.Invalid;
            if (_hosting)
            {
                if (!_connBySteamId.TryGetValue(remote.m_SteamID, out conn))
                    return false;
            }
            else
            {
                if (!_clientTransportReady || _serverConn == HSteamNetConnection.Invalid)
                    return false;
                conn = _serverConn;
            }

            bool reliable = method != DeliveryMethod.Unreliable;
            return SendRaw(conn, data, data.Length, reliable);
        }

        /// <summary>
        /// Classic AcceptP2PSession no longer applies; SNS accept happens in status callback.
        /// Kept so call sites stay stable.
        /// </summary>
        public void AcceptSession(CSteamID remote)
        {
            // no-op — connection accepted on Connecting → AcceptConnection
        }

        public void CloseSession(CSteamID remote)
        {
            if (!remote.IsValid())
                return;
            if (!_connBySteamId.TryGetValue(remote.m_SteamID, out HSteamNetConnection conn))
            {
                if (!_hosting && remote == _hostSteamId && _serverConn != HSteamNetConnection.Invalid)
                    conn = _serverConn;
                else
                    return;
            }

            try
            {
                SteamNetworkingSockets.CloseConnection(conn, CloseReasonGeneric, "peer dropped", false);
            }
            catch { /* tear */ }

            UntrackConn(conn, remote.m_SteamID);
            if (!_hosting && conn == _serverConn)
                _serverConn = HSteamNetConnection.Invalid;
        }

        private void UntrackConn(HSteamNetConnection conn, ulong steamId)
        {
            _connBySteamId.Remove(steamId);
            _steamIdByConn.Remove(conn.m_HSteamNetConnection);
            _reliableOutbox.Remove(conn.m_HSteamNetConnection);
        }

        private void OnConnectionStatusChanged(SteamNetConnectionStatusChangedCallback_t cb)
        {
            if (!_active)
                return;
            try
            {
                if (cb.m_info.m_hListenSocket != HSteamListenSocket.Invalid)
                    OnHostSideStatus(cb);
                else if (cb.m_hConn == _serverConn)
                    OnClientSideStatus(cb);
            }
            catch (Exception ex)
            {
                ModLog.Error(LogCat.Network, "Steam SNS status: " + ex.Message);
            }
        }

        private void OnHostSideStatus(SteamNetConnectionStatusChangedCallback_t cb)
        {
            CSteamID steamId = cb.m_info.m_identityRemote.GetSteamID();
            ESteamNetworkingConnectionState state = cb.m_info.m_eState;

            switch (state)
            {
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting:
                    if (!LobbyAllowsSteamId(steamId))
                    {
                        ModLog.Event(LogCat.Network,
                            "Steam SNS refuse " + steamId.m_SteamID + " (not in lobby)");
                        SteamNetworkingSockets.CloseConnection(
                            cb.m_hConn, CloseReasonRejected, "not in lobby", false);
                    }
                    else if (SteamNetworkingSockets.AcceptConnection(cb.m_hConn)
                             != EResult.k_EResultOK)
                    {
                        SteamNetworkingSockets.CloseConnection(
                            cb.m_hConn, CloseReasonGeneric, "accept failed", false);
                    }
                    break;

                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected:
                    SteamNetworkingSockets.SetConnectionPollGroup(cb.m_hConn, _pollGroup);
                    TrackConn(cb.m_hConn, steamId.m_SteamID);
                    ModLog.Event(LogCat.Network,
                        "Steam SNS peer connected " + steamId.m_SteamID + " — awaiting packets");
                    break;

                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer:
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally:
                    ModLog.Warn(LogCat.Network,
                        "Steam SNS peer lost " + steamId.m_SteamID + ": " + cb.m_info.m_szEndDebug);
                    UntrackConn(cb.m_hConn, steamId.m_SteamID);
                    _owner.OnSteamSessionFailed(steamId);
                    break;
            }
        }

        private void OnClientSideStatus(SteamNetConnectionStatusChangedCallback_t cb)
        {
            ESteamNetworkingConnectionState state = cb.m_info.m_eState;
            if (state == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected)
            {
                _clientTransportReady = true;
                _clientConnectStartedUtc = DateTime.MinValue;
                TrackConn(_serverConn, _hostSteamId.m_SteamID);
                ModLog.Event(LogCat.Network, "Steam SNS connected to host — starting handshake");
                _owner.OnSteamLobbyReady(_lobbyId, isHost: false);
                return;
            }

            if (state == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer
                || state == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally)
            {
                string detail = cb.m_info.m_szEndDebug ?? state.ToString();
                ModLog.Warn(LogCat.Network, "Steam SNS connection lost: " + detail);
                // Pre-handshake: no peer map yet — tear via lobby-failed (StopNetwork).
                if (!_clientTransportReady)
                    _owner.OnSteamLobbyFailed("SNS connection lost: " + detail);
                else
                    _owner.OnSteamSessionFailed(_hostSteamId);
            }
        }

        private bool LobbyAllowsSteamId(CSteamID steamId)
        {
            if (!_lobbyId.IsValid() || !steamId.IsValid())
                return false;
            // Classic P2P accepted any host-side session. Member-list lag after JoinLobby
            // used to refuse valid clients (n>=1 host-only → not-in-list → CloseConnection).
            if (_hosting)
                return true;
            int n = SteamMatchmaking.GetNumLobbyMembers(_lobbyId);
            for (int i = 0; i < n; i++)
            {
                if (SteamMatchmaking.GetLobbyMemberByIndex(_lobbyId, i) == steamId)
                    return true;
            }
            return n == 0;
        }

        private void TrackConn(HSteamNetConnection conn, ulong steamId)
        {
            if (conn == HSteamNetConnection.Invalid || steamId == 0)
                return;
            _connBySteamId[steamId] = conn;
            _steamIdByConn[conn.m_HSteamNetConnection] = steamId;
        }

        private void OnGameLobbyJoinRequested(GameLobbyJoinRequested_t req)
        {
            if (_owner.Role != NetworkRole.Offline)
            {
                ModLog.Event(LogCat.Session, "Steam invite ignored — already in a session.");
                return;
            }
            ModLog.Event(LogCat.Network, "Steam invite → join lobby " + req.m_steamIDLobby.m_SteamID);
            _owner.ConnectSteamLobby(req.m_steamIDLobby);
        }

        private void OnLobbyChatUpdate(LobbyChatUpdate_t upd)
        {
            if (!_active || !_lobbyId.IsValid() || upd.m_ulSteamIDLobby != _lobbyId.m_SteamID)
                return;
            if (!_hosting && _hostSteamId.IsValid())
            {
                CSteamID changed = new CSteamID(upd.m_ulSteamIDUserChanged);
                bool left = (upd.m_rgfChatMemberStateChange
                    & (uint)(EChatMemberStateChange.k_EChatMemberStateChangeLeft
                        | EChatMemberStateChange.k_EChatMemberStateChangeDisconnected
                        | EChatMemberStateChange.k_EChatMemberStateChangeKicked
                        | EChatMemberStateChange.k_EChatMemberStateChangeBanned)) != 0;
                if (left && changed == _hostSteamId)
                {
                    ModLog.Event(LogCat.Network, "Steam host left lobby — disconnecting.");
                    _owner.OnSteamHostLeftLobby();
                }
            }
        }

        private void OnLobbyCreated(LobbyCreated_t result, bool ioFailure)
        {
            if (!_active || !_hosting)
                return;
            if (ioFailure || result.m_eResult != EResult.k_EResultOK)
            {
                ModLog.Error(LogCat.Network, "CreateLobby failed: " + result.m_eResult);
                _owner.OnSteamLobbyFailed("CreateLobby " + result.m_eResult);
                return;
            }

            _lobbyId = new CSteamID(result.m_ulSteamIDLobby);
            ApplyHostLobbyData();
            _owner.OnSteamLobbyReady(_lobbyId, isHost: true);
            ModLog.Event(LogCat.Network,
                "Steam lobby ready id=" + _lobbyId.m_SteamID
                + " (SNS listen; invite or paste lobby id)");
        }

        private void OnLobbyEnter(LobbyEnter_t result, bool ioFailure)
        {
            if (!_active)
                return;

            if (_hosting)
            {
                if (_lobbyId.IsValid())
                    return;
                if (!ioFailure && result.m_EChatRoomEnterResponse
                    == (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess)
                {
                    _lobbyId = new CSteamID(result.m_ulSteamIDLobby);
                    ApplyHostLobbyData();
                    _owner.OnSteamLobbyReady(_lobbyId, isHost: true);
                }
                return;
            }

            if (ioFailure || result.m_EChatRoomEnterResponse
                != (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess)
            {
                ModLog.Error(LogCat.Network, "JoinLobby failed response=" + result.m_EChatRoomEnterResponse);
                _owner.OnSteamLobbyFailed("JoinLobby " + result.m_EChatRoomEnterResponse);
                return;
            }

            _lobbyId = new CSteamID(result.m_ulSteamIDLobby);

            string expected = ModConfig.GetConnectionKey() ?? "";
            string remoteKey = SteamMatchmaking.GetLobbyData(_lobbyId, LobbyKeyConn) ?? "";
            if (!string.IsNullOrEmpty(remoteKey) && !string.Equals(remoteKey, expected, StringComparison.Ordinal))
            {
                ModLog.Error(LogCat.Network, "Steam lobby password mismatch (HostPassword must match).");
                _owner.OnSteamLobbyFailed("password mismatch");
                return;
            }

            string modTag = SteamMatchmaking.GetLobbyData(_lobbyId, LobbyKeyMod) ?? "";
            if (!string.Equals(modTag, "1", StringComparison.Ordinal))
                ModLog.Warn(LogCat.Network, "Lobby missing yokware tag — joining anyway.");

            string remoteProto = SteamMatchmaking.GetLobbyData(_lobbyId, LobbyKeyProto) ?? "";
            string localProto = PluginInfo.ProtocolVersion.ToString();
            if (!string.IsNullOrEmpty(remoteProto)
                && !string.Equals(remoteProto, localProto, StringComparison.Ordinal))
            {
                ModLog.Error(LogCat.Network,
                    "Steam lobby protocol mismatch remote=" + remoteProto + " local=" + localProto);
                _owner.OnSteamLobbyFailed("protocol mismatch " + remoteProto);
                return;
            }

            _hostSteamId = SteamMatchmaking.GetLobbyOwner(_lobbyId);
            if (!_hostSteamId.IsValid() || _hostSteamId == LocalSteamId())
            {
                ModLog.Error(LogCat.Network, "Steam join: no remote host in lobby.");
                _owner.OnSteamLobbyFailed("no host");
                return;
            }

            SteamNetworkingIdentity identity = default;
            identity.SetSteamID(_hostSteamId);
            _serverConn = SteamNetworkingSockets.ConnectP2P(ref identity, VirtualPort, 0, null);
            if (_serverConn == HSteamNetConnection.Invalid)
            {
                ModLog.Error(LogCat.Network, "ConnectP2P failed");
                _owner.OnSteamLobbyFailed("ConnectP2P failed");
                return;
            }

            _clientTransportReady = false;
            _clientConnectStartedUtc = DateTime.UtcNow;
            ModLog.Event(LogCat.Network,
                "Steam lobby entered — ConnectP2P host=" + _hostSteamId.m_SteamID
                + " (handshake after SNS Connected, timeout "
                + ClientConnectTimeout.TotalSeconds + "s)");
            // OnSteamLobbyReady(false) deferred until SNS Connected.
        }

        private void ApplyHostLobbyData()
        {
            if (!_lobbyId.IsValid())
                return;
            SteamMatchmaking.SetLobbyData(_lobbyId, LobbyKeyMod, "1");
            SteamMatchmaking.SetLobbyData(_lobbyId, LobbyKeyProto, PluginInfo.ProtocolVersion.ToString());
            SteamMatchmaking.SetLobbyData(_lobbyId, LobbyKeyConn, ModConfig.GetConnectionKey() ?? "");
            string name = ModConfig.PlayerName?.Value;
            if (string.IsNullOrEmpty(name))
            {
                try { name = SteamFriends.GetPersonaName(); }
                catch { name = "Host"; }
            }
            SteamMatchmaking.SetLobbyData(_lobbyId, LobbyKeyName, name ?? "Host");
            SteamMatchmaking.SetLobbyJoinable(_lobbyId, true);
        }

        private bool SendRaw(HSteamNetConnection conn, byte[] data, int length, bool reliable)
        {
            if (conn == HSteamNetConnection.Invalid)
                return false;

            uint key = conn.m_HSteamNetConnection;
            if (reliable && _reliableOutbox.TryGetValue(key, out Queue<byte[]> q) && q.Count > 0)
            {
                q.Enqueue(Slice(data, length));
                return true;
            }

            EResult result = SendNow(conn, data, length, reliable);
            if (result == EResult.k_EResultLimitExceeded && reliable)
            {
                if (!_reliableOutbox.TryGetValue(key, out q))
                    _reliableOutbox[key] = q = new Queue<byte[]>();
                q.Enqueue(Slice(data, length));
                return true;
            }
            if (result != EResult.k_EResultOK && reliable)
            {
                ModLog.Trace(LogCat.Network,
                    () => "Steam SNS reliable send failed: " + result + " len=" + length);
                return false;
            }
            return result == EResult.k_EResultOK;
        }

        private static EResult SendNow(HSteamNetConnection conn, byte[] data, int length, bool reliable)
        {
            int flags = reliable ? SendReliable : SendUnreliableNoDelay;
            GCHandle pin = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                long msgNum = 0;
                return SteamNetworkingSockets.SendMessageToConnection(
                    conn, pin.AddrOfPinnedObject(), (uint)length, flags, out msgNum);
            }
            finally
            {
                pin.Free();
            }
        }

        private static byte[] Slice(byte[] data, int length)
        {
            if (data.Length == length)
                return data;
            byte[] copy = new byte[length];
            Buffer.BlockCopy(data, 0, copy, 0, length);
            return copy;
        }

        private void FlushReliableOutbox()
        {
            if (_reliableOutbox.Count == 0)
                return;

            List<uint> remove = null;
            foreach (var item in _reliableOutbox)
            {
                var conn = new HSteamNetConnection { m_HSteamNetConnection = item.Key };
                Queue<byte[]> queue = item.Value;
                bool known = item.Key == _serverConn.m_HSteamNetConnection
                    || _steamIdByConn.ContainsKey(item.Key);
                if (!known || queue.Count == 0)
                {
                    (remove ?? (remove = new List<uint>())).Add(item.Key);
                    continue;
                }

                while (queue.Count > 0)
                {
                    byte[] payload = queue.Peek();
                    EResult result = SendNow(conn, payload, payload.Length, reliable: true);
                    if (result == EResult.k_EResultLimitExceeded)
                        break;
                    queue.Dequeue();
                    if (result != EResult.k_EResultOK)
                    {
                        (remove ?? (remove = new List<uint>())).Add(item.Key);
                        break;
                    }
                }
                if (queue.Count == 0)
                    (remove ?? (remove = new List<uint>())).Add(item.Key);
            }

            if (remove == null)
                return;
            foreach (uint key in remove)
                _reliableOutbox.Remove(key);
        }

        public static void CopyToClipboard(string text)
        {
            if (string.IsNullOrEmpty(text))
                return;
            try { GUIUtility.systemCopyBuffer = text; }
            catch { /* no clipboard */ }
        }
    }
}
