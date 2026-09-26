using System;
using DWMPHorde.Config;
using DWMPHorde.Logging;
using DWMPHorde.Networking;
using UnityEngine;

namespace DWMPHorde
{
    public static partial class MainMenuMultiplayerInject
    {
        private static void OnHostLanClicked()
        {
            MultiplayerMenu.EnsureExists();
            MultiplayerMenu.PushFieldsToConfig();

            var net = ModRuntime.Network;
            if (net == null)
                return;
            if (net.Role != NetworkRole.Offline)
            {
                ModLog.Event(LogCat.Session, "Already in a session — use DISCONNECT first.");
                return;
            }

            int port = ModConfig.ConnectPort != null ? ModConfig.ConnectPort.Value : PluginInfo.DefaultPort;
            if (port < 1 || port > 65535)
                port = PluginInfo.DefaultPort;

            net.StartHost(port);
            if (net.Role != NetworkRole.Host)
            {
                ModLog.Event(LogCat.Session, "Host failed: " + (net.StatusText ?? "bind error"));
                return;
            }

            _joinPending = false;
            _hostingHint = true;
            ModLog.Event(LogCat.Session,
                "Hosting LAN on port " + port
                + " — load a save; clients on JOIN get the world after you are in-chapter.");
            ClosePanel();
            if (_menu != null)
                _menu.displayProfilesMenu();
        }

        private static void OnHostSteamClicked()
        {
            MultiplayerMenu.EnsureExists();
            MultiplayerMenu.PushFieldsToConfig();

            var net = ModRuntime.Network;
            if (net == null)
                return;
            if (net.Role != NetworkRole.Offline)
            {
                ModLog.Event(LogCat.Session, "Already in a session — use DISCONNECT first.");
                return;
            }

            net.StartHostSteam();
            if (net.Role != NetworkRole.Host)
            {
                ModLog.Event(LogCat.Session, "Steam host failed: " + (net.StatusText ?? "steam error"));
                return;
            }

            _joinPending = false;
            _hostingHint = true;
            ModLog.Event(LogCat.Session,
                "Hosting Steam lobby — invite via overlay (SETTINGS shows lobby id). "
                + "Load a save; clients join after you are in-chapter.");
            ClosePanel();
            if (_menu != null)
                _menu.displayProfilesMenu();
        }

        private static void OnJoinLanClicked()
        {
            _joinViaSteam = false;
            BeginOrContinueJoin(steam: false);
        }

        private static void OnJoinSteamClicked()
        {
            _joinViaSteam = true;
            BeginOrContinueJoin(steam: true);
        }

