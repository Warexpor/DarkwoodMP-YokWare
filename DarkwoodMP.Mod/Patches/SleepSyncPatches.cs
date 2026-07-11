using DWMPHorde.Networking;
using DWMPHorde.Sync;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Host is sole clock authority. Host sleep → immediate TimeSync.
    /// Client sleep → SleepEndRequest so host can forward-adopt the skip.
    /// </summary>
    [HarmonyPatch(typeof(Player), "onEndSleep")]
    public static class SleepEndSyncPatch
    {
        private static void Postfix(Player __instance)
        {
            if (__instance == null) return;
            if (LanNetworkManager.IsApplyingRemoteState || TraverseHack.ApplyingFromNetwork)
                return;
            // Only the local human player.
            if (Player.Instance != null && __instance != Player.Instance)
                return;

            var net = ModRuntime.Network as LanNetworkManager;
            if (net == null || !net.IsConnected)
                return;

            Controller ctrl = Singleton<Controller>.Instance;
            if (ctrl == null) return;

            if (net.Role == NetworkRole.Host)
            {
                net.SendTimeSyncTo(-1);
                ModRuntime.LegacyInfo(
                    $"[SleepSync] host end sleep → TimeSync day={ctrl.day} time={ctrl.CurrentTime}");
                return;
            }

            // Client: request host adopt post-sleep clock.
            var msg = new SleepEndRequestMessage
            {
                CurrentTime = ctrl.CurrentTime,
                Day = ctrl.day,
                IsAfterNight = ctrl.isAfterNight
            };
            net.Send(NetMessageType.SleepEndRequest,
                w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
            ModRuntime.LegacyInfo(
                $"[SleepSync] client end sleep → SleepEndRequest day={msg.Day} time={msg.CurrentTime}");
        }
    }
}
