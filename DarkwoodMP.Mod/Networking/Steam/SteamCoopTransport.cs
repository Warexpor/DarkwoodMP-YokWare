using System;
using DWMPHorde.Config;
using DWMPHorde.Logging;
using LiteNetLib;
using Steamworks;
using UnityEngine;

namespace DWMPHorde.Networking.Steam
{
    /// <summary>
    /// Steam lobbies + classic SteamNetworking P2P for YokWare co-op.
    /// Carries the same application packets as LAN (byte type + body); no LiteNetLib on this path.
    /// Requires the game's existing SteamManager (SteamAPI already Init).
    /// </summary>
    public sealed class SteamCoopTransport
    {
        public const string LobbyKeyMod = "yokware";
        public const string LobbyKeyProto = "proto";
        public const string LobbyKeyConn = "conn";
        public const string LobbyKeyName = "name";
        public const int P2PChannel = 0;
        /// <summary>Darkwood Steam AppID.</summary>
        public const uint DarkwoodAppId = 274520;

        private readonly LanNetworkManager _owner;
        private readonly byte[] _recvBuf = new byte[1024 * 512];

        private Callback<P2PSessionRequest_t> _cbSessionRequest;
        private Callback<P2PSessionConnectFail_t> _cbSessionFail;
        private Callback<GameLobbyJoinRequested_t> _cbLobbyJoinRequested;
        private Callback<LobbyChatUpdate_t> _cbLobbyChatUpdate;
        private CallResult<LobbyCreated_t> _crLobbyCreated;
        private CallResult<LobbyEnter_t> _crLobbyEnter;

        private CSteamID _lobbyId = CSteamID.Nil;
        private CSteamID _hostSteamId = CSteamID.Nil;
        private bool _active;
        private bool _hosting;

        public bool IsActive => _active;
        public bool IsHosting => _hosting;
        public CSteamID LobbyId => _lobbyId;
        public CSteamID HostSteamId => _hostSteamId;
        public string LobbyIdString => _lobbyId.IsValid() ? _lobbyId.m_SteamID.ToString() : "";

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

        public void EnsureCallbacks()
        {
            if (_cbSessionRequest != null)
                return;
            _cbSessionRequest = Callback<P2PSessionRequest_t>.Create(OnP2PSessionRequest);
            _cbSessionFail = Callback<P2PSessionConnectFail_t>.Create(OnP2PSessionFail);
            _cbLobbyJoinRequested = Callback<GameLobbyJoinRequested_t>.Create(OnGameLobbyJoinRequested);
            _cbLobbyChatUpdate = Callback<LobbyChatUpdate_t>.Create(OnLobbyChatUpdate);
            _crLobbyCreated = CallResult<LobbyCreated_t>.Create(OnLobbyCreated);
            _crLobbyEnter = CallResult<LobbyEnter_t>.Create(OnLobbyEnter);
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

            int max = Mathf.Clamp(ModConfig.MaxPlayers?.Value ?? 8, 2, 16);
            SteamNetworking.AllowP2PPacketRelay(true);

            _hosting = true;
            _active = true;
            _hostSteamId = LocalSteamId();

            SteamAPICall_t call = SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypeFriendsOnly, max);
            _crLobbyCreated.Set(call);
            ModLog.Event(LogCat.Network, "Steam host: CreateLobby max=" + max);
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

            SteamNetworking.AllowP2PPacketRelay(true);
            _hosting = false;
            _active = true;
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
            // Accept plain ulong, or "steam://joinlobby/appid/lobbyid/..."
            if (raw.StartsWith("steam://", StringComparison.OrdinalIgnoreCase))
            {
                string[] parts = raw.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                // steam: / joinlobby / appid / lobbyid / ...
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
                try
                {
                    // Close P2P sessions we know about (owner maps).
                    _owner.CloseAllSteamSessions();
                }
                catch { /* tear */ }
            }

            if (leaveLobby && _lobbyId.IsValid())
            {
                try { SteamMatchmaking.LeaveLobby(_lobbyId); }
                catch { /* tear */ }
            }

            _lobbyId = CSteamID.Nil;
            _hostSteamId = CSteamID.Nil;
            _active = false;
            _hosting = false;
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

            // Drain all available P2P packets this frame.
            uint size = 0;
            int safety = 256;
            while (safety-- > 0 && SteamNetworking.IsP2PPacketAvailable(out size, P2PChannel))
            {
                if (size == 0 || size > _recvBuf.Length)
                {
                    // Drain oversized / empty
                    CSteamID dump = CSteamID.Nil;
                    uint got = 0;
                    byte[] trash = size > 0 && size < 4 * 1024 * 1024 ? new byte[size] : new byte[1];
                    SteamNetworking.ReadP2PPacket(trash, (uint)trash.Length, out got, out dump, P2PChannel);
                    continue;
                }

                CSteamID remote = CSteamID.Nil;
                uint msgSize = 0;
                if (!SteamNetworking.ReadP2PPacket(_recvBuf, size, out msgSize, out remote, P2PChannel))
                    break;
                if (msgSize == 0 || !remote.IsValid())
                    continue;

                byte[] payload = new byte[msgSize];
                Buffer.BlockCopy(_recvBuf, 0, payload, 0, (int)msgSize);
                _owner.OnSteamPacket(remote, payload);
            }
        }