        private static void BeginOrContinueJoin(bool steam)
        {
            MultiplayerMenu.EnsureExists();
            MultiplayerMenu.PushFieldsToConfig();

            var net = ModRuntime.Network;
            if (net == null || _joinPending)
                return;

            var lanReady = net as LanNetworkManager;
            if (lanReady?.WorldSaveShare != null && lanReady.WorldSaveShare.IsAwaitingSlotPick)
            {
                SetJoinProgress("CHOOSE SLOT");
                JoinWorldSlotPicker.EnsureExists();
                return;
            }
            if (lanReady?.WorldSaveShare != null && lanReady.WorldSaveShare.IsAwaitingEnterWorld)
            {
                if (lanReady.WorldSaveShare.HasTerminalShareFailure)
                {
                    ModLog.Warn(LogCat.Session,
                        "ENTER WORLD blocked — " + lanReady.WorldSaveShare.ProgressText);
                    SetJoinProgress("SHARE FAIL");
                    return;
                }
                if (lanReady.WorldSaveShare.TryBeginEnterWorld())
                {
                    SetJoinProgress("LOADING…");
                    ModLog.Event(LogCat.Session, "ENTER WORLD — starting offline load (phase 2)");
                }
                return;
            }

            if (net.Role == NetworkRole.Client && net.IsHandshakeComplete && Core.mainMenu)
            {
                var lan = net as LanNetworkManager;
                if (lan?.WorldSaveShare != null && lan.WorldSaveShare.IsClientReceivingOrApplying)
                {
                    SetJoinProgress("DOWNLOADING…");
                    return;
                }
                if (lan != null && !lan.ClientSeesHostWorldReady)
                {
                    SetJoinProgress("WAIT HOST…");
                    ModLog.Event(LogCat.Session,
                        "JOIN while connected — host not fully in-world yet; waiting (no download).");
                    // Still nudge host in case they are ready but signal was missed.
                    lan.RequestHostWorld("join-button-wait-host");
                }
                else if (lan != null && lan.RequestHostWorld("join-button"))
                {
                    SetJoinProgress("REQUESTING WORLD…");
                    ModLog.Event(LogCat.Session, "JOIN while connected — WorldRequest sent to host.");
                }
                else
                {
                    SetJoinProgress(lan != null && lan.ClientSeesHostWorldReady
                        ? "WAITING…"
                        : "WAIT HOST…");
                    ModLog.Event(LogCat.Session,
                        "JOIN while connected — request rate-limited or share already in progress.");
                }
                return;
            }

            if (net.Role != NetworkRole.Offline)
            {
                ModLog.Event(LogCat.Session, "Already in a session — use DISCONNECT first.");
                return;
            }

            if (steam)
            {
                string lobby = (ModConfig.SteamLobbyId != null ? ModConfig.SteamLobbyId.Value : "") ?? "";
                lobby = lobby.Trim();
                if (string.IsNullOrEmpty(lobby))
                {
                    ModLog.Event(LogCat.Session, "JOIN STEAM: set lobby id in SETTINGS (or accept a Steam invite).");
                    MultiplayerMenu.ShowSettings();
                    return;
                }

                net.ConnectSteam(lobby);
                _joinPending = true;
                _joinStartedAt = Time.realtimeSinceStartup;
                _handshakeAt = 0f;
                _loggedWaitingWorld = false;
                _worldRequest10sSent = false;
                _worldRequest25sSent = false;
                SetJoinProgress("STEAM…");
                ModLog.Event(LogCat.Session, "Connecting Steam lobby " + lobby + " …");
                return;
            }

            string ip = (ModConfig.ConnectAddress != null ? ModConfig.ConnectAddress.Value : "127.0.0.1") ?? "127.0.0.1";
            ip = ip.Trim();
            if (string.IsNullOrEmpty(ip))
                ip = "127.0.0.1";

            int port = ModConfig.ConnectPort != null ? ModConfig.ConnectPort.Value : PluginInfo.DefaultPort;
            if (port < 1 || port > 65535)
                port = PluginInfo.DefaultPort;

            net.ConnectToHost(ip, port);
            _joinPending = true;
            _joinStartedAt = Time.realtimeSinceStartup;
            _handshakeAt = 0f;
            _loggedWaitingWorld = false;
            _worldRequest10sSent = false;
            _worldRequest25sSent = false;
            SetJoinProgress("CONNECTING…");
            ModLog.Event(LogCat.Session, "Connecting to " + ip + ":" + port + " …");
        }

        private static void SetJoinProgress(string text)
        {
            ResetInactiveJoinLabel();
            SetLabel(ActiveJoinButton, text);
        }

        private static void ResetInactiveJoinLabel()
        {
            if (_joinViaSteam)
                SetLabel(_joinLanBtn, "JOIN LAN");
            else
                SetLabel(_joinSteamBtn, "JOIN STEAM");
        }

        private static void ResetJoinLabelsIdle()
        {
            SetLabel(_joinLanBtn, "JOIN LAN");
            SetLabel(_joinSteamBtn, "JOIN STEAM");
        }

