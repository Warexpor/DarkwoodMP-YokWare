using System;
using DWMPHorde.Logging;
using DWMPHorde.Networking;
using UnityEngine;

namespace DWMPHorde
{
    // Join-flow bookkeeping and the transient HOST/JOIN failure label.
    public static partial class MainMenuMultiplayerInject
    {
        private static void BeginJoinTimer()
        {
            _joinPending = true;
            _joinStartedAt = Time.realtimeSinceStartup;
            _handshakeAt = 0f;
            _loggedWaitingWorld = false;
            _worldRequest10sSent = false;
            _worldRequest25sSent = false;
        }

        /// <summary>Forget every join-flow static (session ended or never started).</summary>
        private static void ClearJoinState()
        {
            _joinPending = false;
            _handshakeAt = 0f;
            _loggedWaitingWorld = false;
            _worldRequest10sSent = false;
            _worldRequest25sSent = false;
        }

        /// <summary>
        /// Steam invite / launch-lobby connects start outside the JOIN button, so they never got
        /// the join timeout. Adopt them into the same state machine while on the title screen
        /// (in-world connects are soft reconnects / host migration and keep their own retry logic).
        /// </summary>
        private static void TrackExternalJoin()
        {
            var net = ModRuntime.Network;
            if (net == null)
                return;

            if (net.Role == NetworkRole.Offline)
            {
                // PollJoinState reports a failed JOIN-button attempt first; clear once it is done.
                if (!_joinPending)
                    ClearJoinState();
                return;
            }

            if (_joinPending || net.Role != NetworkRole.Client || net.IsHandshakeComplete)
                return;
            if (!Core.mainMenu || Core.loadingGame)
                return;

            _joinViaSteam = net.IsSteamSession;
            BeginJoinTimer();
            SetJoinProgress(_joinViaSteam ? "STEAM…" : "CONNECTING…");
            ModLog.Event(LogCat.Session,
                "Connect started outside the JOIN button (" + (_joinViaSteam ? "Steam invite/launch lobby" : "LAN")
                + ") — join timeout armed.");
        }

        // ------------------------------------------------------------------
        // Transient failure label (HOST/JOIN failures used to be log-only)
        // ------------------------------------------------------------------

        private const float FailureLabelSec = 4f;
        private static GameObject _failBtn;
        private static string _failRestore;
        private static float _failUntil; // process-scoped: menu label timer

        private static bool FailureLabelActive =>
            _failBtn != null && _failBtn && Time.realtimeSinceStartup < _failUntil;

        private static string FailureLabel(string status, string fallback)
        {
            if (string.IsNullOrEmpty(status))
                return fallback;
            if (status.IndexOf("bind", StringComparison.OrdinalIgnoreCase) >= 0)
                return "PORT IN USE";
            if (status.IndexOf("unavailable", StringComparison.OrdinalIgnoreCase) >= 0)
                return "STEAM NOT READY";
            if (status.IndexOf("Invalid", StringComparison.OrdinalIgnoreCase) >= 0)
                return "BAD LOBBY ID";
            if (status.IndexOf("lobby", StringComparison.OrdinalIgnoreCase) >= 0)
                return "LOBBY MISSING";
            return fallback;
        }

        private static void ShowTransientFailure(GameObject button, string text, string restoreText)
        {
            if (button == null || !button)
                return;
            ClearFailureLabel();
            _failBtn = button;
            _failRestore = restoreText;
            _failUntil = Time.realtimeSinceStartup + FailureLabelSec;
            SetLabel(button, text);
        }

        private static void ClearFailureLabel()
        {
            if (_failBtn != null && _failBtn)
                SetLabel(_failBtn, _failRestore);
            _failBtn = null;
            _failRestore = null;
        }

        /// <summary>Called from RefreshSessionButtons: put the label back once the timer is up.</summary>
        private static void ExpireFailureLabel()
        {
            if (_failBtn == null)
                return;
            if (_failBtn && Time.realtimeSinceStartup < _failUntil)
                return;
            ClearFailureLabel();
        }
    }
}
