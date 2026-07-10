using System;
using System.Collections.Generic;

namespace DarkwoodMP.Network;

/// <summary>
/// Registry of session cleanup actions. Modules register once at init;
/// <see cref="ResetAll"/> runs on full disconnect so static maps do not leak.
/// Each action is isolated — one throw must not skip the rest.
/// </summary>
public static class NetworkResetRegistry
{
    private static readonly List<Action> _resets = new List<Action>();

    public static void Register(Action reset)
    {
        if (reset != null && !_resets.Contains(reset))
            _resets.Add(reset);
    }

    public static void Unregister(Action reset)
    {
        if (reset != null)
            _resets.Remove(reset);
    }

    public static void ResetAll()
    {
        for (int i = 0; i < _resets.Count; i++)
        {
            try
            {
                _resets[i]?.Invoke();
            }
            catch (Exception ex)
            {
                ModLogger.Error($"[Reset] NetworkResetRegistry action failed: {ex.Message}");
            }
        }
        ModLogger.Msg($"[Reset] NetworkResetRegistry.ResetAll ({_resets.Count} actions)");
    }
}
