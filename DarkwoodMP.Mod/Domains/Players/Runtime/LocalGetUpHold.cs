using DWMPHorde.Logging;
using DWMPHorde.Networking;
using UnityEngine;

namespace DWMPHorde.Players
{
    /// <summary>
    /// This player's stand-up waits for the others to be able to see it. Vanilla starts the
    /// get-up clip (<c>Sleep</c> after a load or a respawn, <c>GetUpFromBed</c>; 2.9 s) the
    /// moment the load ends, but this player's position and clip only reach the others once
    /// the session is connected, past its handshake and out of its loading mutes, and a
    /// joiner's stand-in is only made when the first of those packets lands. The watcher got
    /// the clip from wherever it was by then: popping in half risen and on its feet at once.
    /// <para>
    /// So the clip is held on its first frame, lying down, until a few position packets
    /// carrying it have gone out (and, for a joiner, until their character backup has put
    /// them where they left), and runs from there: the watchers' stand-ins start it at
    /// the same frame (<c>SecondPlayerAnimController.IsGetUpClip</c>) and both finish
    /// together. With nobody to watch (no session, or a host alone) nothing is held; a
    /// session that cannot send, or a placement that does not come, lets go after
    /// <see cref="MaxHoldSec"/>.
    /// </para>
    /// </summary>
    internal static class LocalGetUpHold
    {
        /// <summary>Packets (30 a second) that carried the held clip before it runs: the first makes a joiner's stand-in, the rest cover a lost one.</summary>
        private const int PacketsBeforeRelease = 4;
        private const float MaxHoldSec = 6f;

        private static bool _holding; // process-scoped: cleared by Tick whenever no stand-up is running
        private static bool _handled; // process-scoped: cleared by Tick whenever no stand-up is running
        private static int _sent; // process-scoped: set when a hold begins
        private static float _began; // process-scoped: set when a hold begins

        /// <summary>Every frame, connected or not.</summary>
        internal static void Tick(LanNetworkManager net)
        {
            Player p = Player.Instance;
            tk2dSpriteAnimator torso = p != null ? p.torsoAnimator : null;
            string clip = torso != null && torso.CurrentClip != null ? torso.CurrentClip.name : null;
            bool gettingUp = p != null && p.alive && (p.endingSleep || p.gettingUpFromBed)
                && SecondPlayerAnimController.IsGetUpClip(clip);
            if (!gettingUp)
            {
                if (_holding)
                    Release(torso);
                _handled = false;
                return;
            }

            if (!_handled)
            {
                // Once for each stand-up, and only from its start.
                _handled = true;
                if (torso.CurrentFrame <= 1 && HasWatchers(net))
                {
                    _holding = true;
                    _sent = 0;
                    _began = Time.unscaledTime;
                    torso.Pause();
                }
                return;
            }
            if (!_holding)
                return;
            // A joiner is first put where they were when they left (their character backup).
            bool placed = net == null || net.SaveHandlers == null || !net.SaveHandlers.ClientPlacementPending;
            if ((_sent >= PacketsBeforeRelease && placed) || !HasWatchers(net) || Time.unscaledTime - _began > MaxHoldSec)
            {
                if (_sent < PacketsBeforeRelease || !placed)
                    ModLog.Event(LogCat.Session, "[GetUp] stand-up let go after " + (Time.unscaledTime - _began).ToString("0.0") + " s (sent " + _sent + ", placed " + placed + ")");
                Release(torso);
                return;
            }
            if (!torso.Paused)
                torso.Pause();
        }

        /// <summary>A position packet went out with this torso clip.</summary>
        internal static void NoteStateSent(string torsoClip)
        {
            if (!_holding || !SecondPlayerAnimController.IsGetUpClip(torsoClip))
                return;
            // The wait for a connection and the wait to be placed each get their own time.
            if (_sent == 0)
                _began = Time.unscaledTime;
            _sent++;
        }

        private static bool HasWatchers(LanNetworkManager net)
        {
            if (net == null)
                return false;
            if (net.Role == NetworkRole.Client)
                return true;
            return net.Role == NetworkRole.Host && net.IsConnected;
        }

        private static void Release(tk2dSpriteAnimator torso)
        {
            _holding = false;
            _sent = 0;
            if (torso != null && torso.Paused)
                torso.Resume();
        }
    }
}
