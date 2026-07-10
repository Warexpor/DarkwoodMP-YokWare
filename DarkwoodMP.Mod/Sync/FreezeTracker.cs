using UnityEngine;

namespace DWMPHorde.Sync
{
    internal static class FreezeTracker
    {
        private static int _count;

        public static int TotalFreezeCount => _count;
        public static bool IsFrozen => _count > 0;

        public static void AddFreeze()
        {
            if (_count == 0)
                Core.pause(keepMusicAndEnviromental: true);
            _count++;
        }

        public static void RemoveFreeze()
        {
            if (_count <= 0) return;
            _count--;
            if (_count == 0)
                ForceUnfreeze();
        }

        public static void Reset()
        {
            if (_count > 0)
            {
                _count = 0;
                ForceUnfreeze();
            }
        }

        private static void ForceUnfreeze()
        {
            if (!Core.Paused && Mathf.Approximately(Time.timeScale, 1f))
                return;
            Core.Paused = false;
            Time.timeScale = 1f;
            AudioController.UnpauseAll(1f);
        }
    }
}
