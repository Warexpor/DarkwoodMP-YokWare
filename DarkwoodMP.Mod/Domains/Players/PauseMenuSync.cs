using System.Collections.Generic;
using DWMPHorde.Logging;
using DWMPHorde.Networking;
using DWMPHorde.Patches;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Sync
{
    /// <summary>
    /// The in-game pause menu (Esc). Vanilla stops the game while it is open (<c>MainMenu.OnEnable</c>
    /// → <c>Core.pause</c>). In co-op that froze the host's whole world for every player when the host
    /// opened it, and froze a client's own view while the world went on. Now the menu pauses nothing
    /// by itself: the player in it is shielded like in a dialogue (<see cref="MenuShield"/>), and the
    /// world pauses only when every player in the session has the menu open
    /// (<see cref="CoopPausePolicy"/>). Clients report their menu to the host
    /// (<see cref="NetMessageType.PauseMenuState"/>); the host decides and tells everyone
    /// (<see cref="NetMessageType.WorldPause"/>). The first player to close the menu resumes their own
    /// game at once and the host resumes the rest.
    /// </summary>
    internal static class PauseMenuSync
    {
        private const float KeepaliveSec = 2f;
        private const float HostEvalSec = 0.1f;

        private static readonly Dictionary<int, bool> _peerOpen = new Dictionary<int, bool>(); // reset-in: Reset
        private static bool _reportedOpen; // reset-in: Reset
        private static float _lastReportAt = -999f; // reset-in: Reset
        private static float _nextHostEval; // reset-in: Reset
        private static bool _hostWorldPaused; // reset-in: Reset
        private static bool _appliedPause; // reset-in: Reset

        public static void Reset()
        {
            ReleaseLocal();
            _appliedPause = false;
            _peerOpen.Clear();
            _reportedOpen = false;
            _lastReportAt = -999f;
            _nextHostEval = 0f;
            _hostWorldPaused = false;
        }

        /// <summary>The world is paused here because every player is in the pause menu.</summary>
        internal static bool WorldPaused => _appliedPause;

        /// <summary>Every frame (unscaled time: a paused machine still runs it).</summary>
        internal static void Tick(LanNetworkManager net)
        {
            if (net == null || !net.IsConnected)
            {
                if (_appliedPause)
                    ReleaseLocal();
                return;
            }

            bool open = GameScreen.InPauseMenu;
            if (!open && _appliedPause)
                ReleaseLocal();

            float now = Time.unscaledTime;
            if (net.Role == NetworkRole.Client)
            {
                if (!net.IsHandshakeComplete)
                    return;
                if (open != _reportedOpen || (open && now - _lastReportAt >= KeepaliveSec))
                {
                    _reportedOpen = open;
                    _lastReportAt = now;
                    var msg = new PauseMenuStateMessage { Open = open };
                    net.Broadcast(NetMessageType.PauseMenuState, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
                }
                return;
            }

            if (net.Role == NetworkRole.Host && now >= _nextHostEval)
            {
                _nextHostEval = now + HostEvalSec;
                EvaluateHost(net, open);
            }
        }

        private static void EvaluateHost(LanNetworkManager net, bool hostOpen)
        {
            int peers = 0, peersInMenu = 0;
            foreach (int id in net.EnumeratePeerIds())
            {
                peers++;
                if (_peerOpen.TryGetValue(id, out bool o) && o)
                    peersInMenu++;
            }
            bool paused = CoopPausePolicy.WorldPaused(hostOpen, peers, peersInMenu);
            if (paused == _hostWorldPaused)
                return;
            _hostWorldPaused = paused;
            if (paused)
                ApplyLocal();
            else
                ReleaseLocal();
            ModLog.Event(LogCat.Session, paused
                ? "[PauseMenu] every player is in the pause menu: world paused"
                : "[PauseMenu] a player left the pause menu: world resumed");
            var msg = new WorldPauseMessage { Paused = paused };
            net.SendToAll(NetMessageType.WorldPause, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
        }

        /// <summary>Host: a client's pause menu opened or closed.</summary>
        internal static void HandleState(LanNetworkManager net, PauseMenuStateMessage msg)
        {
            if (net == null || net.Role != NetworkRole.Host)
                return;
            int id = net.CurrentReceivePlayerId;
            if (id <= 0)
                return;
            _peerOpen[id] = msg.Open;
            _nextHostEval = 0f;
        }

        /// <summary>Client: the host paused or resumed the world.</summary>
        internal static void HandleWorldPause(LanNetworkManager net, WorldPauseMessage msg)
        {
            if (net == null || net.Role != NetworkRole.Client)
                return;
            if (msg.Paused)
            {
                // Closed the menu while this was on its way: the host hears so and resumes.
                if (GameScreen.InPauseMenu)
                    ApplyLocal();
            }
            else
            {
                ReleaseLocal();
            }
        }

        /// <summary>Host: a peer left; it no longer holds the world in or out of the pause.</summary>
        internal static void HostPeerLeft(int playerId)
        {
            _peerOpen.Remove(playerId);
            _nextHostEval = 0f;
        }

        /// <summary>
        /// Vanilla's pause, keeping the menu music the menu started after its own (suppressed) pause.
        /// Skipped when something else already paused the game; that pause is not ours to lift.
        /// </summary>
        private static void ApplyLocal()
        {
            if (_appliedPause || Core.Paused)
                return;
            Time.timeScale = 0f;
            Core.Paused = true;
            _appliedPause = true;
            AudioObject menuMusic = Singleton<MainMenu>.Instance != null ? Singleton<MainMenu>.Instance.menuMusic : null;
            try
            {
                List<AudioObject> playing = AudioController.GetPlayingAudioObjects();
                for (int i = 0; i < playing.Count; i++)
                {
                    if (playing[i] != null && playing[i] != menuMusic)
                        playing[i].Pause(0f);
                }
            }
            catch (System.Exception ex)
            {
                ModLog.Warn(LogCat.Audio, "[PauseMenu] pausing audio: " + ex.Message);
            }
        }

        private static void ReleaseLocal()
        {
            if (!_appliedPause)
                return;
            _appliedPause = false;
            Time.timeScale = 1f;
            Core.Paused = false;
            try { AudioController.UnpauseAll(0f); }
            catch (System.Exception ex) { ModLog.Warn(LogCat.Audio, "[PauseMenu] resuming audio: " + ex.Message); }
        }

        /// <summary>In a session with a chapter loaded: the pause menu follows the co-op rules.</summary>
        internal static bool CoopInWorld()
            => PauseSuppression.MultiplayerActive && Core.coreStarted && !Core.loadingGame && Player.Instance != null;
    }

    /// <summary>The pause menu opening does not pause the game in co-op (<see cref="PauseMenuSync"/>).</summary>
    [HarmonyPatch(typeof(MainMenu), "OnEnable")]
    internal static class PauseMenuOpenNoPausePatch
    {
        private static void Prefix(out bool __state)
        {
            __state = Core.mainMenu && PauseMenuSync.CoopInWorld();
            if (__state)
                PauseSuppression.BeginNoPause();
        }

        // Finalizer (not Postfix): OnEnable can throw after Begin → stuck SuppressPause.
        private static void Finalizer(bool __state)
        {
            if (__state)
                PauseSuppression.EndNoPause();
        }
    }

    /// <summary>
    /// Closing the pause menu does not unpause either: the menu's own pause never happened, and a
    /// world pause it shared is lifted by <see cref="PauseMenuSync"/> as soon as the menu is closed.
    /// </summary>
    [HarmonyPatch(typeof(MainMenu), "OnDisable")]
    internal static class PauseMenuCloseNoUnpausePatch
    {
        private static void Prefix(out bool __state)
        {
            __state = Core.mainMenu && PauseMenuSync.CoopInWorld();
            if (__state)
                PauseSuppression.BeginNoUnpause();
        }

        private static void Finalizer(bool __state)
        {
            if (__state)
                PauseSuppression.EndNoUnpause();
        }
    }
}
