using System.Linq;
using DWMPHorde;
using DWMPHorde.Networking;
using DWMPHorde.Spectator;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Suppresses skipDay during night-time first-death (only one player dead).
    /// When both players are dead at night, allows skipDay (=> morning advance).
    /// On suppression, enters spectator mode for the dead player.
    /// </summary>
    [HarmonyPatch(typeof(Controller), "skipDay")]
    public static class NightDeathSkipDayPatch
    {
        private static bool Prefix()
        {
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected)
                return true;

            // Only the host calls skipDay — client always suppresses
            if (ModRuntime.Network.Role != NetworkRole.Host)
            {
                if (DeathStateTracker.LocalNightDeath)
                {
                    ModRuntime.LegacyInfo("[Death] Client suppressing skipDay (only host advances time)");
                    EnterNightDeathSpectator();
                }
                return false;
            }

            if (!DeathStateTracker.LocalNightDeath)
                return true;

            if (DeathStateTracker.AllDeadAtNight)
            {
                ModRuntime.LegacyInfo("[Death] All dead at night — host allowing skipDay");
                return true;
            }

            ModRuntime.LegacyInfo("[Death] Partial night death — host suppressing skipDay, entering spectator");
            EnterNightDeathSpectator();
            return false;
        }

        private static void EnterNightDeathSpectator()
        {
            if (DeathStateTracker.PreventSpectator)
            {
                ModRuntime.LegacyInfo("[Death] PreventSpectator flag set — skipping spectator entry");
                return;
            }
            if (DeathStateTracker.AllDeadAtNight)
            {
                ModRuntime.LegacyInfo("[Death] All dead — skipping spectator (morning incoming)");
                return;
            }

            Player player = Player.Instance;
            if (player == null) return;

            var net = ModRuntime.Network as LanNetworkManager;
            if (net == null) return;

            // Prefer living remotes, stable order by PlayerId (3+ cycling via F4).
            Transform followTarget = net.GetAllProxies()
                .Where(p => p != null && p.GetComponent<CharBase>()?.alive != false)
                .OrderBy(p => p.PlayerId)
                .Select(p => p.transform)
                .FirstOrDefault();

            if (followTarget == null)
            {
                // Everyone else is already dead/gone — do NOT wipe LocalNightDeath;
                // host TryResolveNightMorning (or AllDeadTrigger) will advance day.
                ModRuntime.Log?.LogWarning(
                    "[Death] No living remote for spectator — holding night-death state for morning");
                return;
            }

            SpectatorModeController.EnsureExists();
            var spec = SpectatorModeController.Instance;
            if (spec != null)
            {
                spec.ForceEnter(followTarget);
            }

            DeathStateTracker.LocalBagSynced = true;
        }
    }

    /// <summary>
    /// Suppresses SaveManager.Save() during night-time first-death,
    /// so the death state isn't persisted until both players die.
    /// </summary>
    [HarmonyPriority(Priority.Last)]
    [HarmonyPatch(typeof(SaveManager), "Save")]
    public static class NightDeathSavePatch
    {
        private static bool Prefix()
        {
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected)
                return true;

            // Only the host saves
            if (ModRuntime.Network.Role != NetworkRole.Host)
            {
                if (DeathStateTracker.LocalNightDeath)
                {
                    ModRuntime.LegacyInfo("[Death] Client suppressing Save (only host persists)");
                    return false;
                }
                return true;
            }

            if (!DeathStateTracker.LocalNightDeath)
                return true;

            if (DeathStateTracker.AllDeadAtNight)
            {
                ModRuntime.LegacyInfo("[Death] All dead — host allowing Save");
                return true;
            }

            ModRuntime.LegacyInfo("[Death] First night death — host suppressing Save");
            return false;
        }
    }

    /// <summary>
    /// Postfix on onDeath: if the local player died at night and both are now dead,
    /// notify the remote that morning should advance. If only the remote died,
    /// mark the state and trigger bag spawn.
    /// </summary>
    [HarmonyPriority(Priority.Last)]
    [HarmonyPatch(typeof(Player), "onDeath")]
    public static class NightDeathOnDeathPatch
    {
        private static void Postfix(Player __instance)
        {
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected)
                return;

            var net = ModRuntime.Network as LanNetworkManager;
            if (net == null) return;

            // Only the host decides when to end the night — clients with wrong
            // TotalRemoteCount (only count host peer) could send a false trigger.
            if (net.Role != NetworkRole.Host) return;

            DeathStateTracker.TryResolveNightMorning("host local onDeath");
        }
    }
}