        public bool Send(CSteamID remote, byte[] data, DeliveryMethod method)
        {
            if (!_active || !remote.IsValid() || data == null || data.Length == 0)
                return false;

            EP2PSend sendType = method == DeliveryMethod.Unreliable
                    ? EP2PSend.k_EP2PSendUnreliableNoDelay
                    : EP2PSend.k_EP2PSendReliable;

            bool ok = SteamNetworking.SendP2PPacket(remote, data, (uint)data.Length, sendType, P2PChannel);
            if (!ok)
                ModLog.Trace(LogCat.Network, () => "Steam SendP2PPacket failed → " + remote.m_SteamID);
            return ok;
        }

        public void AcceptSession(CSteamID remote)
        {
            if (!remote.IsValid())
                return;
            SteamNetworking.AcceptP2PSessionWithUser(remote);
        }

        public void CloseSession(CSteamID remote)
        {
            if (!remote.IsValid())
                return;
            try { SteamNetworking.CloseP2PSessionWithUser(remote); }
            catch { /* tear */ }
        }

        private void OnP2PSessionRequest(P2PSessionRequest_t req)
        {
            if (!_active)
                return;
            CSteamID remote = req.m_steamIDRemote;
            if (!remote.IsValid())
                return;

            // Host accepts anyone who can talk P2P (lobby filter is soft). Client only accepts host.
            if (_hosting || (_hostSteamId.IsValid() && remote == _hostSteamId))
            {
                SteamNetworking.AcceptP2PSessionWithUser(remote);
                ModLog.Event(LogCat.Network, "Steam P2P accept " + remote.m_SteamID);
            }
            else
            {
                ModLog.Warn(LogCat.Network, "Steam P2P reject unexpected " + remote.m_SteamID);
            }
        }

        private void OnP2PSessionFail(P2PSessionConnectFail_t fail)
        {
            if (!_active)
                return;
            ModLog.Warn(LogCat.Network,
                "Steam P2P fail remote=" + fail.m_steamIDRemote.m_SteamID
                + " error=" + fail.m_eP2PSessionError);
            _owner.OnSteamSessionFailed(fail.m_steamIDRemote);
        }

        private void OnGameLobbyJoinRequested(GameLobbyJoinRequested_t req)
        {
            // Overlay invite while offline → join that lobby.
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
            // Member leave: if we are client and host left, tear.
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
                + " (friends can join via invite or paste lobby id)");
        }

        private void OnLobbyEnter(LobbyEnter_t result, bool ioFailure)
        {
            if (!_active)
                return;

            // CreateLobby also fires LobbyEnter for the host on some API versions — host path uses LobbyCreated.
            if (_hosting)
            {
                if (_lobbyId.IsValid())
                    return;
                if (!ioFailure && result.m_EChatRoomEnterResponse == (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess)
                {
                    _lobbyId = new CSteamID(result.m_ulSteamIDLobby);
                    ApplyHostLobbyData();
                    _owner.OnSteamLobbyReady(_lobbyId, isHost: true);
                }
                return;
            }

            if (ioFailure || result.m_EChatRoomEnterResponse != (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess)
            {
                ModLog.Error(LogCat.Network, "JoinLobby failed response=" + result.m_EChatRoomEnterResponse);
                _owner.OnSteamLobbyFailed("JoinLobby " + result.m_EChatRoomEnterResponse);
                return;
            }

            _lobbyId = new CSteamID(result.m_ulSteamIDLobby);

            // Soft password check via lobby data (always set by YokWare hosts).
            string expected = ModConfig.GetConnectionKey() ?? "";
            string remoteKey = SteamMatchmaking.GetLobbyData(_lobbyId, LobbyKeyConn) ?? "";
            // Empty remote key = non-YokWare / ancient lobby — still allow join with warning.
            if (!string.IsNullOrEmpty(remoteKey) && !string.Equals(remoteKey, expected, StringComparison.Ordinal))
            {
                ModLog.Error(LogCat.Network, "Steam lobby password mismatch (HostPassword must match).");
                _owner.OnSteamLobbyFailed("password mismatch");
                return;
            }

            string modTag = SteamMatchmaking.GetLobbyData(_lobbyId, LobbyKeyMod) ?? "";
            if (!string.Equals(modTag, "1", StringComparison.Ordinal))
            {
                ModLog.Warn(LogCat.Network, "Lobby missing yokware tag — joining anyway.");
            }

            _hostSteamId = SteamMatchmaking.GetLobbyOwner(_lobbyId);
            if (!_hostSteamId.IsValid() || _hostSteamId == LocalSteamId())
            {
                // Edge: we became owner (empty lobby) — not a valid join.
                ModLog.Error(LogCat.Network, "Steam join: no remote host in lobby.");
                _owner.OnSteamLobbyFailed("no host");
                return;
            }

            SteamNetworking.AcceptP2PSessionWithUser(_hostSteamId);
            _owner.OnSteamLobbyReady(_lobbyId, isHost: false);
            ModLog.Event(LogCat.Network,
                "Steam lobby entered — host steamId=" + _hostSteamId.m_SteamID);
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

        /// <summary>Copy lobby id to clipboard when possible (IMGUI helper).</summary>
        public static void CopyToClipboard(string text)
        {
            if (string.IsNullOrEmpty(text))
                return;
            try { GUIUtility.systemCopyBuffer = text; }
            catch { /* no clipboard */ }
        }
    }
}