        private static void PollJoinState()
        {
            var net = ModRuntime.Network;
            if (net == null)
            {
                _joinPending = false;
                return;
            }

            if (net.Role == NetworkRole.Client && net.IsHandshakeComplete)
            {
                bool firstReady = _joinPending;
                _joinPending = false;
                if (firstReady)
                {
                    _handshakeAt = Time.realtimeSinceStartup;
                    _loggedWaitingWorld = false;
                    _worldRequest10sSent = false;
                    _worldRequest25sSent = false;
                    ModLog.Event(LogCat.Session,
                        "Connected to host — waiting for host fully in-world, then world share / auto-load…");
                }
                UpdateJoinLabelFromShare(net);
                RefreshSessionButtons();
                return;
            }

            if (net.Role == NetworkRole.Host)
            {
                _joinPending = false;
                ResetJoinLabelsIdle();
                RefreshSessionButtons();
                return;
            }

            bool steamJoin = net.IsSteamSession || _joinViaSteam;
            float joinTimeout = steamJoin ? SteamJoinTimeoutSec : JoinTimeoutSec;
            if (net.Role == NetworkRole.Offline
                || Time.realtimeSinceStartup - _joinStartedAt > joinTimeout)
            {
                bool wasTimeout = net.Role != NetworkRole.Offline;
                _joinPending = false;
                if (wasTimeout)
                    net.StopNetwork();
                ResetJoinLabelsIdle();
                RefreshSessionButtons();
                ModLog.Event(LogCat.Session,
                    wasTimeout
                        ? (steamJoin
                            ? "Steam join timeout — lobby id / password / proto / friends, or SNS relay."
                            : "Join timeout — check IP/port/password in SETTINGS (and firewall).")
                        : "Connection closed.");
            }
        }

        private static void PollPostHandshakeWorldWait()
        {
            var net = ModRuntime.Network as LanNetworkManager;
            if (net == null || net.Role != NetworkRole.Client || !net.IsHandshakeComplete)
                return;
            if (!Core.mainMenu)
                return;

            UpdateJoinLabelFromShare(net);

            if (_handshakeAt <= 0f)
                return;

            if (net.WorldSaveShare != null
                && (net.WorldSaveShare.IsAwaitingSlotPick
                    || net.WorldSaveShare.IsAwaitingEnterWorld
                    || net.WorldSaveShare.IsClientReceivingOrApplying))
                return;

            float waited = Time.realtimeSinceStartup - _handshakeAt;
            bool receiving = IsShareProgressActive(net);

            if (!_loggedWaitingWorld && waited > 8f && !receiving)
            {
                _loggedWaitingWorld = true;
                bool hostReady = net.ClientSeesHostWorldReady;
                ModLog.Warn(LogCat.Session,
                    "Still on title 8s after handshake with no world download. "
                    + (hostReady
                        ? "Host announced ready — waiting for world package. "
                        : "Host not fully in-world yet (loading / title). ")
                    + "Auto WorldRequest at 10s; or press JOIN again / host F2 Resend.");
            }

            if (!receiving && waited >= 10f && !_worldRequest10sSent)
            {
                _worldRequest10sSent = true;
                if (net.RequestHostWorld("title-wait-10s"))
                    SetJoinProgress(net.ClientSeesHostWorldReady
                        ? "REQUESTING WORLD…"
                        : "WAIT HOST…");
            }
            else if (!receiving && waited >= 25f && !_worldRequest25sSent)
            {
                _worldRequest25sSent = true;
                if (net.RequestHostWorld("title-wait-25s"))
                    SetJoinProgress(net.ClientSeesHostWorldReady
                        ? "REQUESTING WORLD…"
                        : "WAIT HOST…");
            }
        }

