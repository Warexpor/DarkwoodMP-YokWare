using System.Collections;
using DWMPHorde;
using DWMPHorde.Harmony;
using DWMPHorde.Networking;
using DWMPHorde.Sync;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// When the HOST player dies, sends a PlayerDiedMessage to the client
    /// so it can handle bag spawn, proxy cleanup, and night-death tracking. A
    /// permadeath-eligible host death follows the same shared-death rewrite as a
    /// client's (<see cref="SharedPermadeathDeath"/>), so the host never runs a
    /// single-player game-over the clients are not told about.
    /// </summary>
    [HarmonyPatch(typeof(Player), "onDeath")]
    public static class HostDeathSendPatch
    {
        [HarmonyPriority(Priority.Last)]
        private static bool Prefix(Player __instance, ref IEnumerator __result)
        {
            if (LanNetworkManager.IsApplyingRemoteState) return true;
            if (SharedPermadeathDeath.Bypass) return true;
            if (ModRuntime.Network == null || ModRuntime.Network.Role != NetworkRole.Host)
                return true;
            if (!ModRuntime.Network.IsConnected)
                return true;

            // Epilogue crawl / camera pan: vanilla only. Do not fan PlayerDied or
            // DeathStateTracker — that dragged living peers into day/night death paths.
            if (__instance != null && __instance.inEpilogue)
            {
                ModRuntime.LegacyInfo(
                    "[Death] Host epilogue death — vanilla crawl/cam (no PlayerDied fan-out)");
                return true;
            }

            // Shared dream death: skip bag/respawn — but never steal epilogue crawl/cam path.
            // onDeath is IEnumerator; StartCoroutine(null) if Prefix returns false without __result.
            if (FinalDreamsceneManager.IsActive)
            {
                if (FinalDreamsceneManager.IsLocalDead)
                {
                    __result = HarmonyCoroutineUtil.Empty();
                    return false;
                }
                ModRuntime.LegacyInfo("[Death] Host died during dream session — handling dream death");
                FinalDreamsceneManager.OnLocalDeathInDream();
                __result = HarmonyCoroutineUtil.Empty();
                return false;
            }

            var net = ModRuntime.Network;
            if (net == null) return true;

            Vector3 pos = __instance._transform.position;
            bool permadeathEligible = SharedPermadeathDeath.IsPermadeathEligible(__instance);

            // One ready-peer decision for both the rewrite and the morning/party-wipe logic.
            // A peer still loading is not counted: without a ready peer vanilla owns the
            // permadeath outcome and DeathStateTracker must not run a second one.
            bool sharedDeath = net.RemotePlayerCount > 0;
            bool vanillaEndsRun = permadeathEligible && !sharedDeath;
            // A one-life death keeps the player down until morning at any hour.
            bool isNight = DeathStateTracker.IsDownUntilMorning(permadeathEligible, sharedDeath);

            ModRuntime.LegacyInfo($"[Death] Host died at {pos}, isNight={isNight}");

            net.Broadcast(NetMessageType.PlayerDied,
                w => new PlayerDiedMessage
                {
                    PosX = pos.x,
                    PosY = pos.y,
                    PosZ = pos.z,
                    IsNight = isNight,
                    HasDropBag = __instance.Inventory != null && __instance.Inventory.getAllItems().Count > 1,
                    PermadeathEligible = permadeathEligible
                }.Serialize(w),
                DeliveryMethod.ReliableOrdered);

            // Turn off light on remote proxy — player died, torch/flashlight goes out
            net.Broadcast(NetMessageType.PlayerLightState,
                w => new PlayerLightStateMessage { LightOn = false }.Serialize(w),
                DeliveryMethod.ReliableOrdered);

            if (isNight)
            {
                DeathStateTracker.OnLocalNightDeath(pos, permadeathEligible, vanillaEndsRun);
            }
            else
            {
                DeathStateTracker.OnLocalDayDeath();
            }

            IEnumerator shared = SharedPermadeathDeath.TryRewrite(__instance, connected: sharedDeath);
            if (shared != null)
            {
                __result = shared;
                return false;
            }

            return true;
        }
    }
}
