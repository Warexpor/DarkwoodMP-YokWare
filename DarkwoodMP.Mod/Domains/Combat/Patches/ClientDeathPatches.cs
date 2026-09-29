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
    /// night/day death state, and redirects Final Dreamscene deaths to
    /// the dream manager instead of vanilla death handling.
    /// </summary>
    [HarmonyPriority(Priority.Last)]
    [HarmonyPatch(typeof(Player), "onDeath")]
    public static class ClientDeathPatch
    {
        private static bool Prefix(Player __instance, ref IEnumerator __result)
        {
            if (_bypassPermadeathRewrite)
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
            Controller ctrl = Singleton<Controller>.Instance;
            bool isNight = ctrl != null && ctrl.isHardNight && (!Core.isDay() || ctrl.CurrentTime <= ctrl.dayTime + 50f);

            ModRuntime.LegacyInfo($"[Death] Client died at {pos}, isNight={isNight}");

            bool hasItems = __instance.Inventory != null && __instance.Inventory.getAllItems().Count > 1;

            net.Send(NetMessageType.PlayerDied,
                w => new PlayerDiedMessage
                {
                    PosX = pos.x,
                    PosY = pos.y,
                    PosZ = pos.z,
                    IsNight = isNight,
                    HasDropBag = hasItems
                }.Serialize(w),
                DeliveryMethod.ReliableOrdered);

            // Turn off light on remote proxy — player died, torch/flashlight goes out
            net.Send(NetMessageType.PlayerLightState,
                w => new PlayerLightStateMessage { LightOn = false }.Serialize(w),
                DeliveryMethod.ReliableOrdered);

            if (isNight)
            {
                DeathStateTracker.OnLocalNightDeath(pos);
            }
            else
            {
                DeathStateTracker.OnLocalDayDeath();
            }

            var profile = Core.currentProfile;
            if (profile != null
                && PermadeathPolicy.ClientUsesSharedDeath(true, (int)profile.difficulty, __instance.lifes))
            {
                ModRuntime.LegacyInfo(
                    "[Death] Client permadeath — shared respawn instead of local game-over");
                __result = RunSharedDeath(__instance);
                return false;
            }

            return true;
        }

        private static bool _bypassPermadeathRewrite;

        /// <summary>
        /// Run vanilla onDeath with difficulty normal so the nightmare / last-life
        /// branch is skipped, then put the profile difficulty back. Save sees the
        /// real difficulty.
        /// </summary>
        private static IEnumerator RunSharedDeath(Player player)
        {
            var profile = Core.currentProfile;
            var saved = profile.difficulty;
            ClientPermadeathSaveGuard.Arm(saved);
            profile.difficulty = GameProfile.Difficulty.normal;
            _bypassPermadeathRewrite = true;
            IEnumerator inner = null;
            try
            {
                var method = AccessTools.Method(typeof(Player), "onDeath");
                inner = method != null ? method.Invoke(player, null) as IEnumerator : null;
            }
            finally
            {
                _bypassPermadeathRewrite = false;
            }

            if (inner != null)
            {
                while (inner.MoveNext())
                    yield return inner.Current;
            }

            ClientPermadeathSaveGuard.RestoreIfArmed();
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

        internal static void RestoreIfArmed()
        {
            if (!_armed) return;
            _armed = false;
            if (Core.currentProfile != null)
                Core.currentProfile.difficulty = _saved;
        }

        [HarmonyPatch(typeof(SaveManager), "Save")]
        [HarmonyPriority(Priority.First)]
        public static class RestoreDifficultyBeforeSave
        {
            private static void Prefix()
            {
                RestoreIfArmed();
            }
        }
    }
}
