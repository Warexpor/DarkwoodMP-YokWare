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

        /// <summary>
        /// Host-grant: allow roster Steam IDs to ConnectP2P without lobby membership.
        /// </summary>
        public void ArmMigrationAllowlist(IEnumerable<ulong> steamIds, double seconds = 45)
        {
            _migrationAllow.Clear();
            if (steamIds != null)
            {
                foreach (ulong id in steamIds)
                {
                    if (id != 0)
                        _migrationAllow.Add(id);
                }
            }
            _migrationAllowUntilUtc = DateTime.UtcNow.AddSeconds(seconds);
            ModLog.Event(LogCat.Network,
                "Steam migration allowlist armed n=" + _migrationAllow.Count
                + " for " + seconds.ToString("0") + "s");
        }

        private bool MigrationAllows(CSteamID steamId)
        {
            if (!steamId.IsValid())
                return false;
            if (DateTime.UtcNow > _migrationAllowUntilUtc)
                return false;
            return _migrationAllow.Contains(steamId.m_SteamID);
        }

        /// <summary>Graceful leave: pass lobby ownership before the old host disconnects.</summary>
        public bool TransferLobbyOwner(CSteamID newOwner)
        {
            if (!_lobbyId.IsValid() || !newOwner.IsValid())
                return false;
            try
            {
                bool ok = SteamMatchmaking.SetLobbyOwner(_lobbyId, newOwner);
                ModLog.Event(LogCat.Network,
                    "Steam SetLobbyOwner → " + newOwner.m_SteamID + " ok=" + ok);
                return ok;
            }
            catch (Exception ex)
            {
                ModLog.Warn(LogCat.Network, "SetLobbyOwner failed: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Host-grant promote: listen on SNS; keep existing lobby if still a member, else CreateLobby.
        /// Caller must already have torn the client SNS role (Shutdown leaveLobby:false).
        /// </summary>
        public bool BeginHostingAfterMigration()
        {
            if (!IsSteamReady(out string fail))
            {
                ModLog.Error(LogCat.Network, "Steam promote failed: " + fail);
                return false;
            }

            EnsureCallbacks();
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

            SteamRelay.WarmRelay();
            if (!CreateListenSocket())
                return false;

            _hosting = true;
            _active = true;
            _hostSteamId = LocalSteamId();
            _clientTransportReady = false;
            _clientConnectStartedUtc = DateTime.MinValue;

            if (_lobbyId.IsValid() && IsLocalInLobby(_lobbyId))
            {
                try
                {
                    CSteamID owner = SteamMatchmaking.GetLobbyOwner(_lobbyId);
                    if (owner != LocalSteamId())
                        SteamMatchmaking.SetLobbyOwner(_lobbyId, LocalSteamId());
                }
                catch (Exception ex)
                {
                    ModLog.Warn(LogCat.Network, "Promote SetLobbyOwner: " + ex.Message);
                }
                ApplyHostLobbyData();
                _owner.OnSteamLobbyReady(_lobbyId, isHost: true);
                ModLog.Event(LogCat.Network,
                    "Steam promote: hosting existing lobby " + _lobbyId.m_SteamID);
                return true;
            }

            _lobbyId = CSteamID.Nil;
            int max = Mathf.Clamp(ModConfig.MaxPlayers?.Value ?? 8, 2, 16);
            ELobbyType lobbyType = ResolveLobbyType();
            SteamAPICall_t call = SteamMatchmaking.CreateLobby(lobbyType, max);
            _crLobbyCreated.Set(call);
            ModLog.Event(LogCat.Network,
                "Steam promote: CreateLobby (old lobby gone) type=" + lobbyType);
            return true;
        }

        /// <summary>
        /// Host-grant reconnect: ConnectP2P to elected host Steam ID (lobby optional).
        /// </summary>
        public bool ConnectP2PDirect(CSteamID hostSteamId)
        {
            if (!IsSteamReady(out string fail))
            {
                ModLog.Error(LogCat.Network, "Steam migration connect failed: " + fail);
                return false;
            }
            if (!hostSteamId.IsValid() || hostSteamId == LocalSteamId())
            {
                ModLog.Error(LogCat.Network, "Steam migration connect: bad host steam id");
                return false;
            }

            EnsureCallbacks();
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

            SteamRelay.WarmRelay();
            _hosting = false;
            _active = true;
            _clientTransportReady = false;
            _hostSteamId = hostSteamId;

            SteamNetworkingIdentity identity = default;
            identity.SetSteamID(hostSteamId);
            _serverConn = SteamNetworkingSockets.ConnectP2P(ref identity, VirtualPort, 0, null);
            if (_serverConn == HSteamNetConnection.Invalid)
            {
                ModLog.Error(LogCat.Network, "ConnectP2PDirect failed");
                return false;
            }

            _clientConnectStartedUtc = DateTime.UtcNow;
            ModLog.Event(LogCat.Network,
                "Steam migration ConnectP2P → " + hostSteamId.m_SteamID
                + " lobby=" + (_lobbyId.IsValid() ? _lobbyId.m_SteamID.ToString() : "none"));
            return true;
        }

        private static bool IsLocalInLobby(CSteamID lobbyId)
        {
            if (!lobbyId.IsValid())
                return false;
            try
            {
                CSteamID self = LocalSteamId();
                int n = SteamMatchmaking.GetNumLobbyMembers(lobbyId);
                for (int i = 0; i < n; i++)
                {
                    if (SteamMatchmaking.GetLobbyMemberByIndex(lobbyId, i) == self)
                        return true;
                }
            }
            catch { /* steam tear */ }
            return false;
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


        /// <summary>False when Steam's in-game overlay is off for this game (its dialogs will not show).</summary>
        public static bool OverlayEnabled()
        {
            try { return IsSteamReady(out _) && SteamUtils.IsOverlayEnabled(); }
            catch { return false; }
        }

        /// <summary>Open Steam's friends list over the game. False when the overlay is off.</summary>
        public static bool OpenFriendsOverlay()
        {
            if (!OverlayEnabled())
                return false;
            try
            {
                SteamFriends.ActivateGameOverlay("friends");
                return true;
            }
            catch (Exception ex)
            {
                ModLog.Warn(LogCat.Network, "Friends overlay failed: " + ex.Message);
                return false;
            }
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
