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
                    if (LobbyAllowsSteamId(steamId))
                    {
                        AcceptHostConnection(cb.m_hConn);
                    }
                    else
                    {
                        // Member-list can lag after JoinLobby; park the connection and
                        // re-check in Poll() before rejecting. This blocks random Steam
                        // users who are not (and never will be) lobby members.
                        _pendingHostConnects[steamId.m_SteamID] = new PendingHostConnect
                        {
                            Conn = cb.m_hConn,
                            Since = DateTime.UtcNow
                        };
                        ModLog.Event(LogCat.Network,
                            "Steam SNS pending " + steamId.m_SteamID + " (lobby list grace)");
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
                    // A connection in a closed/problem state still owns its handle until we close it.
                    // UntrackConn runs first, so the owner's CloseSession can no longer find it —
                    // closing here is the only place the handle is released (leaked one per peer loss).
                    try
                    {
                        SteamNetworkingSockets.CloseConnection(
                            cb.m_hConn, CloseReasonGeneric, "peer lost", false);
                    }
                    catch { /* tear */ }
                    if (_pendingHostConnects.TryGetValue(steamId.m_SteamID, out PendingHostConnect parked)
                        && parked.Conn == cb.m_hConn)
                        _pendingHostConnects.Remove(steamId.m_SteamID);
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
                // Pre-handshake: no peer map yet; tear down via lobby-failed (StopNetwork).
                if (!_clientTransportReady)
                    _owner.OnSteamLobbyFailed("SNS connection lost: " + detail);
                else
                    _owner.OnSteamSessionFailed(_hostSteamId);
            }
        }

        private bool LobbyAllowsSteamId(CSteamID steamId)
        {
            if (!steamId.IsValid())
                return false;
            // Host-grant survivors reconnect by Steam ID before (re)joining the lobby.
            if (MigrationAllows(steamId))
                return true;
            if (!_lobbyId.IsValid())
                return false;
            int n = SteamMatchmaking.GetNumLobbyMembers(_lobbyId);
            for (int i = 0; i < n; i++)
            {
                if (SteamMatchmaking.GetLobbyMemberByIndex(_lobbyId, i) == steamId)
                    return true;
            }
            // Host-only lobby (no members listed yet): the connecting peer is the first
            // joiner whose member-list entry has not replicated; allow through the grace
            // window instead of refusing them out of hand.
            return n == 0;
        }

        private void AcceptHostConnection(HSteamNetConnection conn)
        {
            if (SteamNetworkingSockets.AcceptConnection(conn) != EResult.k_EResultOK)
            {
                SteamNetworkingSockets.CloseConnection(
                    conn, CloseReasonGeneric, "accept failed", false);
            }
        }

        private void ResolvePendingHostConnects()
        {
            var resolved = new List<ulong>();
            foreach (var kvp in _pendingHostConnects)
            {
                if (LobbyAllowsSteamId(new CSteamID(kvp.Key)))
                {
                    AcceptHostConnection(kvp.Value.Conn);
                    ModLog.Event(LogCat.Network, "Steam SNS accepted " + kvp.Key + " (lobby list resolved)");
                    resolved.Add(kvp.Key);
                }
                else if (DateTime.UtcNow - kvp.Value.Since > HostLobbyGrace)
                {
                    SteamNetworkingSockets.CloseConnection(
                        kvp.Value.Conn, CloseReasonRejected, "not in lobby", false);
                    ModLog.Warn(LogCat.Network,
                        "Steam SNS refused " + kvp.Key + " (not in lobby after grace)");
                    resolved.Add(kvp.Key);
                }
            }
            foreach (ulong id in resolved)
                _pendingHostConnects.Remove(id);
        }

        private void TrackConn(HSteamNetConnection conn, ulong steamId)
        {
            if (conn == HSteamNetConnection.Invalid || steamId == 0)
                return;
            _connBySteamId[steamId] = conn;
            _steamIdByConn[conn.m_HSteamNetConnection] = steamId;
        }

    }
}
