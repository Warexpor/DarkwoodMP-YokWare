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
    public sealed partial class SteamCoopTransport
    {
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

    }
}
