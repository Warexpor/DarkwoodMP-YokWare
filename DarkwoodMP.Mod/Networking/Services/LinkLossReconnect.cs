using System;
using DWMPHorde.Logging;
using DWMPHorde.Networking.Steam;
using LiteNetLib;
using Steamworks;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>
    /// Client: the link to the host was lost in the world (timeout, network gone). Before anything
    /// else, the client dials the same host again for <see cref="Config.ModConfig.ReconnectSeconds"/>,
    /// keeping its world (soft reconnect, the same path a joiner takes after its offline load). Only
    /// when that window runs out does the old path run: host migration, or the session ends. Before,
    /// a lost link went straight to migration, so a one-client session made that client its own host
    /// over a network hiccup, and the player saw nothing until it had happened.
    /// The player is told on the HUD and in the chat: link lost, reconnecting, back, or given up.
    /// A host that goes quiet (no packet for <see cref="HostStallNoticeSec"/>) is told too, before the
    /// transport's own 30 s timeout calls the link lost.
    /// </summary>
    public sealed partial class LanNetworkManager
    {
        /// <summary>Time between dials inside the window (a failed LAN dial already took ~5 s).</summary>
        private const float LinkLossRetrySec = 3f;
        /// <summary>A dial still in flight when the window ends gets this long to finish (Steam's connect timeout is 20 s).</summary>
        private const float LinkLossDialGraceSec = 25f;
        private const float HostStallNoticeSec = 8f;

        /// <summary>When the window ends (unscaled time); 0 = no reconnect running.</summary>
        private float _linkLossUntil;
        private float _linkLossRetryAt;
        private int _linkLossTries;
        private string _linkLossAddress;
        private int _linkLossPort;
        private ulong _linkLossLobby;
        private ulong _linkLossHostSid;

        /// <summary>Client: last packet from the host (unscaled time); 0 = none on this link yet.</summary>
        private float _lastHostRxAt;
        private bool _hostStallShown;

        internal bool IsReconnectingToHost => _linkLossUntil > 0f;

        private void ResetLinkLossState()
        {
            _linkLossUntil = 0f;
            _linkLossRetryAt = 0f;
            _linkLossTries = 0;
            _linkLossAddress = null;
            _linkLossPort = 0;
            _linkLossLobby = 0;
            _linkLossHostSid = 0;
            _lastHostRxAt = 0f;
            _hostStallShown = false;
            if (_steam != null)
                _steam.ReconnectExpectedHost = CSteamID.Nil;
        }

        /// <summary>LiteNetLib reasons that mean the link was lost, not closed by either side.</summary>
        private static bool IsLostLinkReason(DisconnectReason reason)
        {
            switch (reason)
            {
                case DisconnectReason.Timeout:
                case DisconnectReason.HostUnreachable:
                case DisconnectReason.NetworkUnreachable:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Client, the host link was lost: start the reconnect window. False when it does not apply
        /// (not in the world, never joined, switched off, intentional leave) and the caller goes on
        /// with migration as before. <paramref name="lanHost"/> is the lost LAN peer (null on Steam).
        /// </summary>
        private bool TryBeginLinkLossReconnect(string reason, NetPeer lanHost)
        {
            if (_role != NetworkRole.Client || _suppressHostMigration || _migrationInProgress)
                return false;
            // Duplicate loss signals (SNS fail + lobby update) while the window runs.
            if (_linkLossUntil > 0f)
                return true;
            if (!_session.Link.HandshakeComplete)
                return false;
            int window = Mathf.Clamp(Config.ModConfig.ReconnectSeconds?.Value ?? 20, 0, 900);
            if (window <= 0)
                return false;
            // The soft reconnect keeps the world: it needs a live, loaded character.
            if (!Sync.ChapterSessionResume.IsLocalPlayableForCoopReconnect())
                return false;

            string address = null;
            int port = 0;
            ulong lobby = 0, hostSid = 0;
            if (IsSteamSession)
            {
                lobby = Steam.LobbyId.IsValid() ? Steam.LobbyId.m_SteamID : 0;
                hostSid = Steam.HostSteamId.IsValid() ? Steam.HostSteamId.m_SteamID : 0;
                if (lobby == 0 || hostSid == 0)
                    return false;
            }
            else
            {
                if (lanHost == null || lanHost.Address == null || lanHost.Port <= 0)
                    return false;
                System.Net.IPAddress ip = lanHost.Address;
                if (ip.IsIPv4MappedToIPv6)
                    ip = ip.MapToIPv4();
                address = ip.ToString();
                port = lanHost.Port;
            }

            // The character is kept in its own file now, as a migration would have done.
            SaveHandlers?.PersistOnLinkLoss("host lost: " + reason);

            _linkLossUntil = Time.unscaledTime + window;
            _linkLossTries = 0;
            _linkLossAddress = address;
            _linkLossPort = port;
            _linkLossLobby = lobby;
            _linkLossHostSid = hostSid;
            _hostStallShown = false;
            ModLog.Event(LogCat.Network, "Host link lost (" + reason + ") — reconnecting for up to " + window + "s"
                + (lobby != 0 ? " (Steam)" : ""));
            ShowLinkNotice("Lost connection to the host — reconnecting…");
            DialLinkLoss();
            return true;
        }

        private void DialLinkLoss()
        {
            // Mid load (a location, a dream): the connect calls would take that for a cold join and
            // drop the world. Dial once the character stands again.
            if (!Sync.ChapterSessionResume.IsLocalPlayableForCoopReconnect())
            {
                _linkLossRetryAt = Time.unscaledTime + 1f;
                return;
            }
            _linkLossRetryAt = 0f;
            _linkLossTries++;
            int left = Mathf.Max(0, Mathf.CeilToInt(_linkLossUntil - Time.unscaledTime));
            ModLog.Event(LogCat.Network, "Reconnect to the host: try " + _linkLossTries + " (" + left + "s left)");
            if (_linkLossLobby != 0)
                DialLinkLossSteam();
            else
                ConnectToHost(_linkLossAddress, _linkLossPort);
            StatusText = "Reconnecting to the host… " + left + "s left";
        }

        private void DialLinkLossSteam()
        {
            var lobby = new CSteamID(_linkLossLobby);
            var hostSid = new CSteamID(_linkLossHostSid);
            if (SteamCoopTransport.IsLocalMemberOf(lobby))
            {
                // Still in the lobby: dial the host straight (it accepts lobby members), as a
                // migration survivor does. Leaving and rejoining the lobby is not needed.
                SoftTearForReconnect("steam link-loss reconnect", leaveSteamLobby: false);
                _role = NetworkRole.Client;
                _backend = ConnectionBackend.Steam;
                NoteSoftReconnectAttempt(null, 0, _linkLossLobby);
                if (!Steam.ConnectP2PDirect(hostSid))
                {
                    OnClientLinkFailed("Steam connect failed");
                    return;
                }
                _steamPeers.Set(_hostPlayerId > 0 ? _hostPlayerId : 1, hostSid);
                // The handshake follows from OnSteamLobbyReady once SNS is connected.
                return;
            }
            // Dropped out of the lobby too (the network was gone): join it again, but only into
            // the same host. A lobby the host left has another owner, who is no host.
            ConnectSteamLobby(lobby);
            if (_steam != null)
                _steam.ReconnectExpectedHost = hostSid;
        }

        /// <summary>
        /// Client: drop the link but keep the world for a reconnect into the same session. Shared by
        /// the LAN and Steam soft reconnects.
        /// </summary>
        private void SoftTearForReconnect(string reason, bool leaveSteamLobby)
        {
            StopTransportOnly(reason, leaveSteamLobby);
            foreach (int id in new System.Collections.Generic.List<int>(_remoteProxies.Keys))
                WorldProxyLifecycleHandlers.DestroyRemoteProxy(id);
            _remoteProxies.Clear();
            _remotePlayers.Clear();
            // Proxies gone — drop sticky membership so host LocationEnter re-places.
            LocationEnterExitHandlers?.ClearMembershipForSoftReconnect();
            PlayerLightFxApplyHandlers?.ClearPendingPlayerLights();
            PlayerFXHandlers?.ClearAllPendingAnimLibraries();
            _session.Link = new LinkState();
            _session.HostWasShareableForWaitingClients = false;
            _session.HostWorldReadyEmitted = false;
            _session.ClientHostWorldReady = false;
        }

        /// <summary>
        /// A dial inside the window failed. True when the window took it (a retry is armed, or the
        /// window ran out and the fallback ran); false when no window runs.
        /// </summary>
        private bool OnLinkLossDialFailed(string reason)
        {
            if (_role != NetworkRole.Client || _linkLossUntil <= 0f)
                return false;
            // Dead peer slots out: IsConnected / sends must not target a link that never came up.
            ClearAllPeerSlots();
            if (Time.unscaledTime >= _linkLossUntil)
            {
                EndLinkLossReconnectFailed(reason);
                return true;
            }
            _linkLossRetryAt = Time.unscaledTime + LinkLossRetrySec;
            int left = Mathf.Max(0, Mathf.CeilToInt(_linkLossUntil - Time.unscaledTime));
            StatusText = "Reconnecting to the host… " + left + "s left";
            ModLog.Event(LogCat.Network, "Reconnect try " + _linkLossTries + " failed (" + reason + ") — "
                + left + "s left");
            return true;
        }

        /// <summary>Client handshake on the new link: the reconnect worked.</summary>
        private void OnLinkLossReconnected()
        {
            _lastHostRxAt = Time.unscaledTime;
            _hostStallShown = false;
            if (_linkLossUntil <= 0f)
                return;
            ModLog.Event(LogCat.Network, "Reconnected to the host after " + _linkLossTries + " tr"
                + (_linkLossTries == 1 ? "y" : "ies"));
            _linkLossUntil = 0f;
            _linkLossRetryAt = 0f;
            _linkLossTries = 0;
            if (_steam != null)
                _steam.ReconnectExpectedHost = CSteamID.Nil;
            ShowLinkNotice("Reconnected to the host");
        }

        /// <summary>
        /// The window ran out, or the host is known gone (left its Steam lobby): what a lost link did
        /// before runs now, host migration or the end of the session.
        /// </summary>
        private void EndLinkLossReconnectFailed(string reason)
        {
            if (_linkLossUntil <= 0f)
                return;
            ModLog.Warn(LogCat.Network, "Could not reconnect to the host (" + reason + ") after "
                + _linkLossTries + " tries");
            _linkLossUntil = 0f;
            _linkLossRetryAt = 0f;
            _linkLossTries = 0;
            if (_steam != null)
                _steam.ReconnectExpectedHost = CSteamID.Nil;
            // The window's dials are over: a later failure is not a soft-reconnect retry.
            _softReconnectTries = 0;
            _softReconnectRetryAt = 0f;
            ShowLinkNotice("Could not reconnect to the host");
            TryBeginHostMigration("reconnect failed: " + reason, afterReconnectWindow: true);
        }

        /// <summary>Steam: the host left its lobby while the window ran. It is gone; no point waiting.</summary>
        internal void OnLinkLossHostGone(string reason)
        {
            if (_linkLossUntil > 0f)
                EndLinkLossReconnectFailed(reason);
        }

        private void TickLinkLossReconnect()
        {
            TickHostStallNotice();
            if (_linkLossUntil <= 0f)
                return;
            if (_role != NetworkRole.Client)
            {
                // The session ended or this player took the host meanwhile.
                ResetLinkLossState();
                return;
            }
            float now = Time.unscaledTime;
            if (_linkLossRetryAt > 0f)
            {
                if (now >= _linkLossRetryAt)
                    DialLinkLoss();
                return;
            }
            // A dial that neither connects nor fails (a hung lobby join, a refused manual join)
            // must not hold the window open past its end.
            if (now >= _linkLossUntil + LinkLossDialGraceSec && !_session.Link.HandshakeComplete)
                EndLinkLossReconnectFailed("no answer");
        }

        /// <summary>Client: the host went quiet. Said once, and said again when it answers.</summary>
        private void TickHostStallNotice()
        {
            if (_role != NetworkRole.Client || _linkLossUntil > 0f || _lastHostRxAt <= 0f
                || !_session.Link.HandshakeComplete || !IsConnected)
            {
                _hostStallShown = false;
                return;
            }
            float silent = Time.unscaledTime - _lastHostRxAt;
            if (!_hostStallShown && silent >= HostStallNoticeSec)
            {
                _hostStallShown = true;
                ModLog.Event(LogCat.Network, "Host silent for " + (int)silent + "s");
                ShowLinkNotice("The host is not responding — waiting…");
            }
            else if (_hostStallShown && silent < 1f)
            {
                _hostStallShown = false;
                ModLog.Event(LogCat.Network, "Host answering again");
                ShowLinkNotice("Connection to the host is back");
            }
        }

        private static void ShowLinkNotice(string text)
        {
            string line = Loc.T(text);
            try { ChatHud.AddSystemLine(line); }
            catch (Exception ex) { ModLog.Swallowed(ex); /* chat not up */ }
            try
            {
                if (Player.Instance != null && !GameScreen.AtTitle && !Core.loadingGame)
                {
                    DWMPHorde.Patches.PersonalFlavorHud.BeginBypass();
                    try { Player.Instance.displayMessage(line); }
                    finally { DWMPHorde.Patches.PersonalFlavorHud.EndBypass(); }
                }
            }
            catch (Exception ex)
            {
                ModLog.Warn(LogCat.Network, "Link notice: " + ex.Message);
            }
        }
    }
}
