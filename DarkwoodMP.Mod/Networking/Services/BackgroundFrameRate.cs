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
    /// </summary>
    internal static class BackgroundFrameRate
    {
        private const int BackgroundFps = 60;

        private static bool _applied; // reset-in: Reset
        private static int _vsync; // reset-in: Reset
        private static int _target; // reset-in: Reset

        internal static void Tick(LanNetworkManager net)
        {
            bool want = net != null && net.Role != NetworkRole.Offline && !Application.isFocused;
            if (want == _applied)
                return;
            if (want)
            {
                _vsync = QualitySettings.vSyncCount;
                _target = Application.targetFrameRate;
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = BackgroundFps;
                _applied = true;
                ModLog.Event(LogCat.Session, $"[Frame] unfocused: vsync off, {BackgroundFps} fps cap (was vsync={_vsync} target={_target})");
            }
            else
                Reset();
        }

        public static void Reset()
        {
            if (!_applied)
                return;
            _applied = false;
            QualitySettings.vSyncCount = _vsync;
            Application.targetFrameRate = _target;
            ModLog.Event(LogCat.Session, $"[Frame] focused: vsync={_vsync} target={_target} restored");
        }
    }
}
