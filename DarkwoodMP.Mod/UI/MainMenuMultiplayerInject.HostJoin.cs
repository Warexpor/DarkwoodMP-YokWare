using System;
using DWMPHorde.Config;
using DWMPHorde.Logging;
using DWMPHorde.Networking;
using UnityEngine;

namespace DWMPHorde
{
    public static partial class MainMenuMultiplayerInject
    {
        /// <summary>Start hosting on the local network. False (with a status line) when it did not start.</summary>
        internal static bool HostLan()
        {
            var net = ModRuntime.Network;
            if (net == null)
                return false;
            if (net.Role != NetworkRole.Offline)
            {
                Flash("Already in a game — disconnect first");
                return false;
            }

            int port = ModConfig.GetConnectPort();
            net.StartHost(port);
            if (net.Role != NetworkRole.Host)
            {
                ModLog.Event(LogCat.Session, "Host failed: " + (net.StatusText ?? "bind error"));
                Flash(FailureText(net.StatusText, "Could not start hosting"));
                return false;
            }

            _joinPending = false;
            _hostingHint = GameScreen.AtTitle;
            ModLog.Event(LogCat.Session,
                "Hosting LAN on port " + port
                + (GameScreen.AtTitle ? " — load a save; clients get the world once you are in-chapter." : " — in-world; clients get this world."));
            return true;
        }

        /// <summary>Start hosting a Steam lobby. False (with a status line) when it did not start.</summary>
        internal static bool HostSteam()
        {
            var net = ModRuntime.Network;
            if (net == null)
                return false;
            if (net.Role != NetworkRole.Offline)
            {
                Flash("Already in a game — disconnect first");
                return false;
            }

            net.StartHostSteam();
            if (net.Role != NetworkRole.Host)
            {
                ModLog.Event(LogCat.Session, "Steam host failed: " + (net.StatusText ?? "steam error"));
                Flash(FailureText(net.StatusText, "Could not start hosting"));
                return false;
            }

            _joinPending = false;
            _hostingHint = GameScreen.AtTitle;
            ModLog.Event(LogCat.Session,
                "Hosting Steam lobby — invite from the multiplayer menu or the Steam overlay. "
                + (GameScreen.AtTitle ? "Load a save; clients join after you are in-chapter." : "In-world; clients get this world."));
            return true;
        }

        /// <summary>Test pilot: one press of Connect (connect, request, enter world as it progresses).</summary>
        internal static void PilotJoinLan()
        {
            _joinViaSteam = false;
            BeginOrContinueJoin(steam: false);
        }

        internal static void JoinLan()
        {
            _joinViaSteam = false;
            BeginOrContinueJoin(steam: false);
        }

        internal static void JoinSteam()
        {
            _joinViaSteam = true;
            BeginOrContinueJoin(steam: true);
        }

        /// <summary>Enter the downloaded host world (the Join screen's "Enter world").</summary>
        internal static void EnterWorld()
        {
            BeginOrContinueJoin(_joinViaSteam);
        }

