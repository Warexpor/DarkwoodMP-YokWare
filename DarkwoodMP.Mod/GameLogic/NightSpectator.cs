using DarkwoodMP.Network;
using UnityEngine;

namespace DarkwoodMP.GameLogic;

/// <summary>
/// Thin entry into full <see cref="SpectatorModeController"/> for night death.
/// </summary>
public static class NightSpectator
{
    public static bool IsActive =>
        SpectatorModeController.Instance != null && SpectatorModeController.Instance.IsSpectating
        && DeathStateTracker.LocalNightDeath;

    public static void TryEnterIfNightDead()
    {
        if (!DeathStateTracker.LocalNightDeath) return;
        var manager = NetworkManager.Instance;
        if (manager == null || !manager.IsConnected) return;

        SpectatorModeController.EnsureExists();
        Transform follow = null;
        foreach (var kvp in manager.RemotePlayers)
        {
            if (kvp.Value == null) continue;
            var proxy = kvp.Value.GetComponent<RemotePlayerProxy>();
            if (proxy != null && proxy.IsDead) continue;
            follow = kvp.Value.transform;
            break;
        }
        if (follow == null) return;
        SpectatorModeController.Instance.ForceEnter(follow);
    }

    public static void Exit()
    {
        if (SpectatorModeController.Instance != null && SpectatorModeController.Instance.IsSpectating)
            SpectatorModeController.Instance.ExitAndRespawn();
    }

    public static void Reset() => SpectatorModeController.ResetAll();
}