        private static bool IsShareProgressActive(LanNetworkManager net)
        {
            if (net?.WorldSaveShare == null)
                return false;
            if (net.WorldSaveShare.IsClientReceivingOrApplying)
                return true;
            if (net.WorldSaveShare.IsAwaitingSlotPick || net.WorldSaveShare.IsAwaitingEnterWorld)
                return true;
            string prog = net.WorldSaveShare.ProgressText ?? "";
            if (string.IsNullOrEmpty(prog))
                return false;
            return prog.IndexOf("Receiv", StringComparison.OrdinalIgnoreCase) >= 0
                || prog.IndexOf("Load", StringComparison.OrdinalIgnoreCase) >= 0
                || prog.IndexOf("Send", StringComparison.OrdinalIgnoreCase) >= 0
                || prog.IndexOf("Appl", StringComparison.OrdinalIgnoreCase) >= 0
                || prog.IndexOf("Request", StringComparison.OrdinalIgnoreCase) >= 0
                || prog.IndexOf("Pick a profile", StringComparison.OrdinalIgnoreCase) >= 0
                || prog.IndexOf("Same world", StringComparison.OrdinalIgnoreCase) >= 0
                || prog.IndexOf("ENTER WORLD", StringComparison.OrdinalIgnoreCase) >= 0
                || prog.IndexOf("Permanent", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsShareFailureBlocked(LanNetworkManager net)
        {
            var share = net?.WorldSaveShare;
            return share != null && share.HasTerminalShareFailure;
        }

        private static void UpdateJoinLabelFromShare(LanNetworkManager net)
        {
            if (net == null)
                return;
            if (IsShareFailureBlocked(net))
            {
                SetJoinProgress("SHARE FAIL");
                return;
            }
            if (net.WorldSaveShare != null && net.WorldSaveShare.IsAwaitingSlotPick)
            {
                SetJoinProgress("CHOOSE SLOT");
                return;
            }
            if (net.WorldSaveShare != null && net.WorldSaveShare.IsAwaitingEnterWorld)
            {
                SetJoinProgress("ENTER WORLD");
                return;
            }
            string prog = net.WorldSaveShare != null ? net.WorldSaveShare.ProgressText : null;
            if (!string.IsNullOrEmpty(prog))
            {
                if (prog.IndexOf("fail", StringComparison.OrdinalIgnoreCase) >= 0
                    || prog.IndexOf("FAILED", StringComparison.OrdinalIgnoreCase) >= 0)
                    SetJoinProgress("SHARE FAIL");
                else if (WrongSaveWarning.IsWrongSaveMessage(prog)
                    || prog.IndexOf("WRONG SAVE", StringComparison.OrdinalIgnoreCase) >= 0
                    || prog.IndexOf("DIFFERENT CAMPAIGN", StringComparison.OrdinalIgnoreCase) >= 0)
                    SetJoinProgress("WRONG SAVE");
                else if (prog.IndexOf("ENTER WORLD", StringComparison.OrdinalIgnoreCase) >= 0
                    || prog.IndexOf("Permanent copy", StringComparison.OrdinalIgnoreCase) >= 0
                    || prog.IndexOf("World ready", StringComparison.OrdinalIgnoreCase) >= 0)
                    SetJoinProgress("ENTER WORLD");
                else if (prog.IndexOf("Pick a profile", StringComparison.OrdinalIgnoreCase) >= 0
                    || prog.IndexOf("permanent", StringComparison.OrdinalIgnoreCase) >= 0)
                    SetJoinProgress("CHOOSE SLOT");
                else if (prog.IndexOf("Receiv", StringComparison.OrdinalIgnoreCase) >= 0
                    || prog.IndexOf("Send", StringComparison.OrdinalIgnoreCase) >= 0
                    || prog.IndexOf("Writ", StringComparison.OrdinalIgnoreCase) >= 0
                    || prog.IndexOf("Inflat", StringComparison.OrdinalIgnoreCase) >= 0
                    || prog.IndexOf("Verif", StringComparison.OrdinalIgnoreCase) >= 0)
                    SetJoinProgress("DOWNLOADING…");
                else if (prog.IndexOf("Load", StringComparison.OrdinalIgnoreCase) >= 0
                         || prog.IndexOf("Appl", StringComparison.OrdinalIgnoreCase) >= 0)
                    SetJoinProgress("LOADING…");
                else if (prog.IndexOf("Request", StringComparison.OrdinalIgnoreCase) >= 0)
                    SetJoinProgress("REQUESTING WORLD…");
                else
                    SetJoinProgress("CONNECTED");
            }
            else if (net.IsHandshakeComplete)
            {
                if (!net.ClientSeesHostWorldReady && Core.mainMenu)
                    SetJoinProgress("WAIT HOST…");
                else if (net.ClientSeesHostWorldReady && Core.mainMenu)
                    SetJoinProgress("HOST READY");
                else
                    SetJoinProgress("CONNECTED");
            }
        }

        private static void TryConsumeSteamLaunchLobby()
        {
            var net = ModRuntime.Network;
            if (net == null)
                return;

            net.EnsureSteamCallbacks();

            if (_launchLobbyTried)
                return;
            try
            {
                if (!Core.mainMenu)
                    return;
            }
            catch { return; }

            _launchLobbyTried = true;
            net.TryConsumePendingSteamLaunchLobby();
        }

    }
}