        private static void BeginOrContinueJoin(bool steam)
        {
            var net = ModRuntime.Network;
            if (net == null || _joinPending)
                return;

            var share = net.WorldSaveShare;
            if (share != null && share.IsAwaitingSlotPick)
            {
                MultiplayerScreens.OpenSlotPicker();
                return;
            }
            if (share != null && share.IsAwaitingEnterWorld)
            {
                if (share.HasTerminalShareFailure)
                {
                    ModLog.Warn(LogCat.Session, "ENTER WORLD blocked — " + share.ProgressText);
                    return;
                }
                if (share.TryBeginEnterWorld())
                    ModLog.Event(LogCat.Session, "ENTER WORLD — starting offline load (phase 2)");
                return;
            }

            if (net.Role == NetworkRole.Client && net.IsHandshakeComplete && GameScreen.AtTitle)
            {
                if (share != null && share.IsClientReceivingOrApplying)
                    return;
                if (!net.ClientSeesHostWorldReady)
                {
                    ModLog.Event(LogCat.Session,
                        "JOIN while connected — host not fully in-world yet; waiting (no download).");
                    // Still nudge the host in case they are ready but the signal was missed.
                    net.RequestHostWorld("join-button-wait-host");
                }
                else if (net.RequestHostWorld("join-button"))
                    ModLog.Event(LogCat.Session, "JOIN while connected — WorldRequest sent to host.");
                else
                    ModLog.Event(LogCat.Session,
                        "JOIN while connected — request rate-limited or share already in progress.");
                return;
            }

            if (net.Role != NetworkRole.Offline)
            {
                Flash("Already in a game — disconnect first");
                return;
            }

            if (steam)
            {
                string lobby = ((ModConfig.SteamLobbyId != null ? ModConfig.SteamLobbyId.Value : "") ?? "").Trim();
                if (string.IsNullOrEmpty(lobby))
                {
                    ModLog.Event(LogCat.Session, "JOIN STEAM: no lobby id (type one in, or accept a Steam invite).");
                    Flash("Type in the Steam lobby id, or accept an invite in Steam");
                    return;
                }

                net.ConnectSteam(lobby);
                if (net.Role == NetworkRole.Offline)
                {
                    // Bad lobby id / Steam not ready: nothing is connecting, so no join timer.
                    ModLog.Event(LogCat.Session, "JOIN STEAM failed: " + (net.StatusText ?? "steam error"));
                    Flash(FailureText(net.StatusText, "Could not join the lobby"));
                    return;
                }
                BeginJoinTimer();
                ModLog.Event(LogCat.Session, "Connecting Steam lobby " + lobby + " …");
                return;
            }

            string ip = ((ModConfig.ConnectAddress != null ? ModConfig.ConnectAddress.Value : "127.0.0.1") ?? "127.0.0.1").Trim();
            if (string.IsNullOrEmpty(ip))
                ip = "127.0.0.1";
            int port = ModConfig.GetConnectPort();

            net.ConnectToHost(ip, port);
            BeginJoinTimer();
            ModLog.Event(LogCat.Session, "Connecting to " + ip + ":" + port + " …");
        }

        /// <summary>Leave the session (a host hands it to a client when one can take over).</summary>
        internal static void Disconnect()
        {
            var net = ModRuntime.Network;
            if (net == null)
                return;
            _joinPending = false;
            _hostingHint = false;
            if (net.Role == NetworkRole.Host && net.TryGracefulHostLeave())
            {
                ModLog.Event(LogCat.Session, "Host disconnect — handing off to elect…");
                return;
            }
            net.StopNetwork();
            ModLog.Event(LogCat.Session, "Disconnected.");
        }

        /// <summary>The host's next step after HOST on the title screen: pick the save to play.</summary>
        internal static bool HostWaitingForSave =>
            _hostingHint && GameScreen.AtTitle && ModRuntime.Network != null && ModRuntime.Network.Role == NetworkRole.Host;

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
                _joinPending = false;
                _handshakeAt = Time.realtimeSinceStartup;
                _loggedWaitingWorld = false;
                _worldRequest10sSent = false;
                _worldRequest25sSent = false;
                ModLog.Event(LogCat.Session,
                    "Connected to host — waiting for host fully in-world, then world share / auto-load…");
                return;
            }

