using DWMPHorde;
using DWMPHorde.Networking;
using DWMPHorde.Sync;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// When the HOST player dies, sends a PlayerDiedMessage to the client
    /// so it can handle bag spawn, proxy cleanup, and night-death tracking.
    /// </summary>
    [HarmonyPriority(Priority.Last)]
    [HarmonyPatch(typeof(Player), "onDeath")]
    public static class HostDeathSendPatch
    {
        private static bool Prefix(Player __instance)
        {
            if (LanNetworkManager.IsApplyingRemoteState) return true;
            if (ModRuntime.Network == null || ModRuntime.Network.Role != NetworkRole.Host)
                return true;
            if (!ModRuntime.Network.IsConnected)
                return true;

            // Shared dream death: skip bag/respawn — but never steal epilogue crawl/cam path.
            if (FinalDreamsceneManager.IsActive
                && (__instance == null || !__instance.inEpilogue))
            {
                ModRuntime.LegacyInfo("[Death] Host died during dream session — handling dream death");
                FinalDreamsceneManager.OnLocalDeathInDream();
                return false;
            }

            var net = ModRuntime.Network as LanNetworkManager;
            if (net == null) return true;

            Vector3 pos = __instance._transform.position;
            Controller ctrl = Singleton<Controller>.Instance;
            bool isNight = ctrl != null && ctrl.isHardNight && (!Core.isDay() || ctrl.CurrentTime <= ctrl.dayTime + 50f);

            ModRuntime.LegacyInfo($"[Death] Host died at {pos}, isNight={isNight}");

            net.Broadcast(NetMessageType.PlayerDied,
                w => new PlayerDiedMessage
                {
                    PosX = pos.x,
                    PosY = pos.y,
                    PosZ = pos.z,
                    IsNight = isNight,
                    HasDropBag = __instance.Inventory != null && __instance.Inventory.getAllItems().Count > 1
                }.Serialize(w),
                DeliveryMethod.ReliableOrdered);

            // Turn off light on remote proxy — player died, torch/flashlight goes out
            net.Broadcast(NetMessageType.PlayerLightState,
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
