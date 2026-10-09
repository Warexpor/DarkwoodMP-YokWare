using System;
using DWMPHorde.Logging;
using DWMPHorde.Networking;
using UnityEngine;

namespace DWMPHorde
{
    // Join-flow bookkeeping and the short-lived status line (a HOST/JOIN that did not work).
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
            ClearFlash();
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

        internal static bool JoinPending => _joinPending;

        /// <summary>
        /// Steam invite / launch-lobby connects start outside the Connect button, so they never got
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
                // PollJoinState reports a failed attempt first; clear once it is done.
                if (!_joinPending)
                    ClearJoinState();
                return;
            }

            if (_joinPending || net.Role != NetworkRole.Client || net.IsHandshakeComplete)
                return;
            if (!GameScreen.AtTitle || Core.loadingGame)
                return;

            _joinViaSteam = net.IsSteamSession;
            BeginJoinTimer();
            MultiplayerScreens.OpenJoinProgress();
            ModLog.Event(LogCat.Session,
                "Connect started outside the Connect button (" + (_joinViaSteam ? "Steam invite/launch lobby" : "LAN")
                + ") — join timeout armed.");
        }

        // ------------------------------------------------------------------
        // Short-lived status line (HOST/JOIN failures, "lobby id copied")
        // ------------------------------------------------------------------

        private const float FlashSec = 6f;
        private static string _flash; // process-scoped: menu status line
        private static float _flashUntil; // process-scoped: menu status line timer

        /// <summary>Show <paramref name="english"/> on the multiplayer screens for a few seconds.</summary>
        internal static void Flash(string english)
        {
            _flash = english;
            _flashUntil = Time.realtimeSinceStartup + FlashSec;
        }

        private static void ClearFlash()
        {
            _flash = null;
        }

        /// <summary>The current short-lived line, or null.</summary>
        internal static string FlashLine =>
            !string.IsNullOrEmpty(_flash) && Time.realtimeSinceStartup < _flashUntil ? _flash : null;

        /// <summary>A failed HOST/JOIN status in plain words.</summary>
        private static string FailureText(string status, string fallback)
        {
            if (string.IsNullOrEmpty(status))
                return fallback;
            if (status.IndexOf("bind", StringComparison.OrdinalIgnoreCase) >= 0)
                return "The port is already in use";
            if (status.IndexOf("unavailable", StringComparison.OrdinalIgnoreCase) >= 0
                || status.IndexOf("not initialized", StringComparison.OrdinalIgnoreCase) >= 0
                || status.IndexOf("not logged on", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Steam is not running";
            if (status.IndexOf("Invalid", StringComparison.OrdinalIgnoreCase) >= 0)
                return "That is not a Steam lobby id";
            if (status.IndexOf("lobby", StringComparison.OrdinalIgnoreCase) >= 0)
                return "The Steam lobby is gone";
            return fallback;
        }
    }
}