            if (net.Role == NetworkRole.Host)
            {
                _joinPending = false;
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
                Flash(steamJoin
                    ? "Could not reach the host through Steam"
                    : "Could not reach the host — check the address, port and password");
                ModLog.Event(LogCat.Session,
                    wasTimeout
                        ? (steamJoin
                            ? "Steam join timeout — lobby id / password / proto / friends, or SNS relay."
                            : "Join timeout — check IP/port/password (and firewall).")
                        : "Connection closed.");
            }
        }

        private static void PollPostHandshakeWorldWait()
        {
            var net = ModRuntime.Network;
            if (net == null || net.Role != NetworkRole.Client || !net.IsHandshakeComplete)
                return;
            if (!GameScreen.AtTitle)
            {
                // In-world: the next title visit gets a fresh wait window, not a stale one.
                _handshakeAt = 0f;
                _loggedWaitingWorld = false;
                _worldRequest10sSent = false;
                _worldRequest25sSent = false;
                return;
            }

            if (_handshakeAt <= 0f)
            {
                // Invite / launch-lobby joins never ran PollJoinState: anchor the wait here.
                _handshakeAt = Time.realtimeSinceStartup;
                _loggedWaitingWorld = false;
                _worldRequest10sSent = false;
                _worldRequest25sSent = false;
                ModLog.Event(LogCat.Session,
                    "Connected to host — waiting for host fully in-world, then world share / auto-load…");
                return;
            }

            var share = net.WorldSaveShare;
            if (share != null
                && (share.IsAwaitingSlotPick || share.IsAwaitingEnterWorld || share.IsClientReceivingOrApplying))
                return;

            float waited = Time.realtimeSinceStartup - _handshakeAt;
            bool receiving = IsShareProgressActive(net);

            if (!_loggedWaitingWorld && waited > 8f && !receiving)
            {
                _loggedWaitingWorld = true;
                ModLog.Warn(LogCat.Session,
                    "Still on title 8s after handshake with no world download. "
                    + (net.ClientSeesHostWorldReady
                        ? "Host announced ready — waiting for world package. "
                        : "Host not fully in-world yet (loading / title). ")
                    + "Auto WorldRequest at 10s/25s; or Connect again / host Send world again.");
            }

            if (!receiving && waited >= 10f && !_worldRequest10sSent)
            {
                _worldRequest10sSent = true;
                net.RequestHostWorld("title-wait-10s");
            }
            else if (!receiving && waited >= 25f && !_worldRequest25sSent)
            {
                _worldRequest25sSent = true;
                net.RequestHostWorld("title-wait-25s");
            }
        }

        private static bool IsShareProgressActive(LanNetworkManager net)
        {
            var share = net?.WorldSaveShare;
            if (share == null)
                return false;
            if (share.IsClientReceivingOrApplying || share.IsAwaitingSlotPick || share.IsAwaitingEnterWorld)
                return true;
            string prog = share.ProgressText ?? "";
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

        /// <summary>
        /// What the client is waiting for, in plain words (the Join and Multiplayer screens show it).
        /// Null when there is nothing to say.
        /// </summary>
        internal static string JoinStatusLine()
        {
            var net = ModRuntime.Network;
            if (net == null)
                return null;
            if (_joinPending)
                return _joinViaSteam || net.IsSteamSession ? "Connecting through Steam…" : "Connecting…";
            if (net.Role != NetworkRole.Client)
                return null;
            if (!net.IsHandshakeComplete)
                return net.IsSteamSession ? "Connecting through Steam…" : "Connecting…";
            var share = net.WorldSaveShare;
            if (share != null)
            {
                if (share.HasTerminalShareFailure)
                    return string.IsNullOrEmpty(share.ProgressText) ? "The host's world could not be copied" : share.ProgressText;
                if (share.IsAwaitingSlotPick)
                    return "Choose the profile that keeps the host's world";
                if (share.IsAwaitingEnterWorld)
                    return "The host's world is ready";
                if (!string.IsNullOrEmpty(share.ProgressText))
                    return share.ProgressText;
            }
            if (!GameScreen.AtTitle)
                return "Connected";
            return net.ClientSeesHostWorldReady
                ? "Connected — asking the host for the world…"
                : "Connected — waiting for the host to enter the game…";
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
                if (!GameScreen.AtTitle)
                    return;
            }
            catch { return; }

            _launchLobbyTried = true;
            net.TryConsumePendingSteamLaunchLobby();
        }
    }
}
