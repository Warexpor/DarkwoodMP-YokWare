using UnityEngine;

namespace DarkwoodMP.GameLogic;

/// <summary>
/// Refcounted co-op world freeze (Horde FreezeTracker) — dreams / shared holds.
/// Uses Core.pause so music/env can keep playing.
/// </summary>
public static class FreezeTracker
{
    private static int _count;

    public static int TotalFreezeCount => _count;
    public static bool IsFrozen => _count > 0;

    public static void AddFreeze()
    {
        if (_count == 0)
        {
            try { Core.pause(keepMusicAndEnviromental: true); }
            catch
            {
                Time.timeScale = 0f;
            }
        }
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
        try
        {
            if (!Core.Paused && Mathf.Approximately(Time.timeScale, 1f))
                return;
            Core.Paused = false;
            Time.timeScale = 1f;
            AudioController.UnpauseAll(1f);
        }
        catch
        {
            Time.timeScale = 1f;
        }
    }
}
