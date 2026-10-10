using DWMPHorde.Logging;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>
    /// A game window out of sight keeps running at full rate during a session. With vsync on
    /// (the game's default), a hidden or unfocused window waits on a frame the desktop never
    /// presents: on this Linux box (Hyprland, XWayland) both games dropped to one frame a second
    /// whenever the player was in the other window, and Unity's frame-time cap then slowed the
    /// game itself to a third of real time. On the host that is the whole world (creatures,
    /// night events, the shared clock) for every client. While unfocused in a session, vsync is
    /// off with a 60 fps cap; focus back (or the session's end) restores the player's setting.
    /// <para>
    /// Focus alone is not enough: with the pause menu open, the Wine client takes focus back
    /// the moment it loses it and then reports "focused" for as long as the player is in the
    /// other window, at one frame a second. So frames that stop being presented count as out
    /// of sight too: several frames in a row, each longer than <see cref="StallSec"/>, with
    /// vsync on. That state ends at the next key or mouse button the game receives (it gets
    /// none while the player is elsewhere) or when focus really goes.
    /// </para>
    /// </summary>
    internal static class BackgroundFrameRate
    {
        private const int BackgroundFps = 60;

        private const float StallSec = 0.5f;
        private const int StallFrames = 3;

        private static bool _stalled; // reset-in: Reset
        private static int _stallFrames; // reset-in: Reset
        private static bool _applied; // reset-in: Reset
        private static int _vsync; // reset-in: Reset
        private static int _target; // reset-in: Reset

        internal static void Tick(LanNetworkManager net)
        {
            bool session = net != null && net.Role != NetworkRole.Offline;
            bool focused = Application.isFocused;
            if (!session || !focused || Input.anyKeyDown)
            {
                _stalled = false;
                _stallFrames = 0;
            }
            else if (!_stalled && !_applied && QualitySettings.vSyncCount > 0 && !Core.loadingGame)
            {
                _stallFrames = Time.unscaledDeltaTime >= StallSec ? _stallFrames + 1 : 0;
                if (_stallFrames >= StallFrames)
                {
                    _stalled = true;
                    ModLog.Event(LogCat.Session, "[Frame] focused but frames are not presented (" + StallFrames
                        + " in a row over " + StallSec.ToString("F1") + " s): treated as out of sight");
                }
            }
            bool want = session && (!focused || _stalled);
            if (want == _applied)
                return;
            if (want)
            {
                _vsync = QualitySettings.vSyncCount;
                _target = Application.targetFrameRate;
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = BackgroundFps;
                _applied = true;
                ModLog.Event(LogCat.Session, $"[Frame] {(focused ? "out of sight" : "unfocused")}: vsync off, {BackgroundFps} fps cap (was vsync={_vsync} target={_target})");
            }
            else
                Reset();
        }

        public static void Reset()
        {
            _stalled = false;
            _stallFrames = 0;
            if (!_applied)
                return;
            _applied = false;
            QualitySettings.vSyncCount = _vsync;
            Application.targetFrameRate = _target;
            ModLog.Event(LogCat.Session, $"[Frame] back in sight: vsync={_vsync} target={_target} restored");
        }
    }
}
