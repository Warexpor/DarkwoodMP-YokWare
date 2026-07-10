using System.Collections.Generic;
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
        private static bool Prefix(Player __instance)
        {
            if (ModRuntime.Network == null || ModRuntime.Network.Role != NetworkRole.Client)
                return true;
            if (!ModRuntime.Network.IsConnected)
                return true;

            // Shared dream death: skip bag/respawn — but never steal epilogue crawl/cam path.
            if (FinalDreamsceneManager.IsActive
                && (__instance == null || !__instance.inEpilogue))
            {
                ModRuntime.LegacyInfo("[Death] Client died during dream session — handling dream death");
                FinalDreamsceneManager.OnLocalDeathInDream();
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

            return true;
        }
    }
}
