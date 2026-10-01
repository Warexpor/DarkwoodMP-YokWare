using System.Collections;
using System.Collections.Generic;
using DWMPHorde.Harmony;
using DWMPHorde.Networking;
using DWMPHorde.Sync;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Intercepts Player.onDeath on the client: notifies host, tracks
    /// night/day death state, redirects Final Dreamscene deaths to
    /// the dream manager instead of vanilla death handling, and rewrites a
    /// permadeath-eligible death to the shared death (<see cref="SharedPermadeathDeath"/>).
    /// </summary>
    [HarmonyPatch(typeof(Player), "onDeath")]
    public static class ClientDeathPatch
    {
        [HarmonyPriority(Priority.Last)]
        private static bool Prefix(Player __instance, ref IEnumerator __result)
        {
            if (SharedPermadeathDeath.Bypass)
                return true;
            if (ModRuntime.Network == null || ModRuntime.Network.Role != NetworkRole.Client)
                return true;
            if (!ModRuntime.Network.IsConnected)
                return true;

            // Epilogue crawl / camera pan: vanilla only. Do not Send PlayerDied or
            // DeathStateTracker — living peers outside the ending must not get day/night death.
            if (__instance != null && __instance.inEpilogue)
            {
                ModRuntime.LegacyInfo(
                    "[Death] Client epilogue death — vanilla crawl/cam (no PlayerDied fan-out)");
                return true;
            }

            // Shared dream death: skip bag/respawn — but never steal epilogue crawl/cam path.
            // onDeath is IEnumerator; StartCoroutine(null) if Prefix returns false without __result.
            if (FinalDreamsceneManager.IsActive)
            {
                // onDeath re-fires while spectating; manager is one-shot — stay silent.
                if (FinalDreamsceneManager.IsLocalDead)
                {
                    __result = HarmonyCoroutineUtil.Empty();
                    return false;
                }
                ModRuntime.LegacyInfo("[Death] Client died during dream session — handling dream death");
                FinalDreamsceneManager.OnLocalDeathInDream();
                __result = HarmonyCoroutineUtil.Empty();
                return false;
            }

            var net = ModRuntime.Network as LanNetworkManager;
            if (net == null) return true;

            Vector3 pos = __instance._transform.position;
            bool isNight = DeathStateTracker.IsNightDeathWindow();
            bool permadeathEligible = SharedPermadeathDeath.IsPermadeathEligible(__instance);

            ModRuntime.LegacyInfo($"[Death] Client died at {pos}, isNight={isNight}");

            bool hasItems = __instance.Inventory != null && __instance.Inventory.getAllItems().Count > 1;

            net.Send(NetMessageType.PlayerDied,
                w => new PlayerDiedMessage
                {
                    PosX = pos.x,
                    PosY = pos.y,
                    PosZ = pos.z,
                    IsNight = isNight,
                    HasDropBag = hasItems,
                    PermadeathEligible = permadeathEligible
                }.Serialize(w),
                DeliveryMethod.ReliableOrdered);

            // Turn off light on remote proxy — player died, torch/flashlight goes out
            net.Send(NetMessageType.PlayerLightState,
                w => new PlayerLightStateMessage { LightOn = false }.Serialize(w),
                DeliveryMethod.ReliableOrdered);

            if (isNight)
            {
                DeathStateTracker.OnLocalNightDeath(pos, permadeathEligible);
            }
            else
            {
                DeathStateTracker.OnLocalDayDeath();
            }

            IEnumerator shared = SharedPermadeathDeath.TryRewrite(__instance, connected: true);
            if (shared != null)
            {
                __result = shared;
                return false;
            }

            return true;
        }
    }

    /// <summary>Puts the real difficulty back before a death Save writes the profile.</summary>
    internal static class ClientPermadeathSaveGuard
    {
        private static bool _armed;
        private static GameProfile.Difficulty _saved;

        internal static void Arm(GameProfile.Difficulty saved)
        {
            _saved = saved;
            _armed = true;
        }

        /// <summary>Session end: put the real difficulty back and disarm.</summary>
        internal static void Reset()
        {
            if (!_armed) return;
            _armed = false;
            if (Core.currentProfile != null)
                Core.currentProfile.difficulty = _saved;
        }

        internal static void RestoreIfArmed()
        {
            if (!_armed) return;
            _armed = false;
            if (Core.currentProfile != null)
                Core.currentProfile.difficulty = _saved;
        }

        [HarmonyPatch(typeof(SaveManager), "Save")]
        public static class RestoreDifficultyBeforeSave
        {
            [HarmonyPriority(Priority.First)]
            private static void Prefix()
            {
                RestoreIfArmed();
            }
        }
    }
}
