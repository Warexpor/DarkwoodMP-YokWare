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
    /// them where they left), and runs from there: the watchers' stand-ins wait on the
    /// first frames with it and follow its clip time (<c>SecondPlayerAnimController.FollowGetUp</c>),
    /// so both finish together. With nobody to watch (no session, or a host alone) nothing is held; a
    /// session that cannot send, or a placement that does not come, lets go after
    /// <see cref="MaxHoldSec"/>.
    /// </para>
    /// </summary>
    internal static class LocalGetUpHold
    {
        /// <summary>Packets (30 a second) that carried the held clip before it runs: the first makes a joiner's stand-in, the rest cover a lost one.</summary>
        private const int PacketsBeforeRelease = 4;
        private const float MaxHoldSec = 6f;
        private const float MaxFrameSec = 0.1f;

        private static bool _holding; // process-scoped: cleared by Tick whenever no stand-up is running
        private static bool _handled; // process-scoped: cleared by Tick whenever no stand-up is running
        private static int _sent; // process-scoped: set when a hold begins
        private static float _held; // process-scoped: set when a hold begins
        private static float _keepLyingUntil = -1f; // process-scoped: set by KeepLying, bounded by its deadline
        private static bool _keptLying; // process-scoped: cleared by Tick when the keep ends

        /// <summary>
        /// Keeps this player's stand-up on its first frame, lying down, for up to
        /// <paramref name="maxSec"/> (until <see cref="StopKeepingLying"/>). For a stand-up that
        /// waits on a beat of its own, as the new character's chapter title: the others see the
        /// player lie there instead of standing until it starts.
        /// </summary>
        internal static void KeepLying(float maxSec) => _keepLyingUntil = Time.unscaledTime + maxSec;

        /// <summary>The kept stand-up runs (behind the usual hold for the others).</summary>
        internal static void StopKeepingLying() => _keepLyingUntil = -1f;

        /// <summary>Every frame, connected or not.</summary>
        internal static void Tick(LanNetworkManager net)
        {
            Player p = Player.Instance;
            tk2dSpriteAnimator torso = p != null ? p.torsoAnimator : null;
            string clip = torso != null && torso.CurrentClip != null ? torso.CurrentClip.name : null;
            bool gettingUp = p != null && p.alive && (p.endingSleep || p.gettingUpFromBed)
                && SecondPlayerAnimController.IsGetUpClip(clip);
            if (Time.unscaledTime < _keepLyingUntil)
            {
                if (gettingUp)
                {
                    // Kept, not held: the stand-up starts over when the keep ends, and is held for the others then.
                    _keptLying = true;
                    _holding = false;
                    _handled = false;
                    if (torso.CurrentFrame > 0)
                        torso.PlayFromFrame(torso.CurrentClip, 0);
                    if (!torso.Paused)
                        torso.Pause();
                }
                return;
            }
            if (_keptLying)
            {
                _keptLying = false;
                if (torso != null && torso.Paused)
                    torso.Resume();
            }
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
                if (HasWatchers(net))
                {
                    _holding = true;
                    _sent = 0;
                    _held = 0f;
                    // This is the first frame the stand-up is seen, but a load ends on a long
                    // frame, which had already run the clip several frames in: no hold was
                    // taken then, and the others got the stand-up from wherever it was.
                    ModLog.Event(LogCat.Session, "[GetUp] " + clip + " held for the others (was at frame " + torso.CurrentFrame + ")");
                    if (torso.CurrentFrame > 0)
                        torso.PlayFromFrame(torso.CurrentClip, 0);
                    torso.Pause();
                }
                return;
            }
            if (!_holding)
                return;
            // A joiner is first put where they were when they left (their character backup).
            // A long frame (a save, a scene load) counts as a short one: the wait is for packets, which need frames.
            _held += Mathf.Min(Time.unscaledDeltaTime, MaxFrameSec);
            bool placed = net == null || net.SaveHandlers == null || !net.SaveHandlers.ClientPlacementPending;
            if ((_sent >= PacketsBeforeRelease && placed) || !HasWatchers(net) || _held > MaxHoldSec)
            {
                if (_sent < PacketsBeforeRelease || !placed)
                    ModLog.Event(LogCat.Session, "[GetUp] stand-up let go after " + _held.ToString("0.0") + " s (sent " + _sent + ", placed " + placed + ")");
                else
                    ModLog.Event(LogCat.Session, "[GetUp] stand-up runs after " + _held.ToString("0.0") + " s (sent " + _sent + ")");
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
                _held = 0f;
            _sent++;
        }

        private static bool HasWatchers(LanNetworkManager net)
        {
            if (net == null)
                return false;
            if (net.Role == NetworkRole.Client)
                return true;
            // A peer on the title screen or still loading has no stand-in of this player to watch.
            return net.Role == NetworkRole.Host && net.AnyPeerInWorld();
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
